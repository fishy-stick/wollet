using Wollet.Client.Core;

namespace Wollet.Client;

internal sealed class CompatibilityHint : FlowLayoutPanel
{
    private readonly Label _version = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 8, 0), UseMnemonic = false };
    private readonly LinkLabel _badge = new() { AutoSize = true, Margin = new Padding(0, 4, 0, 0), Text = "待确认", AccessibleName = "版本兼容详情" };
    private readonly ToolStripDropDown _popup = new() { AutoClose = true, Padding = new Padding(8) };
    private readonly Label _details = new() { AutoSize = true, MaximumSize = new Size(360, 0), Padding = new Padding(8), UseMnemonic = false };
    private readonly System.Windows.Forms.Timer _dismiss = new() { Interval = 250 };
    public CompatibilityHint()
    {
        AutoSize = true; Dock = DockStyle.Fill; WrapContents = true;
        Controls.Add(_version); Controls.Add(_badge);
        _popup.Items.Add(new ToolStripControlHost(_details) { AutoSize = true, Margin = Padding.Empty, Padding = Padding.Empty });
        _badge.MouseEnter += (_, _) => Open();
        _badge.Enter += (_, _) => Open();
        _badge.LinkClicked += (_, _) => Open();
        _badge.MouseLeave += (_, _) => _dismiss.Start();
        _popup.MouseLeave += (_, _) => _dismiss.Start();
        _dismiss.Tick += (_, _) => {
            if (!_popup.Bounds.Contains(Cursor.Position) && !_badge.RectangleToScreen(_badge.ClientRectangle).Contains(Cursor.Position) && !_popup.ContainsFocus && !_badge.Focused) _popup.Close();
            _dismiss.Stop();
        };
        _badge.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { _popup.Close(); e.Handled = true; } };
        ShowResult(null);
    }
    public void ShowResult(CompatibilityResult? result)
    {
        _popup.Close();
        _version.Text = "服务端：" + (result?.ServerVersion ?? "待确认") + (result?.Historical == true ? "（上次连接）" : "");
        _badge.Text = result?.Label ?? "待确认"; _badge.Visible = !string.IsNullOrEmpty(_badge.Text);
        _details.Text = result is null ? "连接后可确认版本和功能支持。" :
            $"客户端：{result.ClientVersion}\n服务端：{result.ServerVersion}\n\n{result.Detail}" +
            string.Concat(result.Missing.Select(m => $"\n• {m.Name}（{(m.Component == "client" ? "客户端" : "服务端")}）")) +
            string.Concat(result.Missing.Where(m => m.Target is not null).Select(m => m.Target).Distinct().Select(v => $"\n参考目标：{v}")) +
            (result.Missing.Length == 0 ? "" : "\n\n更新说明：客户端运行对应的新安装包并选择更新，可保留配对；服务端由管理员更新部署。开发版请使用对应测试构建，不代表正式发布页已有此版本。");
        _badge.AccessibleDescription = _details.Text;
    }
    private void Open() { _dismiss.Stop(); if (_badge.Visible && !_popup.Visible) _popup.Show(_badge, new Point(0, _badge.Height)); }
    protected override void Dispose(bool disposing) { if (disposing) { _dismiss.Dispose(); _popup.Dispose(); } base.Dispose(disposing); }
}
