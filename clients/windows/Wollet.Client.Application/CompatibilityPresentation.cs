using Wollet.Client.Core;

namespace Wollet.Client;

internal sealed record CompatibilityPresentation(string Version, string Badge, string Details)
{
    public static CompatibilityPresentation From(CompatibilityResult? result)
    {
        var version = "服务端版本：" + (result?.ServerVersion ?? "待确认") +
                      (result?.Historical == true ? "（上次连接）" : "");
        if (result is null) return new(version, "待确认", "连接后可确认版本和功能支持。");
        var details = $"客户端：{result.ClientVersion}\n服务端：{result.ServerVersion}\n\n{result.Detail}" +
            string.Concat(result.Missing.Select(m => $"\n• {m.Name}（{(m.Component == "client" ? "客户端" : "服务端")}）")) +
            string.Concat(result.Missing.Where(m => m.Target is not null).Select(m => m.Target).Distinct().Select(v => $"\n参考目标：{v}")) +
            (result.Missing.Length == 0 ? "" : "\n\n更新说明：客户端运行对应的新安装包并选择更新，可保留配对；服务端由管理员更新部署。开发版请使用对应测试构建，不代表正式发布页已有此版本。");
        if (FeatureCatalog.Normalize(result.ServerVersion) == "")
            details += "\n\n服务端未提供有效版本号。旧版服务端或未设置 VERSION 的本地构建可能出现此情况；刷新不会生成版本号。";
        return new(version, result.Label, details);
    }
}
