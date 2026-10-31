using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using JeekMusic.UI;

namespace JeekMusic.App.Tests;

[Collection("Avalonia UI")]
public sealed class TypographyTests
{
    [Fact]
    public async Task RealShapingUsesYaHeiForChineseAndKeepsIconsAndMeasuredControls()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(() =>
        {
            var diagnostic = Typography.Diagnostics();
            var yaHeiAvailable = diagnostic["microsoftYaHeiAvailable"]!.GetValue<bool>();
            if (yaHeiAvailable)
            {
                Assert.Equal("Microsoft YaHei", diagnostic["normalFamilies"]![0]!.GetValue<string>());
                Assert.Equal("Microsoft YaHei", diagnostic["boldFamilies"]![0]!.GetValue<string>());
            }
            foreach (var weight in new[] { FontWeight.Normal, FontWeight.Bold })
            {
                using var layout = new TextLayout(Typography.Sample, new Typeface(FontFamily.Default, weight: weight), 14, Brushes.Black);
                foreach (var run in layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>())
                {
                    if (yaHeiAvailable) Assert.Equal("Microsoft YaHei", run.GlyphRun.GlyphTypeface.FamilyName);
                    Assert.All(run.GlyphRun.GlyphInfos, glyph => Assert.NotEqual(0, glyph.GlyphIndex));
                }
            }
            var icons = new Typeface(new FontFamily(Icons.FontFamily)).GlyphTypeface;
            Assert.Contains(icons.FamilyName, new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" });
            foreach (var glyph in new[] { Icons.Play, Icons.Pause, Icons.Settings, Icons.Filter, Icons.Music })
            {
                Assert.True(icons.CharacterToGlyphMap.TryGetGlyph(glyph[0], out var index));
                Assert.NotEqual(0, index);
            }

            var mini = new MiniPlayerWindow { Background = Brushes.White };
            try
            {
                // Use production XAML with representative mixed-script text, no player/audio.
                var labels = mini.GetLogicalDescendants().OfType<TextBlock>().Where(t => !t.Classes.Contains("glyph")).ToArray();
                foreach (var label in labels) label.Text = "中文歌曲 English gyp 123";
                mini.Show();
                foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
                {
                    mini.SetRenderScaling(scale); Pump(mini);
                    Assert.Equal(112, mini.Height);
                    foreach (var label in labels)
                    {
                        if (yaHeiAvailable) Assert.Equal("Microsoft YaHei", new Typeface(label.FontFamily).GlyphTypeface.FamilyName);
                        Assert.True(label.Bounds.Height >= label.TextLayout.Height - .5);
                        var p = label.TranslatePoint(default, mini)!.Value;
                        Assert.True(p.Y >= 0 && p.Y + label.Bounds.Height <= mini.ClientSize.Height + .5);
                    }
                    Save(mini, $"mini-{scale}");
                }
            }
            finally { mini.Close(); }

            var samples = new StackPanel { Spacing = 8, Margin = new Thickness(16) };
            foreach (var size in new[] { 10d, 11, 12, 13, 14, 16, 19, 22 })
                samples.Children.Add(new TextBlock { Text = $"{size}: 中文歌曲歌词 English gyp 123", FontSize = size });
            foreach (var size in new[] { 14d, 30, 50.4, 72 })
                samples.Children.Add(new OutlinedText { Text = "中文歌词 English gyp", FontSize = size });
            var window = new Window { Width = 1000, SizeToContent = SizeToContent.Height, Background = Brushes.White, Content = samples };
            try
            {
                window.Show(); Pump(window);
                foreach (var label in samples.Children.OfType<TextBlock>())
                    Assert.True(label.Bounds.Height >= label.TextLayout.Height - .5);
                foreach (var outlined in samples.Children.OfType<OutlinedText>())
                {
                    var text = new FormattedText(outlined.Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        new Typeface(FontFamily.Default, weight: FontWeight.Bold), outlined.FontSize, Brushes.Black) { MaxTextWidth = outlined.Bounds.Width };
                    var ink = text.BuildGeometry(new Point(0, 2))!.Bounds;
                    var radius = Math.Max(2, outlined.FontSize / 9) / 2;
                    Assert.True(ink.Y - radius >= -.5 && ink.Bottom + radius <= outlined.Bounds.Height + .5,
                        $"Outlined {outlined.FontSize}: ink={ink}, height={outlined.Bounds.Height}, stroke={radius}");
                }
                Save(window, "text-and-outlined-lyrics");
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MainDialogsAndPopupsUseTheTextChainWithoutOverridingIconChildren()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(LayoutRenderingApplication));
        await session.Dispatch(() =>
        {
            using var h = new PlayingVisibilityTests.Harness();
            var before = h.Player.Queue.Items.ToArray();
            var count = h.Tools.Catalog.Count;
            var diagnostic = h.Tools.Call("get_diagnostics", new())["typography"]!;
            Assert.Equal(count, h.Tools.Catalog.Count); Assert.Equal(28, count);
            Assert.False(h.Player.EngineReady); Assert.Equal(before, h.Player.Queue.Items);
            Assert.Equal(Typography.Diagnostics().ToJsonString(), diagnostic.ToJsonString());
            foreach (var label in h.Window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Text is { Length: > 0 } && t.Text.Any(c => c >= '\u4e00' && c <= '\u9fff')))
                AssertText(label);
            SettingsWindow.ShowFor(h.Window);
            var settings = h.Window.OwnedWindows.OfType<SettingsWindow>().Single();
            var lyrics = new LyricsPanel(h.App, h.Vm.Player, h.App.Lyrics);
            var panelWindow = new Window { Width = 360, Height = 600, Content = lyrics, Background = Brushes.White };
            var menu = new ContextMenu { Items = { new MenuItem { Header = "中文菜单 English" } } };
            try
            {
                Pump(settings); panelWindow.Show(); Pump(panelWindow);
                foreach (var label in settings.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text is { Length: > 0 })) AssertText(label);
                foreach (var label in lyrics.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text is { Length: > 0 } && !t.Classes.Contains("glyph")))
                {
                    // Icon buttons deliberately pass their own icon family into template text.
                    if (!label.FontFamily.ToString().Contains("Segoe")) AssertText(label);
                }
                menu.Open(h.Window.FindControl<Button>("SettingsButton")!); Pump(h.Window);
                var item = (MenuItem)menu.Items[0]!;
                var header = item.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "中文菜单 English");
                AssertText(header);
                Save(settings, "settings"); Save(panelWindow, "lyrics-panel");
            }
            finally { menu.Close(); panelWindow.Close(); settings.Close(); }

            static void AssertText(TextBlock text)
            {
                if (FontManager.Current.SystemFonts.Any(f => f.Name == "Microsoft YaHei"))
                    Assert.Equal("Microsoft YaHei", new Typeface(text.FontFamily).GlyphTypeface.FamilyName);
                if (text.IsEffectivelyVisible && text.Bounds.Width > 0)
                    Assert.True(text.Bounds.Height >= text.TextLayout.Height - .5, $"{text.Text}: {text.Bounds}, {text.TextLayout.Height}");
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task MissingPreferredFamilyFallsBackAndStillShapesChineseAndEnglish()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(MissingFontApplication));
        await session.Dispatch(() =>
        {
            using var layout = new TextLayout("中文歌曲 English gyp", new Typeface(FontFamily.Default), 14, Brushes.Black);
            var runs = layout.TextLines.SelectMany(l => l.TextRuns).OfType<ShapedTextRun>().ToArray();
            Assert.NotEmpty(runs);
            Assert.All(runs, run =>
            {
                Assert.DoesNotContain("JeekMissingFont", run.GlyphRun.GlyphTypeface.FamilyName);
                Assert.All(run.GlyphRun.GlyphInfos, glyph => Assert.NotEqual(0, glyph.GlyphIndex));
            });
        }, CancellationToken.None);
    }

    static void Pump(Window window)
    {
        for (var i = 0; i < 5; i++) { window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    internal static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("JEEKMUSIC_TEST_ARTIFACTS") is not { Length: > 0 } path) return;
        Directory.CreateDirectory(path);
        using var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        bitmap.Save(Path.Combine(path, name + ".png"), PngBitmapEncoderOptions.Default);
    }
}

public sealed class MissingFontApplication : LayoutRenderingApplication
{
    public new static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<MissingFontApplication>().UseSkia()
        .With(new FontManagerOptions { DefaultFamilyName = "JeekMissingFont, Segoe UI" })
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
