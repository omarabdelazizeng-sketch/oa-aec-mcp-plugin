using System.Text.Json;
using System.Text.Json.Serialization;

namespace OaAecMcpPlugin.WebSocket;

/// <summary>JSON-RPC 2.0 request envelope.</summary>
public record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("method")]  string Method,
    [property: JsonPropertyName("params")]  JsonElement? Params,
    [property: JsonPropertyName("id")]      string? Id
);

/// <summary>JSON-RPC 2.0 error object.</summary>
public record JsonRpcError(
    [property: JsonPropertyName("code")]    int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")]    object? Data
);

/// <summary>JSON-RPC 2.0 success response.</summary>
public record JsonRpcSuccessResponse(
    [property: JsonPropertyName("result")] object Result,
    [property: JsonPropertyName("id")]     string? Id
)
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc => "2.0";
}

/// <summary>JSON-RPC 2.0 error response.</summary>
public record JsonRpcErrorResponse(
    [property: JsonPropertyName("error")] JsonRpcError Error,
    [property: JsonPropertyName("id")]    string? Id
)
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc => "2.0";
}
