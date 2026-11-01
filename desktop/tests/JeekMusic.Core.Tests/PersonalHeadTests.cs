using System.Text.Json.Nodes;
using JeekMusic.Core.Preferences;
using static JeekMusic.Core.Preferences.PreferenceProtocol;

namespace JeekMusic.Core.Tests;

public sealed class PersonalHeadTests
{
    public static JsonArray Vector(double first)=>new(Enumerable.Range(0,512).Select(i=>(JsonNode?)JsonValue.Create(i==0?first:i==1?Math.Sqrt(1-first*first):0)).ToArray());
    public static JsonObject Artifact(string dimension="VOICE")=>new() {
        ["dimension"]=dimension,["extractor"]=Extractor,["margin"]=HeadContract()["heads"]![dimension]!["margin"]!.DeepClone(),
        ["model"]=new JsonObject { ["center"]=new JsonArray(Enumerable.Range(0,512).Select(_=>(JsonNode?)JsonValue.Create(0d)).ToArray()),
            ["axes"]=new JsonArray(Enumerable.Range(0,512).Select(i=>(JsonNode)new JsonArray(i==0?1d:0d,i==1?1d:0d)).ToArray()),
            ["scale"]=new JsonArray(1d,1d),["coef"]=new JsonArray(1d,0d),["intercept"]=0d } };
    [Theory] [InlineData("MOOD")] [InlineData("VOICE")] [InlineData("GENRE")] [InlineData("CALMNESS")]
    public void FrozenMarginsAbstainAtEqualityAndKeepDimensionsIndependent(string d)
    {
        var artifact=Artifact(d); var model=new PersonalHead(artifact); var margin=artifact["margin"]!.GetValue<double>();
        Assert.Null(model.Decide(Vector(margin))); Assert.Null(model.Decide(Vector(-margin)));
        Assert.Equal(Labels(d).Positive,model.Decide(Vector(.9))); Assert.Equal(Labels(d).Negative,model.Decide(Vector(-.9)));
        Assert.Equal(.123,model.Score(Vector(.123)),10);
    }
    [Fact] public void RejectsWrongExtractorThresholdShapeAxesSyntheticAndNonfiniteOrNonunitRepresentation()
    {
        foreach(var mutate in new Action<JsonObject>[] { a=>a["extractor"]="different",a=>a["margin"]=.01,a=>a["syntheticFixture"]=true,a=>a["model"]!["axes"]![0]![0]=2d,a=>a["model"]!["scale"]![0]=0d,a=>a["model"]!["center"]=new JsonArray(0d) })
        { var a=Artifact(); mutate(a); Assert.Throws<InvalidDataException>(()=>new PersonalHead(a)); }
        var model=new PersonalHead(Artifact()); var vector=Vector(.8); vector[0]=2d;
        Assert.Throws<InvalidDataException>(()=>model.Score(vector));
    }
    [Fact] public void EquivalenceRequiresScoresAndExactDecisionsIncludingAbstention()
    {
        var model=new PersonalHead(Artifact()); var probes=new JsonArray();
        foreach(var v in Enumerable.Range(0,12).Select(i=>(i-6)/10d)) probes.Add((JsonNode)new JsonObject { ["vector"]=Vector(v),["score"]=v,["decision"]=model.Decide(Vector(v))??"ABSTAIN" });
        model.VerifyEquivalence(probes); probes[0]!["score"]=-.1;
        Assert.Throws<InvalidDataException>(()=>model.VerifyEquivalence(probes));
        probes[0]!["score"]=-.6; probes[0]!["decision"]="VOCAL";
        Assert.Throws<InvalidDataException>(()=>model.VerifyEquivalence(probes));
    }
}
