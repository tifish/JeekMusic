namespace JeekMusic.Playback;

/// <summary>在歌曲原始采样率下选择设备支持的 mpv 输出样本格式，不打开音频流。</summary>
internal interface IExclusiveAudioFormat
{
    string? Select(string? device, int sampleRate, int channels, int? sourceBits);
}
