using System.Text.Json.Nodes;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Core.Preferences;

/// <summary>Fixed, data-only CPU inference contract. No executable or remote model is loaded.</summary>
public sealed class PersonalHead
{
    readonly double[] center, scale, coef;
    readonly double[][] axes;
    readonly double intercept, margin;
    public string Dimension { get; }
    static double Number(JsonNode? n) => n is not null && n.AsValue().TryGetValue<double>(out var v) && double.IsFinite(v) ? v : throw new InvalidDataException("Non-finite model value");
    static double[] Vector(JsonNode? n,int length) => n is JsonArray a && a.Count==length ? a.Select(Number).ToArray() : throw new InvalidDataException("Model shape mismatch");
    public PersonalHead(JsonObject artifact)
    {
        Dimension=S(artifact["dimension"]);
        if(!Dimensions.Contains(Dimension) || S(artifact["extractor"])!=Extractor || artifact["syntheticFixture"]?.GetValue<bool>()==true)
            throw new InvalidDataException("Unsupported personal head");
        margin=Number(artifact["margin"]);
        if(margin!=Number(HeadContract()["heads"]![Dimension]!["margin"])) throw new InvalidDataException("Frozen threshold mismatch");
        var m=artifact["model"] ?? throw new InvalidDataException("Missing model");
        center=Vector(m["center"],512); scale=Vector(m["scale"],2); coef=Vector(m["coef"],2); intercept=Number(m["intercept"]);
        if(scale.Any(v=>v<1e-6) || m["axes"] is not JsonArray a || a.Count!=512) throw new InvalidDataException("Invalid PCA transform");
        axes=a.Select(v=>Vector(v,2)).ToArray();
        for(int i=0;i<2;i++) for(int j=0;j<2;j++)
            if(Math.Abs(axes.Sum(v=>v[i]*v[j])-(i==j?1:0))>1e-6) throw new InvalidDataException("Invalid PCA axes");
    }
    public double Score(JsonNode vector)
    {
        var x=Vector(vector,512); double value=intercept;
        if(Math.Abs(x.Sum(v=>v*v)-1)>1e-5) throw new InvalidDataException("Representation must be unit L2");
        for(int j=0;j<2;j++) { double z=0; for(int i=0;i<512;i++) z+=(x[i]-center[i])*axes[i][j]; value+=z/scale[j]*coef[j]; }
        return double.IsFinite(value) ? value : throw new InvalidDataException("Invalid score");
    }
    public string? Decide(JsonNode vector) { var score=Score(vector); var labels=Labels(Dimension); return score>margin?labels.Positive:score< -margin?labels.Negative:null; }
    public void VerifyEquivalence(JsonArray probes)
    {
        if(probes.Count<12 || probes.Count>300) throw new InvalidDataException("Missing deployment equivalence evidence");
        foreach(var p in probes)
            if(Math.Abs(Score(p!["vector"]!)-Number(p["score"]))>1e-9 || (Decide(p["vector"]!)??"ABSTAIN")!=S(p["decision"])) throw new InvalidDataException("Worker/production score or decision mismatch");
    }
}
