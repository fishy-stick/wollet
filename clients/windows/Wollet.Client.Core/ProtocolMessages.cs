using System.Text.Json.Serialization;

namespace Wollet.Client.Core;

internal static class ProtocolVersion
{
    public const int Current = 1;
}

internal sealed record BindRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("deviceName")] string DeviceName,
    [property: JsonPropertyName("macAddress")] string MacAddress);

internal sealed record BindResponse(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("deviceSecret")] string DeviceSecret);

internal sealed record ApiErrorEnvelope([property: JsonPropertyName("error")] ApiErrorBody? Error);

internal sealed record ApiErrorBody(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record ClientMessage
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("protocolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ProtocolVersion { get; init; }

    [JsonPropertyName("deviceName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceName { get; init; }

    [JsonPropertyName("macAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MacAddress { get; init; }

    [JsonPropertyName("commandId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommandId { get; init; }
}

internal sealed record ServerMessage
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; }

    [JsonPropertyName("heartbeatIntervalSeconds")]
    public int HeartbeatIntervalSeconds { get; init; }

    [JsonPropertyName("offlineAfterSeconds")]
    public int OfflineAfterSeconds { get; init; }

    [JsonPropertyName("commandId")]
    public string? CommandId { get; init; }
}
