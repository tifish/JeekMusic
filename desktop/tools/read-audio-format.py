"""Independent, read-only Windows endpoint/device/mix format diagnostics (no JeekMusic APIs).

The restore helper is used only by explicitly opted-in, isolated hardware tests.
"""
import ctypes as c
import json
import struct
import uuid


def guid(value):
    return (c.c_byte * 16).from_buffer_copy(uuid.UUID(value).bytes_le)


def method(pointer, index, result, *arguments):
    table = c.cast(pointer, c.POINTER(c.POINTER(c.c_void_p))).contents
    return c.WINFUNCTYPE(result, c.c_void_p, *arguments)(table[index])


def check(hr):
    if hr < 0:
        raise OSError(f"HRESULT 0x{hr & 0xffffffff:08X}")


def describe(data):
    tag, channels, rate, _, _, bits, extra = struct.unpack_from("<HHIIHHH", data)
    extensible = tag == 0xfffe and extra >= 22 and len(data) >= 40
    valid = struct.unpack_from("<H", data, 18)[0] if extensible else bits
    mask = struct.unpack_from("<I", data, 20)[0] if extensible else {1: 4, 2: 3}.get(channels, 0)
    floating = tag == 3 or extensible and uuid.UUID(bytes_le=data[24:40]) == uuid.UUID("00000003-0000-0010-8000-00aa00389b71")
    return dict(sampleRate=rate, containerBits=bits, validBits=valid or bits, channels=channels,
                channelMask=mask, sampleKind="float" if floating else "pcm")


class AudioAccess:
    def __init__(self, device_id=None):
        self.ole = c.OleDLL("ole32")
        self.pointers = []
        self.initialized = self.ole.CoInitializeEx(None, 0)
        try:
            check(self.initialized)
            enumerator = self.create("bcde0395-e52f-467c-8e3d-c4579291692e", "a95664d2-9614-4f35-a746-de8db63617e6")
            self.device = c.c_void_p()
            if device_id:
                check(method(enumerator, 5, c.c_long, c.c_wchar_p, c.POINTER(c.c_void_p))(enumerator, device_id, c.byref(self.device)))
            else:
                check(method(enumerator, 4, c.c_long, c.c_int, c.c_int, c.POINTER(c.c_void_p))(enumerator, 0, 1, c.byref(self.device)))
            self.pointers.append(self.device)
            pointer = c.c_void_p()
            check(method(self.device, 5, c.c_long, c.POINTER(c.c_void_p))(self.device, c.byref(pointer)))
            try:
                self.id = c.wstring_at(pointer)
            finally:
                self.ole.CoTaskMemFree(pointer)
            self.client = c.c_void_p()
            iid = guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2")
            check(method(self.device, 3, c.c_long, c.c_void_p, c.c_uint, c.c_void_p, c.POINTER(c.c_void_p))(
                self.device, c.byref(iid), 1, None, c.byref(self.client)))
            self.pointers.append(self.client)
            self.policy = self.create("870af99c-171d-4f9e-af0d-e63df40c2bc9", "f8679f50-850a-41cf-9c72-430f290290c8")
        except BaseException:
            self.close()
            raise

    def create(self, clsid, iid):
        pointer = c.c_void_p()
        check(self.ole.CoCreateInstance(c.byref(guid(clsid)), None, 1, c.byref(guid(iid)), c.byref(pointer)))
        self.pointers.append(pointer)
        return pointer

    def wave_bytes(self, pointer):
        try:
            extra = struct.unpack_from("<H", c.string_at(pointer, 18), 16)[0]
            return c.string_at(pointer, 18 + extra)
        finally:
            self.ole.CoTaskMemFree(pointer)

    def capture(self):
        pointer = c.c_void_p()
        check(method(self.client, 8, c.c_long, c.POINTER(c.c_void_p))(self.client, c.byref(pointer)))
        mix = self.wave_bytes(pointer)
        pointer = c.c_void_p()
        check(method(self.policy, 4, c.c_long, c.c_wchar_p, c.c_int, c.POINTER(c.c_void_p))(self.policy, self.id, 0, c.byref(pointer)))
        device = self.wave_bytes(pointer)
        return {"endpointId": self.id, "deviceBytes": device, "mixBytes": mix,
                "device": describe(self.property_format()), "policyDevice": describe(device), "mix": describe(mix)}

    def property_format(self):
        class Key(c.Structure):
            _fields_ = [("guid", c.c_byte * 16), ("id", c.c_uint)]
        class Variant(c.Structure):
            _fields_ = [("type", c.c_ushort), ("reserved", c.c_ushort * 3), ("size", c.c_uint),
                        ("padding", c.c_uint), ("data", c.c_void_p)]
        store = c.c_void_p()
        value = Variant()
        try:
            check(method(self.device, 4, c.c_long, c.c_uint, c.POINTER(c.c_void_p))(self.device, 0, c.byref(store)))
            key = Key(guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0)
            check(method(store, 5, c.c_long, c.c_void_p, c.c_void_p)(store, c.byref(key), c.byref(value)))
            if value.type != 65 or not value.data or value.size < 18:
                raise ValueError("PKEY_AudioEngine_DeviceFormat is not a WAVEFORMAT BLOB")
            return c.string_at(value.data, value.size)
        finally:
            self.ole.PropVariantClear(c.byref(value))
            if store:
                method(store, 2, c.c_ulong)(store)

    def restore(self, snapshot):
        endpoint, mix = c.create_string_buffer(snapshot["deviceBytes"]), c.create_string_buffer(snapshot["mixBytes"])
        check(method(self.policy, 6, c.c_long, c.c_wchar_p, c.c_void_p, c.c_void_p)(self.policy, self.id, endpoint, mix))

    def supports(self, rate, valid, container=None, channels=2, mask=3):
        container = container or valid
        align = channels * container // 8
        data = struct.pack("<HHIIHHHHI", 0xfffe, channels, rate, rate * align, align, container, 22, valid, mask)
        data += uuid.UUID("00000001-0000-0010-8000-00aa00389b71").bytes_le
        query = method(self.client, 7, c.c_long, c.c_int, c.c_void_p, c.c_void_p)
        result = query(self.client, 1, c.create_string_buffer(data), None)
        if result == 0:
            return True
        if channels <= 2 and valid == container:
            legacy = struct.pack("<HHIIHHH", 1, channels, rate, rate * align, align, container, 0)
            return query(self.client, 1, c.create_string_buffer(legacy), None) == 0
        return False

    def close(self):
        for pointer in reversed(self.pointers):
            method(pointer, 2, c.c_ulong)(pointer)
        self.pointers.clear()
        if self.initialized >= 0:
            self.ole.CoUninitialize()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def public(snapshot):
    return {key: value for key, value in snapshot.items() if not key.endswith("Bytes")}


if __name__ == "__main__":
    with AudioAccess() as access:
        print(json.dumps(public(access.capture()), indent=2))
