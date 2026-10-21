using JeekMusic.Platform;
using Microsoft.Win32;

namespace JeekMusic.App.Tests;

public sealed class FileAssociationsTests
{
    [Fact]
    public void RegisterAndRemovePreserveOtherApplicationsAndDefaults()
    {
        var testRoot = @"Software\JeekMusic.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(testRoot);
            using var extension = root.CreateSubKey(@"Software\Classes\.flac");
            extension.SetValue("", "OtherPlayer.Audio");
            using var others = extension.CreateSubKey("OpenWithProgids");
            others.SetValue("OtherPlayer.Audio", "");
            FileAssociations.Register(root, @"C:\音乐 播放器\JeekMusic.exe");
            using (var command = root.OpenSubKey(FileAssociations.ApplicationKey + @"\shell\open\command"))
                Assert.Equal("\"C:\\音乐 播放器\\JeekMusic.exe\" \"%1\"", command?.GetValue(""));
            Assert.Equal("OtherPlayer.Audio", extension.GetValue(""));
            Assert.Contains(FileAssociations.ProgId, others.GetValueNames());
            // 重复注册可更新解压位置，重复注销也安全。
            FileAssociations.Register(root, @"D:\Player\JeekMusic.exe");
            FileAssociations.Unregister(root);
            FileAssociations.Unregister(root);
            Assert.Equal("OtherPlayer.Audio", extension.GetValue(""));
            Assert.Equal(new[] { "OtherPlayer.Audio" }, others.GetValueNames());
            Assert.Null(root.OpenSubKey(FileAssociations.Capabilities));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(testRoot, throwOnMissingSubKey: false); }
    }
}
