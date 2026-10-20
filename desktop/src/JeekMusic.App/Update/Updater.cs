using System.Diagnostics;
using JeekMusic.Infrastructure;

namespace JeekMusic.Update;

/// <summary>程序内更新（需求 UP-2，设计 §15.2）。这里是"替换文件"那一步；检查和下载在 UpdateService 里。</summary>
public static partial class Updater
{
    /// <summary>
    /// 由新版本（解压在 update\new\ 里）以 --apply-update &lt;程序目录&gt; &lt;旧进程 ID&gt; 启动：等旧进程退出，
    /// 把自己目录里的文件复制到程序目录（覆盖），启动程序目录里的新版本，然后退出。
    /// </summary>
    public static int ApplyUpdate(string appDir, string pidText)
    {
        try
        {
            if (int.TryParse(pidText, out var pid))
            {
                try { Process.GetProcessById(pid).WaitForExit(15000); } catch (ArgumentException) { }
            }
            var source = AppContext.BaseDirectory;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, file);
                var target = Path.Combine(appDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                for (var attempt = 0; ; attempt++)
                {
                    try { File.Copy(file, target, overwrite: true); break; }
                    catch (IOException) when (attempt < 20) { Thread.Sleep(250); }
                }
            }
            Process.Start(new ProcessStartInfo(Path.Combine(appDir, "JeekMusic.exe")) { UseShellExecute = false, WorkingDirectory = appDir });
            return 0;
        }
        catch (Exception e)
        {
            Log.Error("update", "替换文件失败", e);
            Log.Flush();
            return 1;
        }
    }
}
