using JeekMusic.Settings;

namespace JeekMusic.Playback;

/// <summary>所有输出模式切换入口共用同一份音量记录和持久化逻辑。</summary>
internal static class OutputSettingsBinding
{
    public static void Attach(PlayerService player, SettingsStore settings)
    {
        var s = settings.Current;
        player.RestoreVolumeSettings(s.Exclusive, s.Volume, s.SharedVolume, s.ExclusiveVolume, s.Muted);
        player.OutputChanged += () =>
        {
            if (s.Exclusive == player.Exclusive && s.Volume == player.Volume && s.Muted == player.Muted &&
                s.SharedVolume == player.SharedVolume && s.ExclusiveVolume == player.ExclusiveVolume) return;
            s.Exclusive = player.Exclusive;
            s.Volume = player.Volume;
            s.Muted = player.Muted;
            s.SharedVolume = player.SharedVolume;
            s.ExclusiveVolume = player.ExclusiveVolume;
            settings.Save("playback");
        };
    }
}
