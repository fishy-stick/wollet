using System.Net;

namespace Wollet.Client.Core;

public sealed record ClientCredentials(Uri Server, string DeviceId, string DeviceSecret);

public sealed record DeviceIdentity(string Name, string MacAddress);

public sealed record DeviceSnapshot(
    string Id,
    string Name,
    string MacAddress,
    string Status,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt);

public sealed class WolletApiException : Exception
{
    public WolletApiException(HttpStatusCode statusCode, string code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public HttpStatusCode StatusCode { get; }

    public string Code { get; }

    public bool IsInvalidDeviceCredentials =>
        StatusCode == HttpStatusCode.Unauthorized && Code == "invalid_device_credentials";
}

public sealed class DeviceCredentialsRejectedException : Exception
{
    public DeviceCredentialsRejectedException()
        : base("设备凭据已失效，需要重新绑定")
    {
    }
}

public sealed class ClientProtocolException : Exception
{
    public ClientProtocolException(string message)
        : base(message)
    {
    }

    public ClientProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IDeviceInfoProvider
{
    Task<DeviceIdentity> GetAsync(Uri server, CancellationToken cancellationToken);
}

public interface IShutdownController
{
    Task RequestShutdownAsync(CancellationToken cancellationToken);
}
