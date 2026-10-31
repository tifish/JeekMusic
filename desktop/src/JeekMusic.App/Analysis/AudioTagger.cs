using JeekMusic.Core.Analysis;
using Microsoft.ML.OnnxRuntime;

namespace JeekMusic.Analysis;

/// <summary>与移动端同一个量化 YAMNet，按 15600 个样本一帧累加 521 类得分。</summary>
public sealed class AudioTagger : IDisposable
{
    readonly InferenceSession _session;
    readonly string[] _inputs;
    readonly string[] _outputs;
    public AudioTagger()
    {
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        _session = new InferenceSession(Path.Combine(AppContext.BaseDirectory, "Models", "yamnet.onnx"), options);
        _inputs = _session.InputNames.ToArray(); _outputs = _session.OutputNames.ToArray();
    }

    public ModelScores? Analyze(float[] pcm, CancellationToken ct = default)
        => AnalyzeWindows(pcm, ct).Mean;

    public (ModelScores? Mean, ModelScores[] Frames) AnalyzeWindows(float[] pcm, CancellationToken ct = default)
        => AnalyzeObserved(pcm, ct, null);

    public (ModelScores? Mean, ModelScores[] Frames) AnalyzeObserved(float[] pcm, CancellationToken ct, Action<int, float[]>? observe)
    {
        var frames = pcm.Length / YamnetScores.Frame;
        if (frames == 0) return (null, []);
        var windows = new List<ModelScores>();
        var sum = new float[YamnetScores.Classes];
        var frame = new float[YamnetScores.Frame];
        using var input = OrtValue.CreateTensorValueFromMemory(frame, new long[] { YamnetScores.Frame });
        using var options = new RunOptions();
        for (int n = 0; n < frames; n++)
        {
            ct.ThrowIfCancellationRequested();
            Array.Copy(pcm, n * frame.Length, frame, 0, frame.Length);
            using var output = _session.Run(options, _inputs, new[] { input }, _outputs);
            var scores = output[0].GetTensorDataAsSpan<float>();
            if (scores.Length != sum.Length) throw new InvalidDataException("YAMNet 输出类别数不匹配");
            foreach (var score in scores) if (!float.IsFinite(score)) throw new InvalidDataException("Non-finite YAMNet output");
            observe?.Invoke(n, scores.ToArray());
            windows.Add(YamnetScores.Scores(scores));
            for (int i = 0; i < sum.Length; i++) sum[i] += scores[i];
        }
        for (int i = 0; i < sum.Length; i++) sum[i] /= frames;
        return (YamnetScores.Scores(sum), windows.ToArray());
    }
    public void Dispose() => _session.Dispose();
}
