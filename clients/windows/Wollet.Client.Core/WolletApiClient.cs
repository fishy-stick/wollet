using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Wollet.Client.Core;

public sealed class WolletApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    public WolletApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ClientCredentials> BindAsync(
        Uri server,
        string token,
        DeviceIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(identity);

        token = token.Trim();
        if (token.Length == 0)
        {
            throw new ArgumentException("请输入绑定 Token", nameof(token));
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            ServerAddress.ApiEndpoint(server, "/api/v1/client/bind"))
        {
            Content = JsonContent.Create(
                new BindRequest(token, identity.Name, identity.MacAddress),
                options: JsonOptions),
        };
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw await ReadApiErrorAsync(response, cancellationToken);
        }

        var payload = await response.Content.ReadFromJsonAsync<BindResponse>(JsonOptions, cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceId) || string.IsNullOrWhiteSpace(payload.DeviceSecret))
        {
            throw new ClientProtocolException("服务端返回了不完整的设备凭据");
        }

        return new ClientCredentials(server, payload.DeviceId, payload.DeviceSecret);
    }

    public async Task<DeviceSnapshot> GetCurrentDeviceAsync(
        ClientCredentials credentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            ServerAddress.ApiEndpoint(credentials.Server, "/api/v1/client/me"));
        ApplyCredentials(request, credentials);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw await ReadApiErrorAsync(response, cancellationToken);
        }

        var payload = await response.Content.ReadFromJsonAsync<DeviceSnapshot>(JsonOptions, cancellationToken);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Id))
        {
            throw new ClientProtocolException("服务端返回了无效的设备信息");
        }

        return payload;
    }

    internal static void ApplyCredentials(HttpRequestMessage request, ClientCredentials credentials)
    {
        request.Headers.TryAddWithoutValidation("X-Wollet-Device-ID", credentials.DeviceId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.DeviceSecret);
    }

    private static async Task<WolletApiException> ReadApiErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<ApiErrorEnvelope>(JsonOptions, cancellationToken);
            if (!string.IsNullOrWhiteSpace(payload?.Error?.Message))
            {
                return new WolletApiException(
                    response.StatusCode,
                    payload.Error.Code ?? "unknown_error",
                    payload.Error.Message);
            }
        }
        catch (JsonException)
        {
        }
        catch (NotSupportedException)
        {
        }

        return new WolletApiException(
            response.StatusCode,
            "http_error",
            $"服务端返回 HTTP {(int)response.StatusCode}");
    }
}
