using System;
using System.Collections.Generic;
using System.IO;

namespace JeekMusic.Spike;

/// <summary>
/// 命令行参数：
///   --library &lt;目录&gt;  用真实曲库的文件名（"艺术家 - 标题"）代替合成数据
///   --rows &lt;N&gt;        合成数据的行数（默认 20000）
///   --measure-startup   首帧后等 2 秒记录启动耗时和内存，然后退出
///   --scroll-bench      首帧后自动滚动表格 6 秒，记录帧间隔，然后退出
///   --smtc-test         打开 SMTC，用系统会话管理器检查能否看到本程序并切换播放 / 暂停，然后退出
///   --software          用 CPU 渲染（默认 GPU），对比内存和帧时间
///   --out &lt;文件&gt;      自动测试的结果写到这个文件（key=value）
/// </summary>
sealed class SpikeOptions
{
    public string? Library { get; private set; }
    public int Rows { get; private set; } = 20000;
    public bool MeasureStartup { get; private set; }
    public bool ScrollBench { get; private set; }
    public bool SmtcTest { get; private set; }
    public bool Software { get; private set; }
    public string? Out { get; private set; }

    public bool Automated => MeasureStartup || ScrollBench || SmtcTest;

    public static SpikeOptions Parse(string[] args)
    {
        var o = new SpikeOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--library": o.Library = args[++i]; break;
                case "--rows": o.Rows = int.Parse(args[++i]); break;
                case "--measure-startup": o.MeasureStartup = true; break;
                case "--scroll-bench": o.ScrollBench = true; break;
                case "--smtc-test": o.SmtcTest = true; break;
                case "--software": o.Software = true; break;
                case "--out": o.Out = args[++i]; break;
            }
        }
        return o;
    }
}

/// <summary>自动测试结果，退出前写到 --out 指定的文件。</summary>
static class Report
{
    static readonly List<string> Lines = new();

    public static void Add(string key, object value) => Lines.Add($"{key}={value}");

    public static void Flush()
    {
        if (Program.Options.Out is { } path) File.WriteAllLines(path, Lines);
    }
}
