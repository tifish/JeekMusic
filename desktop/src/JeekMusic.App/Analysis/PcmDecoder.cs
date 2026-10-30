using System.Globalization;
using System.Buffers.Binary;
using System.IO.Pipes;
using JeekMusic.Core.Analysis;
using JeekMusic.Library;
using JeekMusic.Playback;

namespace JeekMusic.Analysis;

/// <summary>独立 mpv 实例把 PCM 写入命名管道，不发声、不写临时音频文件。</summary>
public static class PcmDecoder
{
    public static async Task<float[]> Decode(string path, Track t, CancellationToken ct)
    {
        var result = new List<float>();
        foreach (var (start, end) in PcmMath.Windows(t.StartMs ?? 0, t.DurationMs))
        {
            if (end <= start) continue;
            var pcm = await Window(path, start, end, ct);
            result.AddRange(pcm);
        }
        return result.ToArray();
    }

    internal static async Task<float[]> Window(string path, long startMs, long endMs, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var token = timeout.Token;
        var name = "JeekMusic-pcm-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var mpv = new Mpv(Mpv.BaseOptions().Concat(new[]
        {
            ("ao", "pcm"), ("ao-pcm-file", @"\\.\pipe\" + name), ("ao-pcm-waveheader", "yes"),
            ("audio-format", "float"), ("audio-channels", "auto"),
            ("gapless-audio", "no"), ("pause", "no"),
        }));
        mpv.FileEnded += (_, reason, error) =>
        {
            if (reason == Mpv.EndReason.Error) ended.TrySetException(new IOException(error ?? L.T("解码失败")));
            else ended.TrySetResult();
        };
        var connected = pipe.WaitForConnectionAsync(token);
        try
        {
            mpv.Run("loadfile", path, "replace", "-1", "start=" + (startMs / 1000.0).ToString(CultureInfo.InvariantCulture) + ",end=" + (endMs / 1000.0).ToString(CultureInfo.InvariantCulture));
            if (await Task.WhenAny(connected, ended.Task) == ended.Task)
            {
                await ended.Task;
                if (!pipe.IsConnected) return [];
            }
            await connected;
            var (rate, channels) = await ReadWaveHeader(pipe, token);
            var frameBytes = channels * sizeof(float);
            var buffer = new byte[16384 + frameBytes];
            var samples = new List<float>();


            int carried = 0;
            long totalBytes = 0;
            int read;
            var max = (endMs - startMs + 2000) * rate / 1000 * frameBytes;
            while ((read = await pipe.ReadAsync(buffer.AsMemory(carried, 16384), token)) > 0)
            {
                totalBytes += read;
                if (totalBytes > max) throw new InvalidDataException("PCM 超出取样窗口");
                var bytes = read + carried;
                var complete = bytes / frameBytes * frameBytes;
                for (int offset = 0; offset < complete; offset += frameBytes)
                {
                    double mono = 0;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        var value = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(offset + channel * 4, 4));
                        mono += float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0;
                    }
                    samples.Add((float)(mono / channels));
                }
                carried = bytes - complete;
                buffer.AsSpan(complete, carried).CopyTo(buffer);
            }
            await ended.Task.WaitAsync(token);
            return PcmMath.Downsample(samples.Take((int)((endMs - startMs) * rate / 1000)).ToArray(), rate, 16000);
        }
        finally
        {
            // 先断开管道解除 mpv 写阻塞，再销毁解码实例。
            pipe.Dispose();
            timeout.Cancel();
            try { await connected; } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }

    static async Task<(int rate, int channels)> ReadWaveHeader(Stream stream, CancellationToken ct)
    {
        var header = new byte[12];
        await stream.ReadExactlyAsync(header, ct);
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Expected WAV PCM header");
        int rate = 0, channels = 0;
        for (var chunk = 0; chunk < 16; chunk++)
        {
            var h = new byte[8];
            await stream.ReadExactlyAsync(h, ct);
            if (h.AsSpan(0, 4).SequenceEqual("data"u8))
                return rate > 0 && channels > 0 ? (rate, channels) : throw new InvalidDataException("Missing WAV format");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
            if (size > 65536) throw new InvalidDataException("Oversized WAV header");
            var data = new byte[size + (size & 1)];
            await stream.ReadExactlyAsync(data, ct);
            if (!h.AsSpan(0, 4).SequenceEqual("fmt "u8)) continue;
            if (size < 16) throw new InvalidDataException("Invalid WAV format");
            var format = BinaryPrimitives.ReadUInt16LittleEndian(data);
            channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
            rate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
            var bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
            if (format == 65534 && size >= 40) format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(24));
            if (format != 3 || bits != 32 || channels is < 1 or > 32 || rate is < 1000 or > 768000)
                throw new InvalidDataException("Unsupported WAV PCM format");
        }
        throw new InvalidDataException("Missing WAV data");
    }
}
