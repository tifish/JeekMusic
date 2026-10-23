using JeekMusic.Settings;

namespace JeekMusic.Playback;

/// <summary>只持久化模式和应用静音；端点音量始终由 Windows 提供。</summary>
internal static class OutputSettingsBinding
{
    public static void Attach(PlayerService player, SettingsStore settings)
    {
        var s = settings.Current;
        player.RestoreVolumeSettings(s.Exclusive, s.Muted);
        player.OutputChanged += () =>
        {
            if (s.Exclusive == player.Exclusive && s.Muted == player.Muted) return;
            s.Exclusive = player.Exclusive;
            s.Muted = player.Muted;
            settings.Save("playback");
        };
    }
}
