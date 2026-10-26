"""Isolated production QR/pairing fixture. Never prints invitations, QR bytes or tokens.

Requires Pillow and zxing-cpp for independent decoding, plus the owned JeekLanSync35 AVD.
Commands: qr (create private camera frame), manual (type fresh invitation into focused
Android field), replay (verify consumed QR rejected), expired (after five minutes),
status (redacted), quit. Camera startup uses the printed private directory path only.
No real music roots or daily configuration are changed.
"""
import argparse
import base64
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import uuid
from urllib.parse import urlsplit

from mcp_client import McpClient

spec = importlib.util.spec_from_file_location("lan_fixture", Path(__file__).with_name("test-lan-sync.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
call, wire = fixture.call, fixture.wire


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--exe", type=Path, required=True)
    p.add_argument("--data", type=Path, required=True)
    p.add_argument("--evidence", type=Path, required=True)
    a = p.parse_args()
    assert "artifacts" in a.data.parts and "artifacts" in a.evidence.parts
    a.evidence.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="jeek-qr-private-") as private:
        # Limit credential frame access to the current Windows account.
        subprocess.run(["icacls", private, "/inheritance:r", "/grant:r", f"{os.environ['USERDOMAIN']}\\{os.environ['USERNAME']}:(OI)(CI)F"], check=True, capture_output=True)
        frame = Path(private) / "camera.png"
        print("Private camera frame path: " + str(frame), flush=True)
        with McpClient(a.exe, data=a.data) as client:
            assert Path(call(client, "get_diagnostics")["processPath"]).resolve() == a.exe.resolve()
            call(client, "lan_sync_control", action="start")
            try:
                for bad in ("127.0.0.1", "0.0.0.0", "224.0.0.1", "255.255.255.255"):
                    response = client.call("lan_sync_control", {"action": "invite", "host": bad, "includeQr": True})
                    assert response.get("isError"), "Unsafe phone QR accepted"
                print("PASS: production QR rejects unsafe phone addresses", flush=True)
                stale = call(client, "lan_sync_control", action="invite", host="10.0.2.2")["invitation"]
                issued = time.monotonic()
                last = None
                for line in iter(input, "quit"):
                    cmd = line.strip()
                    if cmd == "qr":
                        from PIL import Image
                        import zxingcpp
                        qr = call(client, "lan_sync_control", action="invite", host="10.0.2.2", includeQr=True)
                        frame.write_bytes(base64.b64decode(qr["qrPngBase64"]))
                        result = zxingcpp.read_barcode(Image.open(frame))
                        assert result and result.text == qr["invitation"], "Independent QR round-trip failed (redacted)"
                        image = Image.open(frame).convert("RGB")
                        image = image.resize((image.width*2//3, image.height*2//3), Image.Resampling.NEAREST)
                        canvas = Image.new("RGB", (640, 480), "white")
                        canvas.paste(image, (24, (480-image.height)//2))
                        canvas.save(frame)
                        last = qr["invitation"]
                        print("PASS: production NativeAOT QR independently decoded; private frame ready", flush=True)
                    elif cmd == "manual":
                        assert subprocess.check_output(["adb", "-s", "emulator-5582", "emu", "avd", "name"]).splitlines()[0].strip() == b"JeekLanSync35"
                        invite = call(client, "lan_sync_control", action="invite", host="10.0.2.2")["invitation"]
                        subprocess.run(["adb", "-s", "emulator-5582", "shell", "input", "text", invite], check=True, capture_output=True)
                        last = invite
                        print("Fresh invitation entered in focused Android UI (redacted)", flush=True)
                    elif cmd == "expiry-begin":
                        stale = call(client, "lan_sync_control", action="invite", host="10.0.2.2")["invitation"]
                        issued = time.monotonic()
                        print("Actual five-minute expiry interval started; do not generate another invitation", flush=True)
                    elif cmd in ("replay", "expired"):
                        invite = stale if cmd == "expired" else last
                        assert invite
                        if cmd == "expired": assert time.monotonic() - issued >= 301, "Wait for actual five-minute expiry"
                        u = urlsplit(invite)
                        pin, secret = u.path.strip("/").split("/")
                        response = wire({"port": u.port, "pin": pin, "deviceId": uuid.uuid4().hex}, "pair", invitation=secret, name="QR rejection test")
                        assert response.get("code") == "invitation_rejected"
                        (a.evidence / f"{cmd}.json").write_text(json.dumps({"rejected": True, "code": response["code"]}))
                        print("PASS: " + cmd + " refused by actual TLS service", flush=True)
                    elif cmd == "status":
                        status = call(client, "lan_sync_status")
                        (a.evidence / "service-status.json").write_text(json.dumps(status, indent=2))
                        print(json.dumps(status), flush=True)
                    elif cmd == "consumed":
                        assert not call(client, "lan_sync_status")["invitationActive"]
                        frame.unlink(missing_ok=True)
                        print("PASS: invitation inactive; credential frame removed", flush=True)
            except EOFError:
                pass
            finally:
                call(client, "lan_sync_control", action="stop")


if __name__ == "__main__":
    main()
