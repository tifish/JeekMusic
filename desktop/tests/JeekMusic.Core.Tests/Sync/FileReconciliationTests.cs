using System.Text.Json.Nodes;
using JeekMusic.Core.Sync;

namespace JeekMusic.Core.Tests.Sync;

public sealed class FileReconciliationTests
{
    [Fact] public void SharedPresenceContentAndUnknownMatrix()
    {
        var cases=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"testcases","file-reconciliation-v1.json")))!["cases"]!.AsArray();
        foreach(var c in cases)
        {
            string? Hash(JsonNode? n)=>n?.GetValue<string>();
            var baseline=c!["tracked"]!.GetValue<bool>()?new FileReconciliation.Baseline(Hash(c["base"]),"confirmed"):null;
            var decision=FileReconciliation.Decide(baseline,new(c["local"]!["known"]!.GetValue<bool>(),Hash(c["local"]!["hash"])),new(c["remote"]!["known"]!.GetValue<bool>(),Hash(c["remote"]!["hash"])));
            Assert.True(decision.Action==c["action"]!.GetValue<string>() && decision.Reason==Hash(c["reason"]),c["name"]!.GetValue<string>());
        }
    }
}
