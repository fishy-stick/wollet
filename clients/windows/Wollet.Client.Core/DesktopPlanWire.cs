using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Wollet.Client.Core;

public sealed record DesktopPlanRequest(string Action, string? OperationId = null, long Revision = 0);
public sealed record DesktopPlanResponse(ShutdownPlan? Plan, bool Accepted = true, string? Error = null);
public static class DesktopPlanWire
{
    public const string PipeName = "Wollet.ShutdownPlan.v1";
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > 4096) throw new InvalidDataException("消息过长");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > 4096) throw new InvalidDataException("消息长度无效");
        var payload = new byte[size]; await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("消息为空");
    }
}
