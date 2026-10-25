using System.Diagnostics;
using JeekMusic.Online;
using JeekMusic.Library;
using JeekMusic.Core.Library;

// Separate process so tests kill an actual in-flight writer, never the test host/client.
Environment.SetEnvironmentVariable("JEEKMUSIC_DATA", Path.Combine(Path.GetFullPath(args[1]), "data"));
if (args[0] == "scan")
{
    Directory.CreateDirectory(Path.Combine(args[1], "data"));
    using var db = new LibraryDb(Path.Combine(args[1], "data", "library.db"));
    if (db.Folders().Count == 0) db.AddFolder("synthetic", "\\synthetic\\", Path.GetFullPath(Path.Combine(args[1], "music")) + "\\", "Synthetic");
    var folder = db.Folders().Single();
    var result = new Scanner(db).Scan(folder, new(new(ArtistSplitConfig.Default, false, FileNameFormat.ARTIST_TITLE, null)), null, false, CancellationToken.None);
    Console.WriteLine($"records={db.FileStates(folder.Id).Count} deleted={result.Deleted} failed={result.Failed}");
    return db.FileStates(folder.Id).Count == 1 && result.Deleted == 0 ? 0 : 10;
}
if (args[0] == "crash")
{
    var ok = TagWriter.Write(Path.Combine(args[1], "music", "song.mp3"), null, "[00:01.000]合成测试\n[00:01.000]synthetic translation", new(stage =>
    {
        if (stage != args[2]) return;
        Process.GetCurrentProcess().Kill(); Thread.Sleep(Timeout.Infinite);
    }));
    Console.WriteLine(TagWriter.Diagnostics().ToJsonString());
    return ok ? 20 : 21; // A requested crash boundary was never reached: fail test.
}
if (args[0] == "write") return TagWriter.Write(Path.Combine(args[1], "music", "song.mp3"), null, "retry after crash") ? 0 : 1;
return TagWriteRecovery.RecoverBeforeScan() ? 0 : 1;
