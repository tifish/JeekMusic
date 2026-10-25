using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using JeekMusic.Online;
using JeekMusic.Library;
using JeekMusic.Core.Library;

namespace JeekMusic.App.Tests;

[Collection("Tag writing")]
public sealed class TagWriterTransactionTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetKernelObjectSecurity(SafeFileHandle file, uint information, byte[] descriptor);
    static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "TagWritingFixtures", name);
    static void Case(Action<string, string> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "jeek-transaction-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "music"));
        var path = Path.Combine(root, "music", "song.mp3"); File.Copy(Fixture("id3v24.mp3"), path);
        try { run(root, path); } finally { Directory.Delete(root, true); }
    }
    static string Security(string path) => new FileInfo(path).GetAccessControl(AccessControlSections.All).GetSecurityDescriptorSddlForm(AccessControlSections.All);
    static void Protect(string path)
    {
        var acl = new FileInfo(path).GetAccessControl(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
    static string Candidate(string path) => Assert.Single(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, ".jeekmusic", "tag-writes"), "*.mp3"));
    static (int Code, string Output) Probe(string root, string mode, string? stage = null)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../JeekMusic.TagWriteProbe/bin", configuration, "net10.0-windows10.0.19041.0/JeekMusic.TagWriteProbe.dll"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { dll, mode, root, stage }.Where(x => x is not null)) start.ArgumentList.Add(arg!);
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { p.Kill(); throw new TimeoutException("Isolated transaction probe"); }
        return (p.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }
    [Theory]
    [InlineData("cover")] [InlineData("lyrics")] [InlineData("combined")]
    public void NamedStreamsProtectedDaclOwnerAuditAndAttributesSurvive(string op)
    {
        Case((root, path) =>
        {
            Protect(path); File.WriteAllText(path + ":review.ads", "独立非目标ADS\0binary"); File.WriteAllBytes(path + ":second", [0, 255, 3, 8]);
            File.SetAttributes(path, FileAttributes.Archive | FileAttributes.NotContentIndexed);
            var security = Security(path); var attributes = File.GetAttributes(path); var time = File.GetLastWriteTimeUtc(path); var creation = File.GetCreationTimeUtc(path);
            Assert.True(TagWriter.Write(path, op == "lyrics" ? null : File.ReadAllBytes(Fixture("front.jpg")), op == "cover" ? null : "新歌词"), TagWriter.Diagnostics().ToJsonString());
            Assert.Equal(security, Security(path)); Assert.True(new FileInfo(path).GetAccessControl().AreAccessRulesProtected);
            Assert.Equal("独立非目标ADS\0binary", File.ReadAllText(path + ":review.ads")); Assert.Equal(new byte[] { 0, 255, 3, 8 }, File.ReadAllBytes(path + ":second"));
            Assert.Equal(attributes, File.GetAttributes(path)); Assert.Equal(time, File.GetLastWriteTimeUtc(path)); Assert.Equal(creation, File.GetCreationTimeUtc(path));
        });
    }
    [Fact]
    public void UnprotectedDaclWithoutAutoInheritedBitRetainsExactControlFlagsAndRules()
    {
        Case((root, path) =>
        {
            var raw = new RawSecurityDescriptor(new FileInfo(path).GetAccessControl(AccessControlSections.All).GetSecurityDescriptorBinaryForm(), 0);
            raw.SetFlags(raw.ControlFlags & ~(ControlFlags.DiscretionaryAclAutoInherited | ControlFlags.DiscretionaryAclAutoInheritRequired));
            var descriptor = new byte[raw.BinaryLength]; raw.GetBinaryForm(descriptor, 0);
            using (var handle = CreateFileW(path, 0x40000, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero)) Assert.True(SetKernelObjectSecurity(handle, 4, descriptor), Marshal.GetLastPInvokeError().ToString());
            var before = Security(path); Assert.DoesNotContain("D:AI", before);
            Assert.True(TagWriter.Write(path, null, "new lyrics"), TagWriter.Diagnostics().ToJsonString()); Assert.Equal(before, Security(path));
        });
    }
    [Fact]
    public void NonemptyAuditSaclIsPreservedOrSourceIsSafelyRefused()
    {
        Case((root, path) =>
        {
            var acl = new FileInfo(path).GetAccessControl(AccessControlSections.All);
            acl.AddAuditRule(new FileSystemAuditRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AuditFlags.Success)); new FileInfo(path).SetAccessControl(acl);
            var before = Security(path); Assert.Contains("S:", before); var original = File.ReadAllBytes(path);
            var saved = TagWriter.Write(path, null, "new lyrics"); Assert.Equal(before, Security(path));
            if (!saved) Assert.Equal(original, File.ReadAllBytes(path));
        });
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CandidateModifiedOrReplacedAfterValidationBeforeGuardIsNeverPublished(bool replace)
    {
        Case((root, path) =>
        {
            var original = File.ReadAllBytes(path); var raced = false;
            var operations = new TagWriteCommit.Operations(stage =>
            {
                if (stage != "before-guard") return;
                Task.Run(() =>
                {
                    var candidate = Candidate(path);
                    if (replace) { var other = candidate + ".external"; File.WriteAllText(other, "NOT AUDIO"); File.Move(other, candidate, true); }
                    else File.WriteAllText(candidate, "NOT AUDIO");
                    raced = true;
                }).GetAwaiter().GetResult();
            });
            Assert.False(TagWriter.Write(path, null, "candidate test", operations)); Assert.True(raced); Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal("rejected", TagWriter.Diagnostics()["lastResult"]!.GetValue<string>());
        });
    }
    [Fact]
    public void CandidateGuardDeniesConcurrentWritesAndRenamesAfterItsHash()
    {
        Case((root, path) =>
        {
            var tested = false;
            var operations = new TagWriteCommit.Operations(stage =>
            {
                if (stage != "verified-guard") return;
                Task.Run(() => { var p = Candidate(path); Assert.Throws<IOException>(() => File.WriteAllText(p, "NOT AUDIO")); Assert.Throws<IOException>(() => File.Move(p, p + ".moved")); tested = true; }).GetAwaiter().GetResult();
            });
            Assert.True(TagWriter.Write(path, null, "guarded", operations), TagWriter.Diagnostics().ToJsonString()); Assert.True(tested);
        });
    }
    [Fact]
    public void CandidateReplacedWithEqualBytesAndTimesStillFailsIdentityBinding()
    {
        Case((root, path) =>
        {
            var original = File.ReadAllBytes(path);
            var operations = new TagWriteCommit.Operations(stage =>
            {
                if (stage != "before-guard") return;
                Task.Run(() => { var p = Candidate(path); var other = p + ".external"; File.Copy(p, other); File.SetCreationTimeUtc(other, File.GetCreationTimeUtc(p)); File.SetLastWriteTimeUtc(other, File.GetLastWriteTimeUtc(p)); File.Move(other, p, true); }).GetAwaiter().GetResult();
            });
            Assert.False(TagWriter.Write(path, null, "new lyrics", operations)); Assert.Equal(original, File.ReadAllBytes(path));
        });
    }
    [Fact]
    public void UnsupportedReadOnlyAttributeRefusesWithoutChangingContentOrSecurity()
    {
        Case((root, path) =>
        {
            var original = File.ReadAllBytes(path); var security = Security(path); var time = File.GetLastWriteTimeUtc(path); File.SetAttributes(path, FileAttributes.ReadOnly);
            try { Assert.False(TagWriter.Write(path, null, "new lyrics")); Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(security, Security(path)); Assert.Equal(time, File.GetLastWriteTimeUtc(path)); Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0); }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
        });
    }
    [Theory]
    [InlineData("journal-flushed", false)] [InlineData("source-reserved", false)]
    [InlineData("candidate-renamed", true)] [InlineData("original-discarded", true)]
    public void ActualProcessKillAtEveryCommitBoundaryRecoversBeforeScannerDeletion(string stage, bool published)
    {
        Case((root, path) =>
        {
            Protect(path); File.WriteAllText(path + ":review.ads", "crash ADS"); var original = File.ReadAllBytes(path); var security = Security(path); var mtime = File.GetLastWriteTimeUtc(path); var created = File.GetCreationTimeUtc(path);
            var index = Probe(root, "scan"); Assert.True(index.Code == 0, index.Output);
            var crash = Probe(root, "crash", stage); Assert.True(crash.Code is not (0 or 20 or 21), "exit=" + crash.Code + " " + crash.Output);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "data", "tag-transactions"), "*.json"));
            if (stage == "source-reserved") Assert.False(File.Exists(path));
            var recovered = Probe(root, "scan"); Assert.True(recovered.Code == 0, recovered.Output); Assert.Contains("records=1 deleted=0", recovered.Output);
            Assert.True(File.Exists(path)); if (!published) Assert.Equal(original, File.ReadAllBytes(path)); else Assert.NotEqual(original, File.ReadAllBytes(path));
            Assert.Equal("crash ADS", File.ReadAllText(path + ":review.ads")); Assert.Equal(security, Security(path)); Assert.Equal(mtime, File.GetLastWriteTimeUtc(path)); Assert.Equal(created, File.GetCreationTimeUtc(path));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "data", "tag-transactions"), "*.json"));
            var repeat = Probe(root, "scan"); Assert.True(repeat.Code == 0, repeat.Output); // idempotent
        });
    }
    [Fact]
    public void CrashRecoveryNeverOverwritesExternalPathAndScannerDefers()
    {
        Case((root, path) =>
        {
            var original = File.ReadAllBytes(path); Assert.Equal(0, Probe(root, "scan").Code);
            Assert.True(Probe(root, "crash", "source-reserved").Code is not (0 or 20 or 21));
            File.WriteAllText(path, "EXTERNAL NEW CONTENT");
            var scan = Probe(root, "scan"); Assert.Equal(0, scan.Code); Assert.Contains("failed=1", scan.Output);
            Assert.Equal("EXTERNAL NEW CONTENT", File.ReadAllText(path));
            var reserved = Assert.Single(Directory.GetFiles(Path.Combine(root, "music", ".jeekmusic", "tag-writes"), "*.original")); Assert.Equal(original, File.ReadAllBytes(reserved));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "data", "tag-transactions"), "*.json"));
        });
    }
    [Fact]
    public void DirectProductionWriteRecoversMissingNameAfterProcessKill()
    {
        Case((root, path) =>
        {
            Assert.True(Probe(root, "crash", "source-reserved").Code is not (0 or 20 or 21)); Assert.False(File.Exists(path));
            var retry = Probe(root, "write"); Assert.True(retry.Code == 0, retry.Output); Assert.True(File.Exists(path)); Assert.Empty(Directory.GetFiles(Path.Combine(root, "data", "tag-transactions"), "*.json"));
        });
    }
    [Fact]
    public void TamperedReservedOriginalIsRetainedAndScannerKeepsExistingDatabaseRecord()
    {
        Case((root, path) =>
        {
            Assert.Equal(0, Probe(root, "scan").Code); Assert.True(Probe(root, "crash", "source-reserved").Code is not (0 or 20 or 21));
            var reserved = Assert.Single(Directory.GetFiles(Path.Combine(root, "music", ".jeekmusic", "tag-writes"), "*.original")); File.WriteAllText(reserved, "EXTERNAL RESERVED CHANGE");
            var scan = Probe(root, "scan"); Assert.Equal(0, scan.Code); Assert.Contains("failed=1", scan.Output); Assert.False(File.Exists(path)); Assert.Equal("EXTERNAL RESERVED CHANGE", File.ReadAllText(reserved));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "data", "tag-transactions"), "*.json"));
        });
    }
    [Fact]
    public void LiveScannerCannotObserveAnInProcessCommitNameGap()
    {
        Case((root, path) =>
        {
            using var db = new LibraryDb(Path.Combine(root, "scan.sqlite")); db.AddFolder("synthetic", "\\synthetic\\", Path.GetDirectoryName(path)! + "\\", "Synthetic");
            var folder = db.Folders().Single(); var scanner = new Scanner(db); var builder = new TrackBuilder(new(ArtistSplitConfig.Default, false, FileNameFormat.ARTIST_TITLE, null));
            Assert.Equal(1, scanner.Scan(folder, builder, null, false, CancellationToken.None).Added);
            Task<ScanResult>? scan = null; using var entered = new ManualResetEventSlim();
            var operations = new TagWriteCommit.Operations(stage =>
            {
                if (stage != "source-reserved") return;
                scan = Task.Run(() => { entered.Set(); return scanner.Scan(folder, builder, null, false, CancellationToken.None); });
                Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); Assert.False(scan.IsCompleted);
            });
            Assert.True(TagWriter.Write(path, null, "live scan", operations), TagWriter.Diagnostics().ToJsonString());
            Assert.NotNull(scan); Assert.True(scan.Wait(TimeSpan.FromSeconds(10))); Assert.Equal(0, scan.Result.Deleted); Assert.Single(db.FileStates(folder.Id));
        });
    }
}
