using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JeekMusic.Online;

/// <summary>Bounded Windows filesystem metadata profile; never infer that an unnamed-stream hash covers a file.</summary>
internal static class TagWriteMetadata
{
    const FileAttributes Supported = FileAttributes.Normal | FileAttributes.Archive | FileAttributes.Hidden |
        FileAttributes.System | FileAttributes.NotContentIndexed | FileAttributes.Temporary;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StreamData { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr FindFirstStreamW(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetShortPathNameW(string path, StringBuilder result, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetKernelObjectSecurity(SafeFileHandle file, uint information, byte[] descriptor);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool FindNextStreamW(IntPtr search, out StreamData data);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool FindClose(IntPtr search);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool BackupRead(SafeFileHandle file, byte[] buffer, uint count, out uint read, bool abort, bool security, ref IntPtr context);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool BackupSeek(SafeFileHandle file, uint low, uint high, out uint actualLow, out uint actualHigh, ref IntPtr context);
    static IOException Failure(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    internal sealed record Snapshot(FileAttributes Attributes, string Security, SortedDictionary<string, string> Streams)
    {
        internal string Digest => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (int)Attributes + "\n" + Security + "\n" + string.Join("\n", Streams.Select(p => p.Key + "=" + p.Value)))));
    }
    // BACKUP_DATA, BACKUP_SECURITY_DATA and BACKUP_ALTERNATE_DATA are the only
    // supported records. Object IDs, EAs, reparse/sparse/TxF data must not vanish.
    static void CheckBackupRecords(SafeFileHandle file)
    {
        IntPtr context = IntPtr.Zero; var header = new byte[20];
        try
        {
            while (true)
            {
                if (!BackupRead(file, header, 20, out var n, false, false, ref context)) throw Failure("Cannot inspect filesystem backup records");
                if (n == 0) break;
                if (n != 20) throw new IOException("Truncated filesystem backup record");
                var id = BitConverter.ToUInt32(header); var size = BitConverter.ToUInt64(header, 8); var name = BitConverter.ToUInt32(header, 16);
                if (id is not (1 or 3 or 4)) throw new NotSupportedException("Unsupported filesystem backup record: " + id);
                if (name > 65536) throw new NotSupportedException("Oversized filesystem stream name");
                if (name != 0 && (!BackupRead(file, new byte[name], name, out var read, false, false, ref context) || read != name)) throw Failure("Cannot inspect filesystem stream name");
                if (!BackupSeek(file, (uint)size, (uint)(size >> 32), out var low, out var high, ref context) || ((ulong)high << 32 | low) != size) throw Failure("Cannot skip filesystem stream");
            }
        }
        finally { BackupRead(file, header, 0, out _, true, false, ref context); }
    }
    internal static Snapshot Read(string path, SafeFileHandle file, bool checkShortName = false)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & ~Supported) != 0) throw new NotSupportedException("Unsupported filesystem attributes: " + attributes);
        if (checkShortName)
        {
            var shortPath = new StringBuilder(32768);
            if (GetShortPathNameW(path, shortPath, 32768) == 0) throw Failure("Cannot inspect DOS filename alias");
            if (!Path.GetFileName(path).Equals(Path.GetFileName(shortPath.ToString()), StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("DOS short-name alias requires native replacement; refuse name handoff");
        }
        CheckBackupRecords(file);
        // Includes owner/group/DACL and audit SACL. An unreadable descriptor is a
        // refusal, not permission to silently substitute inherited staging rules.
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.All).GetSecurityDescriptorSddlForm(AccessControlSections.All);
        var streams = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var search = FindFirstStreamW(path, 0, out var stream, 0);
        if (search == new IntPtr(-1))
        {
            if (Marshal.GetLastPInvokeError() != 38) throw Failure("Cannot enumerate named streams");
        }
        else
        {
            try
            {
                long total = 0;
                do
                {
                    if (stream.Name == "::$DATA") continue;
                    if (!stream.Name.StartsWith(':') || !stream.Name.EndsWith(":$DATA", StringComparison.Ordinal) || stream.Size < 0 || (total += stream.Size) > 32 * 1024 * 1024)
                        throw new NotSupportedException("Unsupported/oversized named stream");
                    using var input = new FileStream(path + stream.Name, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    streams.Add(stream.Name, Convert.ToHexString(SHA256.HashData(input)));
                } while (FindNextStreamW(search, out stream));
                if (Marshal.GetLastPInvokeError() != 38) throw Failure("Cannot finish named stream enumeration");
            }
            finally { FindClose(search); }
        }
        return new(attributes, security, streams);
    }
    internal static string Prepare(string source, string candidate, byte[] expectedContent)
    {
        var template = Path.Combine(Path.GetDirectoryName(candidate)!, Guid.NewGuid().ToString("N") + Path.GetExtension(candidate));
        try
        {
            // CopyFile supplies the original named streams. ReplaceFile performs
            // Windows' native stream/attribute merge at a PRIVATE path, while the
            // public source name is still present. No ignore-ACL/merge-error flags.
            File.Copy(source, template);
            for (var attempt = 0; ; attempt++)
            {
                try { File.Replace(candidate, template, null, false); break; }
                catch (IOException e) when (attempt < 2 && (e.HResult & 0xffff) is 32 or 1175)
                {
                    // A private preparation path only: source is still present and
                    // the guarded commit later revalidates both exact file objects.
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
            var rawDescriptor = new FileInfo(source).GetAccessControl(AccessControlSections.All).GetSecurityDescriptorBinaryForm();
            var descriptor = new FileSecurity(); descriptor.SetSecurityDescriptorBinaryForm(rawDescriptor, AccessControlSections.All);
            new FileInfo(template).SetAccessControl(descriptor);
            // The high-level setter recomputes inheritance bookkeeping. Restore
            // the raw DACL/control bits through the documented kernel-object API;
            // do not substitute the staging parent's inherited rules or add AI.
            if ((new RawSecurityDescriptor(rawDescriptor, 0).ControlFlags & ControlFlags.DiscretionaryAclAutoInherited) == 0)
            {
                // This legacy setter clears AUTO_INHERITED. Use it only when
                // the source itself lacks that bit; otherwise keep the high-level
                // setter's inheritance bookkeeping and verify the complete result.
                using var security = CreateFileW(template, 0x40000 /* WRITE_DAC */, 7, IntPtr.Zero, 3, 0x00200080, IntPtr.Zero);
                if (security.IsInvalid || !SetKernelObjectSecurity(security, 4 /* DACL_SECURITY_INFORMATION */, rawDescriptor)) throw Failure("Cannot preserve exact source DACL");
            }
            File.SetAttributes(template, File.GetAttributes(source));
            using (var check = new FileStream(template, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (!SHA256.HashData(check).AsSpan().SequenceEqual(expectedContent)) throw new IOException("Prepared candidate differs from validated output");
            return template;
        }
        catch { File.Delete(template); throw; }
    }
}
