using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using NuGet.Versioning;

namespace Wollet.Client.Core;

public interface ICompatibilityReader { Task<CompatibilityResult?> ReadAsync(CancellationToken token); }

public sealed record CompatibilityEndpoint(string? Version, string[]? Capabilities, bool Known);
public sealed record MissingFeature(string Id, string Name, string Component, string? Target);
public sealed record CompatibilityResult(string Kind, string Label, string ClientVersion, string ServerVersion,
    MissingFeature[] Missing, string Detail, bool Historical = false);
public sealed record FeatureDefinition(string Id, string Name, string[] Client, string[] Server);
public sealed record FeatureProfile(string[] Client, string[] Server);
public sealed record FeatureRelease(string Version, string Profile, int Order);
public sealed record FeatureCatalog(FeatureDefinition[] Features, Dictionary<string, FeatureProfile> Profiles,
    FeatureRelease[] Versions, string CurrentProfile)
{
    public static FeatureCatalog Default { get; } = Load();
    private static FeatureCatalog Load()
    {
        using var stream = typeof(FeatureCatalog).Assembly.GetManifestResourceStream("Wollet.FeatureCatalog.json")!;
        return JsonSerializer.Deserialize<FeatureCatalog>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
    public static string RuntimeVersion => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public static string Normalize(string? value)
    {
        if (value is null || value.Length > 128 || !Regex.IsMatch(value, @"^([0-9]+\.[0-9]+(?:\.[0-9]+){0,2})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?\z")) return "";
        return NuGetVersion.TryParse(value, out var parsed) ? parsed.ToNormalizedString().Split('+')[0].ToLowerInvariant() : "";
    }
    public static string Display(string? value) => Normalize(value) == "" ? "版本未知" : value!;
    private FeatureRelease? Release(string? value) => Normalize(value) is { Length: > 0 } normalized
        ? Versions.FirstOrDefault(r => Normalize(r.Version) == normalized) : null;
    private static bool Supports(string[] caps, string[] needs) => needs.All(caps.Contains);
    private string[]? Capabilities(CompatibilityEndpoint endpoint, bool client)
    {
        if (!endpoint.Known) return null;
        if (endpoint.Capabilities is not null) return endpoint.Capabilities;
        var release = Release(endpoint.Version);
        return release is null ? null : client ? Profiles[release.Profile].Client : Profiles[release.Profile].Server;
    }
    private string? Target(CompatibilityEndpoint endpoint, bool client, string[] needs)
    {
        var old = Release(endpoint.Version);
        if (old is null) return null;
        var expected = client ? Profiles[old.Profile].Client : Profiles[old.Profile].Server;
        if (endpoint.Capabilities is not null && !Supports(endpoint.Capabilities, expected)) return null;
        return Versions.OrderBy(r => r.Order).FirstOrDefault(r => r.Order > old.Order &&
            Supports(client ? Profiles[r.Profile].Client : Profiles[r.Profile].Server, needs))?.Version;
    }
    public CompatibilityResult Evaluate(CompatibilityEndpoint client, CompatibilityEndpoint server)
    {
        var result = new CompatibilityResult("compatible", "", Display(client.Version), Display(server.Version), [], "");
        var cc = Capabilities(client, true); var sc = Capabilities(server, false);
        if (cc is null || sc is null) return result with { Kind = "unknown", Label = "兼容性未确认", Detail = "功能支持尚未确认，连接后可重新检查。" };
        var missing = new List<MissingFeature>();
        foreach (var feature in Features)
        {
            if (feature.Client.Length == 0 || feature.Server.Length == 0) continue;
            var cp = Supports(cc, feature.Client); var sp = Supports(sc, feature.Server);
            if (cp == sp) continue;
            missing.Add(new(feature.Id, feature.Name, cp ? "server" : "client",
                Target(cp ? server : client, !cp, cp ? feature.Server : feature.Client)));
        }
        if (missing.Count > 0)
        {
            result = result with { Kind = "limited", Label = "兼容性受限", Missing = missing.ToArray(), Detail = "以下功能当前不可用，其他已支持功能仍可使用。" };
            if (missing.All(m => m.Target is not null) && missing.Select(m => m.Component).Distinct().Count() == 1)
                result = result with { Kind = missing[0].Component + "_upgrade", Label = missing[0].Component == "client" ? "客户端可升级" : "服务端可升级", Detail = "更新对应组件可启用以下功能；目标来自内置目录，未查询最新发布版本。" };
        }
        else if (Normalize(client.Version) == "" || Normalize(server.Version) == "")
            result = result with { Kind = "unknown", Label = "版本未知", Detail = "已确认的功能可正常使用，部分版本信息未提供。" };
        else if (Normalize(client.Version) != Normalize(server.Version))
            result = result with { Kind = "different", Label = "版本不一致", Detail = "当前已确认的功能均可用，无需为保持一致而升级。" };
        return result;
    }
}

public sealed class ConnectionCompatibility
{
    private CompatibilityResult _snapshot = FeatureCatalog.Default.Evaluate(new(FeatureCatalog.RuntimeVersion, null, false), new(null, null, false));
    public CompatibilityResult Snapshot => Volatile.Read(ref _snapshot);
    public void Connected(string? serverVersion, string[]? serverCapabilities, string[] clientCapabilities) =>
        Volatile.Write(ref _snapshot, FeatureCatalog.Default.Evaluate(new(FeatureCatalog.RuntimeVersion, clientCapabilities, true), new(serverVersion, serverCapabilities, true)));
    public void Disconnected() => Volatile.Write(ref _snapshot, Snapshot with { Kind = "unknown", Label = "待确认", Missing = [], Detail = "后台服务未连接，功能支持待确认。", Historical = true });
}
