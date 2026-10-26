using System.Text.Json.Nodes;
using JeekMusic.Automation;

namespace JeekMusic.App.Tests;

public sealed class McpSessionTests
{
    [Fact]
    public void OutputCatalogDescribesOpaqueEndpointsWithoutChangingInputs()
    {
        var catalog = McpTools.BuildCatalog();
        var read = catalog.Single(x => x!["name"]!.GetValue<string>() == "get_output")!;
        Assert.Contains("opaque", read["description"]!.GetValue<string>());
        Assert.Empty(read["inputSchema"]!["properties"]!.AsObject());
        var write = catalog.Single(x => x!["name"]!.GetValue<string>() == "set_output")!;
        var fields = write["inputSchema"]!["properties"]!.AsObject();
        Assert.Equal(new[] { "automaticFormat", "device", "exclusive" }, fields.Select(x => x.Key).Order().ToArray());
        Assert.Contains("opaque", fields["device"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public void DiagnosticsDescribeProductionTagWritingPolicy()
    {
        var tool = McpTools.BuildCatalog().Single(x => x!["name"]!.GetValue<string>() == "get_diagnostics")!;
        Assert.Contains("tagWriting", tool["description"]!.GetValue<string>());
        Assert.Empty(tool["inputSchema"]!["properties"]!.AsObject());
    }
    static McpSession Session(Func<string, JsonObject, Task<JsonObject>>? call = null) =>
        new(McpTools.BuildCatalog(), call ?? ((_, _) => Task.FromResult(new JsonObject { ["ok"] = true })));

    static async Task Ready(McpSession session, string version = McpSession.ProtocolVersion)
    {
        var result = await session.HandleAsync("""{"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"VERSION","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""".Replace("VERSION", version));
        Assert.Equal(version == "future" ? McpSession.ProtocolVersion : version, result!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("init", result["id"]!.GetValue<string>());
        Assert.Null(await session.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
    }

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    [InlineData("2025-03-26")]
    [InlineData("2024-11-05")]
    [InlineData("future")]
    public async Task NegotiatesVersionsAndReturnsDiscoverableTools(string version)
    {
        var session = Session();
        await Ready(session, version);
        var response = await session.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = response!["result"]!["tools"]!.AsArray();
        Assert.Equal(24, tools.Count);
        Assert.Equal(tools.Count, tools.Select(t => t!["name"]!.GetValue<string>()).Distinct().Count());
        Assert.All(tools, t => Assert.False(t!["inputSchema"]!["additionalProperties"]!.GetValue<bool>()));
    }

    [Theory]
    [InlineData("{", -32700)]
    [InlineData("[]", -32600)]
    [InlineData("null", -32600)]
    [InlineData("{}", -32600)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":{},\"method\":\"ping\"}", -32600)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":[]}", -32600)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}", -32602)]
    public async Task RejectsMalformedRequests(string request, int code)
    {
        var response = await Session().HandleAsync(request);
        Assert.Equal(code, response!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task EnforcesLifecycleAndDoesNotExecuteNotifications()
    {
        var called = 0;
        var session = Session((_, _) => { called++; return Task.FromResult(new JsonObject()); });
        var early = await session.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_status"}}""");
        Assert.Equal(-32600, early!["error"]!["code"]!.GetValue<int>());
        await Ready(session);
        Assert.Null(await session.HandleAsync("""{"jsonrpc":"2.0","method":"tools/call","params":{"name":"get_status"}}"""));
        Assert.Equal(0, called);
        var response = await session.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_status"}}""");
        Assert.Equal(1, called);
        Assert.False(response!["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(response["result"]!["structuredContent"]!.ToJsonString(), response["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("set_volume", "{\"volume\":101}")]
    [InlineData("set_output", "{\"sampleRateMode\":\"invalid\"}")]
    [InlineData("set_output", "{\"sampleRateMode\":\"auto\"}")]
    [InlineData("set_output", "{\"automaticFormat\":\"true\"}")]
    [InlineData("set_output", "{\"sampleRate\":0}")]
    [InlineData("set_output", "{\"sampleRate\":44100.5}")]
    [InlineData("set_output", "{\"sampleRate\":1536001}")]
    [InlineData("set_volume", "{\"muted\":\"true\"}")]
    [InlineData("get_status", "{\"unknown\":true}")]
    [InlineData("search_tracks", "{\"offset\":-1}")]
    [InlineData("search_tracks", "{\"limit\":201}")]
    [InlineData("search_tracks", "{\"limit\":1.5}")]
    [InlineData("search_tracks", "{\"sort\":\"bad\"}")]
    [InlineData("search_tracks", "{\"reverse\":\"true\"}")]
    [InlineData("get_track", "{}")]
    [InlineData("get_track", "{\"trackId\":null}")]
    [InlineData("set_smart_tags", "{\"trackIds\":[],\"mood\":\"SPORT\"}")]
    [InlineData("set_smart_tags", "{\"trackIds\":[1]}")]
    [InlineData("set_smart_tags", "{\"trackIds\":[1],\"mood\":\"VOCAL\"}")]
    [InlineData("set_smart_tags", "{\"trackIds\":[1],\"voice\":null}")]
    [InlineData("set_smart_tags", "{\"trackIds\":[1],\"genre\":\"POP\"}")]
    [InlineData("set_smart_tags", "{\"trackIds\":[1],\"extra\":true}")]
    [InlineData("play_tracks", "{\"trackIds\":[]}")]
    [InlineData("play_tracks", "{\"trackIds\":[\"1\"]}")]
    [InlineData("playback", "{\"action\":\"stop_typo\"}")]
    [InlineData("edit_playlist", "{\"action\":\"create\",\"name\":\"   \"}")]
    public async Task InvalidToolArgumentsProduceToolErrorsWithoutCallingServices(string name, string arguments)
    {
        var session = Session((_, _) => throw new Exception("Must not call services"));
        await Ready(session);
        var response = await session.HandleAsync("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"NAME","arguments":ARGS}}""".Replace("NAME", name).Replace("ARGS", arguments));
        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("Modified", false)]
    [InlineData("FileName", true)]
    public async Task SearchSortOverridesPassSchemaWithoutChangingTheToolContract(string sort, bool reverse)
    {
        JsonObject? received = null;
        var session = Session((_, args) => { received = args; return Task.FromResult(new JsonObject()); });
        await Ready(session);
        var response = await session.HandleAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "search_tracks", ["arguments"] = new JsonObject { ["sort"] = sort, ["reverse"] = reverse } }
        }.ToJsonString());
        Assert.False(response!["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal(sort, received!["sort"]!.GetValue<string>());
        Assert.Equal(reverse, received["reverse"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UnknownMethodsToolsAndServiceFailuresHaveDistinctErrorsAndSessionSurvives()
    {
        var session = Session((_, _) => throw new ArgumentException("Track unavailable"));
        await Ready(session);
        var method = await session.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"no_such_method"}""");
        Assert.Equal(-32601, method!["error"]!["code"]!.GetValue<int>());
        var tool = await session.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"no_such_tool"}}""");
        Assert.Equal(-32602, tool!["error"]!["code"]!.GetValue<int>());
        var failure = await session.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_status"}}""");
        Assert.True(failure!["result"]!["isError"]!.GetValue<bool>());
        Assert.Equal("Track unavailable", failure["result"]!["content"]![0]!["text"]!.GetValue<string>());
        var ping = await session.HandleAsync("""{"jsonrpc":"2.0","id":4,"method":"ping"}""");
        Assert.NotNull(ping!["result"]);
    }

    [Fact]
    public async Task FramingAcceptsCrLfUnicodeAndMultipleMessagesButDiscardsTruncatedFinalMessage()
    {
        using var reader = new StringReader("你好\r\n" + new string('a', 9000) + "\nunfinished");
        var lines = new List<string>();
        await foreach (var line in McpWire.ReadLinesAsync(reader)) lines.Add(line);
        Assert.Equal(new[] { "你好", new string('a', 9000) }, lines);
    }

    [Fact]
    public async Task FramingBoundsMemoryEvenWithoutNewline()
    {
        using var reader = new StringReader(new string('x', McpWire.MaxMessageLength + 1));
        await Assert.ThrowsAsync<IOException>(async () => { await foreach (var _ in McpWire.ReadLinesAsync(reader)) { } });
    }
}
