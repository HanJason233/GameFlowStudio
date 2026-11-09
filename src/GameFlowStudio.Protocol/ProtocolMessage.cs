using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameFlowStudio.Protocol;

public static class ProtocolConstants
{
    public const string CurrentVersion = "1.0";
    public const int MaximumMessageBytes = 16 * 1024 * 1024;
}

[JsonConverter(typeof(JsonStringEnumConverter<MessageKind>))]
public enum MessageKind
{
    Command,
    Response,
    Event
}

public sealed record ProtocolMessage
{
    public string ProtocolVersion { get; init; } = ProtocolConstants.CurrentVersion;
    public Guid MessageId { get; init; } = Guid.NewGuid();
    public Guid? ReplyTo { get; init; }
    public required MessageKind Kind { get; init; }
    public required string Type { get; init; }
    public JsonElement Payload { get; init; }
    public long Sequence { get; init; }

    public static ProtocolMessage Command(string type, object? payload = null) =>
        new() { Kind = MessageKind.Command, Type = type, Payload = SerializePayload(payload) };

    public static ProtocolMessage Event(string type, object? payload, long sequence) =>
        new() { Kind = MessageKind.Event, Type = type, Payload = SerializePayload(payload), Sequence = sequence };

    internal static JsonElement SerializePayload(object? payload) =>
        payload is null ? JsonDocument.Parse("null").RootElement.Clone() : JsonSerializer.SerializeToElement(payload, ProtocolJson.Options);
}

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            MaxDepth = 64
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public sealed record RpcResponse(bool Success, JsonElement? Data = null, string? Error = null)
{
    public static RpcResponse Ok(object? data) => new(true, ProtocolMessage.SerializePayload(data));
    public static RpcResponse Fail(string error) => new(false, null, error);
}

public sealed class RemoteCommandException(string message) : Exception(message);
