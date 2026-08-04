namespace Wollet.Client.Core;

public static class ServerAddress
{
    public static Uri Normalize(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            throw new ArgumentException("请输入服务端地址", nameof(value));
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "http://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(parsed.Host))
        {
            throw new ArgumentException("服务端地址必须是有效的 HTTP 或 HTTPS 地址", nameof(value));
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            throw new ArgumentException("服务端地址不能包含用户信息、查询参数或片段", nameof(value));
        }

        var builder = new UriBuilder(parsed)
        {
            Path = parsed.AbsolutePath == "/" ? string.Empty : parsed.AbsolutePath.TrimEnd('/'),
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    public static Uri ApiEndpoint(Uri server, string path)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("API 路径必须以 / 开头", nameof(path));
        }

        return new Uri(server.GetLeftPart(UriPartial.Path).TrimEnd('/') + path, UriKind.Absolute);
    }

    public static Uri WebSocketEndpoint(Uri server)
    {
        var endpoint = new UriBuilder(ApiEndpoint(server, "/api/v1/client/connect"))
        {
            Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };
        return endpoint.Uri;
    }

    public static bool AreEquivalent(Uri left, Uri right) =>
        Uri.Compare(
            Normalize(left.AbsoluteUri),
            Normalize(right.AbsoluteUri),
            UriComponents.HttpRequestUrl,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
}
