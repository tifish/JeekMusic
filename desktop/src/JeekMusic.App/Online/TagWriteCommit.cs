using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using JeekMusic.Infrastructure;

namespace JeekMusic.Online;

/// <summary>Windows no-clobber name handoff. There is no path-based atomic compare-and-
/// replace primitive: reserve the verified object by handle, then atomically rename the
/// candidate WITHOUT replacement. A competing new path always wins, never gets overwritten.
/// The brief name gap is covered by a durable write-ahead recovery mapping.</summary>
internal static class TagWriteCommit
{
    internal static readonly Lock Gate = new();
    internal readonly record struct Stamp(uint Volume, ulong FileId, ulong Length, ulong Modified, ulong Created);
    // Per-operation observer/IO seam, no global hook or developer-only runtime switch.
    internal sealed record Operations(Action<string>? Observe = null, Action? BeforeCandidateRename = null, Action<int>? Delay = null);

    [StructLayout(LayoutKind.Sequential)]
    struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Modified;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, IntPtr information, uint size);
    static IOException Failure(string operation) => new(operation, new Win32Exception(Marshal.GetLastPInvokeError()));
    static SafeFileHandle Open(string path)
    {
        // DELETE on our handle allows handle-based rename; excluding SHARE_WRITE and
        // SHARE_DELETE denies ordinary competing writes/renames while the object is held.
        var handle = CreateFileW(path, 0x80010000, 1, IntPtr.Zero, 3, 0x00200080 /* open reparse object, not its target */, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw Failure("Cannot acquire tag commit guard"); }
        return handle;
    }
    internal static Stamp Capture(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw Failure("Cannot read file identity");
        static ulong Time(System.Runtime.InteropServices.ComTypes.FILETIME time) => (ulong)(uint)time.dwHighDateTime << 32 | (uint)time.dwLowDateTime;
        return new(info.Volume, (ulong)info.IndexHigh << 32 | info.IndexLow, (ulong)info.SizeHigh << 32 | info.SizeLow, Time(info.Modified), Time(info.Created));
    }
    static byte[] Hash(SafeFileHandle handle)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long position = 0;
        while (true) { var n = RandomAccess.Read(handle, buffer, position); if (n == 0) break; hash.AppendData(buffer, 0, n); position += n; }
        return hash.GetHashAndReset();
    }
    static void Rename(SafeFileHandle handle, string path)
    {
        var text = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(path));
        var rootOffset = IntPtr.Size == 8 ? 8 : 4; var lengthOffset = rootOffset + IntPtr.Size; var nameOffset = lengthOffset + 4;
        // Win32's FILE_RENAME_INFO requires a trailing WCHAR NUL in addition to its
        // byte-count field (the Win32 wrapper normalizes the full path).
        var size = nameOffset + text.Length + 2; var buffer = Marshal.AllocHGlobal(size);
        try
        {
            // FILE_RENAME_INFO: ReplaceIfExists/Flags == 0. In particular NO POSIX
            // replace flag is used to bypass another handle's sharing protections.
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, lengthOffset, text.Length); Marshal.Copy(text, 0, buffer + nameOffset, text.Length);
            if (!SetFileInformationByHandle(handle, 3 /* FileRenameInfo */, buffer, (uint)size)) throw Failure("No-replace tag rename refused");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    static void DeleteOwned(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(1);
        try { Marshal.WriteByte(buffer, 1); if (!SetFileInformationByHandle(handle, 4 /* FileDispositionInfo */, buffer, 1)) throw Failure("Cannot discard reserved original"); }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    static void Verify(SafeFileHandle file, Stamp expected, byte[] hash, string name)
    {
        if (Capture(file) != expected || !Hash(file).AsSpan().SequenceEqual(hash)) throw new IOException(name + " identity/signature/SHA256 changed before tag commit");
    }
    internal static void Commit(string path, string candidate, Stamp expected, byte[] hash, Stamp candidateIdentity, byte[] candidateHash, Operations? operations = null)
    {
        operations?.Observe?.Invoke("before-guard");
        using var original = Open(path); using var staged = Open(candidate);
        void VerifyBoth()
        {
            Verify(original, expected, hash, "Source"); Verify(staged, candidateIdentity, candidateHash, "Candidate");
        }
        VerifyBoth();
        var metadata = TagWriteMetadata.Read(path, original, checkShortName: true);
        if (metadata.Digest != TagWriteMetadata.Read(candidate, staged).Digest) throw new IOException("Candidate filesystem streams/security/attributes differ from source");
        operations?.Observe?.Invoke("verified-guard");
        var reserved = Path.Combine(Path.GetDirectoryName(candidate)!, Guid.NewGuid().ToString("N") + ".original");
        var journal = TagWriteRecovery.Persist(new(1, Path.GetFullPath(path), Path.GetFullPath(candidate), reserved, expected, Convert.ToHexString(hash), candidateIdentity, Convert.ToHexString(candidateHash), metadata.Digest));
        operations?.Observe?.Invoke("journal-flushed");
        var moved = false; var committed = false;
        try
        {
            Rename(original, reserved); moved = true; operations?.Observe?.Invoke("source-reserved");
            for (var attempt = 0; ; attempt++)
            {
                // Normal handoff has NO full hash/metadata work in the pathname gap.
                // Both exact objects remain guarded. Recheck after any retry wait.
                if (attempt > 0) VerifyBoth();
                if (Exists(path)) throw new IOException("External source path appeared during tag commit; original retained at " + reserved);
                operations?.Observe?.Invoke("before-candidate-rename");
                try
                {
                    operations?.BeforeCandidateRename?.Invoke(); Rename(staged, path); committed = true;
                    operations?.Observe?.Invoke("candidate-renamed"); break;
                }
                catch (IOException e) when (attempt < 2 && (e.InnerException is Win32Exception { NativeErrorCode: 32 or 1175 } || (e.HResult & 0xffff) is 32 or 1175))
                {
                    operations?.Observe?.Invoke("retry-wait");
                    if (operations?.Delay is { } delay) delay(25 * (attempt + 1)); else Thread.Sleep(25 * (attempt + 1));
                }
            }
        }
        finally
        {
            if (committed)
            {
                try { DeleteOwned(original); operations?.Observe?.Invoke("original-discarded"); File.Delete(journal); }
                catch (IOException e) { Log.Warn("tagwriter", "Tag committed; old original retained at " + reserved, e); }
            }
            else if (moved)
            {
                try { Rename(original, path); File.Delete(journal); }
                catch (IOException e) { Log.Warn("tagwriter", "Commit aborted; never overwrite external path; original retained at " + reserved, e); }
            }
        }
    }

    internal static void Recover(TagWriteJournal journal)
    {
        bool Matches(string path, Stamp identity, string hash, bool remove = false)
        {
            if (!Exists(path)) return false;
            using var file = Open(path);
            Verify(file, identity, Convert.FromHexString(hash), "Recovery object");
            if (TagWriteMetadata.Read(path, file).Digest != journal.MetadataHash) throw new IOException("Recovery filesystem metadata changed: " + path);
            if (remove) DeleteOwned(file);
            return true;
        }
        // Check an existing public name before doing ANY write. A different identity
        // (even with equal bytes) is an external conflict, never a restore target.
        if (Exists(journal.Source))
        {
            using var current = Open(journal.Source);
            var id = Capture(current);
            if (id != journal.SourceIdentity && id != journal.CandidateIdentity) throw new IOException("External file conflicts with pending tag transaction: " + journal.Source);
            Verify(current, id, Convert.FromHexString(id == journal.SourceIdentity ? journal.SourceHash : journal.CandidateHash), "Recovery public object");
            if (TagWriteMetadata.Read(journal.Source, current).Digest != journal.MetadataHash) throw new IOException("Recovery public metadata changed");
            if (Exists(journal.Original)) Matches(journal.Original, journal.SourceIdentity, journal.SourceHash, true);
        }
        else
        {
            // Restore the exact complete old object, not a reconstructed audio stream.
            using var old = Open(journal.Original);
            Verify(old, journal.SourceIdentity, Convert.FromHexString(journal.SourceHash), "Recovery original");
            if (TagWriteMetadata.Read(journal.Original, old).Digest != journal.MetadataHash) throw new IOException("Recovery original metadata changed");
            Rename(old, journal.Source); // NO replacement: a racing new path wins.
        }
        if (Exists(journal.Candidate)) Matches(journal.Candidate, journal.CandidateIdentity, journal.CandidateHash, true);
    }
}
