using System.Text.Json;
using System.Text.Json.Nodes;

namespace JeekMusic.Automation;

/// <summary>无反射的 MCP tools/JSON-RPC 子集，供 Native AOT 和普通构建共用。</summary>
internal sealed class McpSession(JsonArray catalog, Func<string, JsonObject, Task<JsonObject>> call)
{
    internal const string ProtocolVersion = "2025-11-25";
    static readonly string[] Versions = [ProtocolVersion, "2025-06-18", "2025-03-26", "2024-11-05"];
    bool _initialized, _ready;

    internal async Task<JsonObject?> HandleAsync(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }
        if (node is not JsonObject request) return Error(null, -32600, "Invalid Request");
        var id = request["id"];
        if (id is not null && (id is not JsonValue value ||
            !value.TryGetValue<string>(out _) && !value.TryGetValue<long>(out _)))
            return Error(null, -32600, "Invalid request id");
        if (request["jsonrpc"]?.ToString() != "2.0" || request["method"] is not JsonValue methodValue ||
            !methodValue.TryGetValue<string>(out var method) || string.IsNullOrEmpty(method) ||
            request.ContainsKey("params") && request["params"] is not JsonObject)
            return Error(id, -32600, "Invalid Request");
        var args = request["params"] as JsonObject ?? new JsonObject();

        // 通知（包括 initialized / cancelled）不能产生响应，也不能执行工具。
        if (!request.ContainsKey("id"))
        {
            if (method == "notifications/initialized" && _initialized) _ready = true;
            return null;
        }
        if (id is null) return Error(null, -32600, "Request id must not be null");

        if (method == "ping") return Result(id, new JsonObject());
        if (method == "initialize")
        {
            if (_initialized) return Error(id, -32600, "Session already initialized");
            if (args["protocolVersion"] is not JsonValue versionValue || !versionValue.TryGetValue<string>(out var version) ||
                args["capabilities"] is not JsonObject || args["clientInfo"] is not JsonObject info ||
                !IsString(info["name"]) || !IsString(info["version"]))
                return Error(id, -32602, "initialize requires protocolVersion, capabilities and clientInfo");
            _initialized = true;
            return Result(id, new JsonObject
            {
                ["protocolVersion"] = Versions.Contains(version) ? version : ProtocolVersion,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                ["serverInfo"] = new JsonObject { ["name"] = "JeekMusic", ["version"] = typeof(McpSession).Assembly.GetName().Version?.ToString() ?? "1.0.0" },
                ["instructions"] = "Controls the local Windows JeekMusic player. Track IDs and queue indices are local; indices are zero-based. Use get_status to observe asynchronous playback and library work."
            });
        }
        if (!_ready) return Error(id, -32600, "Initialize the session and send notifications/initialized first");
        switch (method)
        {
            case "tools/list":
                if (args["cursor"] is not null) return Error(id, -32602, "Invalid cursor; the tool catalog fits in one page");
                return Result(id, new JsonObject { ["tools"] = catalog.DeepClone() });
            case "tools/call":
                if (!IsString(args["name"]) || args.ContainsKey("arguments") && args["arguments"] is not JsonObject)
                    return Error(id, -32602, "tools/call requires name and object arguments");
                var name = args["name"]!.GetValue<string>();
                var tool = catalog.OfType<JsonObject>().FirstOrDefault(t => t["name"]!.GetValue<string>() == name);
                if (tool is null) return Error(id, -32602, "Unknown tool: " + name);
                try
                {
                    var arguments = args["arguments"] as JsonObject ?? new JsonObject();
                    Validate(arguments, tool["inputSchema"]!.AsObject());
                    var data = await call(name, arguments).ConfigureAwait(false);
                    return Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = data.ToJsonString() }),
                        ["structuredContent"] = data,
                        ["isError"] = false
                    });
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    return Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = e.Message }),
                        ["isError"] = true
                    });
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Infrastructure.Log.Warn("mcp", "工具执行失败：" + name, e);
                    return Error(id, -32603, "Internal error; see JeekMusic app log");
                }
            default: return Error(id, -32601, "Method not found: " + method);
        }
    }

    static bool IsString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out _);

    // 校验和 tools/list 使用同一份 schema，确保手工 JSON-RPC 调用也有类型、枚举和范围约束。
    internal static void Validate(JsonObject args, JsonObject schema)
    {
        var properties = schema["properties"]!.AsObject();
        foreach (var required in schema["required"]!.AsArray())
            if (!args.ContainsKey(required!.GetValue<string>())) throw new ArgumentException("Missing argument: " + required);
        foreach (var (name, value) in args)
        {
            if (properties[name] is not JsonObject rule) throw new ArgumentException("Unknown argument: " + name);
            ValidateValue(name, value, rule);
        }
    }

    static void ValidateValue(string name, JsonNode? node, JsonObject rule)
    {
        var value = node as JsonValue;
        var type = rule["type"]!.GetValue<string>();
        var valid = type switch
        {
            "string" => IsString(node),
            "boolean" => value is not null && value.TryGetValue<bool>(out _),
            "integer" => value is not null && value.TryGetValue<long>(out _),
            "number" => value is not null && value.TryGetValue<double>(out var n) && double.IsFinite(n),
            "array" => node is JsonArray,
            _ => false
        };
        if (!valid) throw new ArgumentException($"{name} must be {type}");
        if (rule["enum"] is JsonArray choices && !choices.Any(c => JsonNode.DeepEquals(c, node)))
            throw new ArgumentException($"{name} must be one of: {string.Join(", ", choices.Select(c => c!.ToString()))}");
        if (type is "integer" or "number")
        {
            var number = double.Parse(node!.ToString(), System.Globalization.CultureInfo.InvariantCulture);
            if (rule["minimum"] is { } min && number < min.GetValue<double>() || rule["maximum"] is { } max && number > max.GetValue<double>())
                throw new ArgumentException($"{name} is out of range");
        }
        if (type == "string")
        {
            var text = node!.GetValue<string>();
            if (rule["minLength"] is { } min && text.Trim().Length < min.GetValue<int>() ||
                rule["maxLength"] is { } max && text.Length > max.GetValue<int>()) throw new ArgumentException($"{name} has invalid length");
        }
        if (node is JsonArray array)
        {
            if (rule["minItems"] is { } min && array.Count < min.GetValue<int>() || rule["maxItems"] is { } max && array.Count > max.GetValue<int>())
                throw new ArgumentException($"{name} has invalid item count");
            foreach (var item in array) ValidateValue(name + "[]", item, rule["items"]!.AsObject());
        }
    }

    static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
    static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
}
