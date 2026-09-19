using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Wollet.Client.Core;

namespace Wollet.Client;

internal sealed class CountdownForm : Form
{
    private ShutdownPlan? _plan;
    private long _observed;
    private readonly Button _cancel = new(), _execute = new();
    private readonly Label _status = new();
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 33 };
    private bool _allowClose;
    public event Action<string>? Requested;
    protected override bool ShowWithoutActivation => true;
    public CountdownForm()
    {
        Text = "Wollet · 即将关机"; AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(440, 510); FormBorderStyle = FormBorderStyle.FixedDialog; ControlBox = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true; DoubleBuffered = true; BackColor = Color.White;
        Font = new Font("Microsoft YaHei UI", 10);
        _cancel.Text = "取消关机"; _cancel.BackColor = Color.FromArgb(10, 88, 245); _cancel.ForeColor = Color.White;
        _execute.Text = "立即关机"; _execute.ForeColor = Color.FromArgb(223, 11, 18); _execute.BackColor = Color.White;
        foreach (var b in new[] { _cancel, _execute }) { b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Color.FromArgb(203, 208, 216); b.Height = 44; Controls.Add(b); }
        _cancel.Click += (_, _) => { if (_allowClose) Close(); else Requested?.Invoke("cancel"); }; _execute.Click += (_, _) => Requested?.Invoke("execute");
        _status.TextAlign = ContentAlignment.MiddleCenter; _status.ForeColor = Color.DimGray; Controls.Add(_status);
        AcceptButton = _cancel; CancelButton = _cancel;
        _animation.Tick += (_, _) => Invalidate(); _animation.Start();
        FormClosing += (_, e) => { if (!_allowClose) { e.Cancel = true; Requested?.Invoke("cancel"); } };
        Resize += (_, _) => LayoutButtons(); LayoutButtons();
        // This form is built at runtime rather than from a 96-DPI designer resource.
        AutoScaleDimensions = new SizeF(DeviceDpi, DeviceDpi);
        ClientSize = new Size((int)(440 * DeviceDpi / 96f), (int)(510 * DeviceDpi / 96f));
        _cancel.AccessibleName = "取消关机";
        _execute.AccessibleName = "立即关机";
        if (SystemInformation.HighContrast)
        {
            BackColor = SystemColors.Window;
            _cancel.BackColor = SystemColors.Highlight; _cancel.ForeColor = SystemColors.HighlightText;
            _execute.BackColor = SystemColors.Window; _execute.ForeColor = SystemColors.WindowText;
            _status.ForeColor = SystemColors.WindowText;
        }
    }
    private void LayoutButtons()
    {
        var scale = DeviceDpi / 96f; var margin = (int)(24 * scale); var gap = (int)(12 * scale); var width = (ClientSize.Width - margin * 2 - gap) / 2;
        _cancel.SetBounds(margin, ClientSize.Height - (int)(68 * scale), width, (int)(44 * scale));
        _execute.SetBounds(margin + width + gap, _cancel.Top, width, _cancel.Height);
        _status.SetBounds(margin, ClientSize.Height - (int)(120 * scale), ClientSize.Width - margin * 2, (int)(45 * scale));
    }
    public void UpdatePlan(ShutdownPlan plan)
    {
        _plan = plan; _observed = Stopwatch.GetTimestamp();
        var active = plan.State == "scheduled";
        _cancel.Enabled = active; _execute.Enabled = active;
        _status.Text = plan.State switch { "scheduled" => "倒计时结束后，此电脑将自动关机", "failed" => "关机失败，请在管理页面查看状态", _ => "正在关机…" };
        _allowClose = plan.State == "failed";
        ControlBox = _allowClose;
        _cancel.Text = _allowClose ? "关闭" : "取消关机";
        if (_allowClose) _cancel.Enabled = true;
        Invalidate();
    }
    public void ShowError(string message) { _status.Text = message; }
    public void Finish() { _allowClose = true; Close(); }
    protected override void Dispose(bool disposing) { if (disposing) _animation.Dispose(); base.Dispose(disposing); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f; float width = ClientSize.Width;
        using var ink = new SolidBrush(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(8, 11, 18)); using var muted = new SolidBrush(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(115, 121, 134));
        using var brand = new Font("Segoe UI", 14, FontStyle.Bold); using var title = new Font("Microsoft YaHei UI", 22, FontStyle.Bold);
        using var number = new Font("Segoe UI", 52, FontStyle.Bold); using var small = new Font("Microsoft YaHei UI", 11);
        using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString("Wollet", brand, ink, 24 * scale, 18 * scale);
        e.Graphics.DrawString("即将关机", title, ink, new RectangleF(0, 65 * scale, width, 45 * scale), centered);
        e.Graphics.DrawString("请保存正在进行的工作", small, muted, new RectangleF(0, 114 * scale, width, 30 * scale), centered);
        var circle = new RectangleF((width - 200 * scale) / 2, 165 * scale, 200 * scale, 200 * scale);
        var remaining = _plan?.State == "scheduled" ? Math.Max(0, _plan.RemainingMilliseconds - Stopwatch.GetElapsedTime(_observed).TotalMilliseconds) : 0;
        using var track = new Pen(SystemInformation.HighContrast ? SystemColors.GrayText : Color.FromArgb(230, 233, 238), 7 * scale); using var progress = new Pen(SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(10, 88, 245), 7 * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawEllipse(track, circle); if (remaining > 0) e.Graphics.DrawArc(progress, circle, -90, (float)(360 * remaining / 10000));
        var counting = _plan?.State == "scheduled";
        var display = counting ? Math.Ceiling(remaining / 1000).ToString("0") : _plan?.State == "failed" ? "关机失败" : "正在关机";
        e.Graphics.DrawString(display, counting ? number : title, ink, new RectangleF(circle.X, circle.Y + 45 * scale, circle.Width, 95 * scale), centered);
        if (counting) e.Graphics.DrawString("秒", small, muted, new RectangleF(circle.X, circle.Y + 140 * scale, circle.Width, 30 * scale), centered);
    }
}

internal sealed class DesktopPlanContext : ApplicationContext
{
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 250 };
    private CountdownForm? _form;
    private ShutdownPlan? _plan;
    private bool _busy;
    private bool _exiting;
    private DesktopPlanRequest? _pendingAction;
    private readonly WindowsPaths _paths = new();
    public DesktopPlanContext() { _poll.Tick += async (_, _) => await PollAsync(); _poll.Start(); }
    private async Task<DesktopPlanResponse> SendAsync(DesktopPlanRequest request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var pipe = new NamedPipeClientStream(".", DesktopPlanWire.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) throw new IOException("无法验证后台服务");
        using var server = Process.GetProcessById((int)pid);
        using var handle = OpenProcess(0x1000, false, pid);
        var image = new System.Text.StringBuilder(32768);
        uint length = (uint)image.Capacity;
        if (server.SessionId != 0 || handle.IsInvalid || !QueryFullProcessImageName(handle, 0, image, ref length) || !string.Equals(image.ToString(), _paths.InstalledExecutable, StringComparison.OrdinalIgnoreCase))
            throw new IOException("后台服务身份无效");
        await DesktopPlanWire.WriteAsync(pipe, request, deadline.Token);
        return await DesktopPlanWire.ReadAsync<DesktopPlanResponse>(pipe, deadline.Token);
    }
    private async Task PollAsync()
    {
        if (File.Exists(Path.Combine(_paths.InstallDirectory, "desktop.stop"))) { ExitThread(); return; }
        if (_busy) return; _busy = true;
        try { Apply(await SendAsync(new("snapshot"))); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { _form?.ShowError("状态未同步，正在重新连接后台服务…"); }
        finally { _busy = false; if (_pendingAction is { } request) { _pendingAction = null; await SendActionAsync(request); } }
    }
    private void Apply(DesktopPlanResponse response)
    {
        if (_exiting) return;
        _plan = response.Plan;
        if (!response.Accepted) { _form?.ShowError(response.Error == "too_late" ? "关机已开始，无法取消" : "操作未成功，请重试"); return; }
        if (_plan is null || _plan.State is "cancelled" or "indeterminate") { _form?.Finish(); _form = null; return; }
        if (_plan.State == "scheduled" && _form is null) { _form = new(); _form.FormClosed += (_, _) => _form = null; _form.Requested += async action => await ActionAsync(action); _form.Show(); }
        _form?.UpdatePlan(_plan);
    }
    private async Task ActionAsync(string action)
    {
        if (_plan is null) return;
        var request = new DesktopPlanRequest(action, _plan.OperationId, _plan.Revision);
        if (_busy) { _pendingAction = request; return; }
        await SendActionAsync(request);
    }
    private async Task SendActionAsync(DesktopPlanRequest request)
    {
        if (_exiting) return;
        _busy = true;
        try { Apply(await SendAsync(request)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { _form?.ShowError("操作结果未确认，请重试"); }
        finally { _busy = false; }
    }
    protected override void ExitThreadCore() { _exiting = true; _pendingAction = null; _poll.Stop(); _poll.Dispose(); _form?.Finish(); base.ExitThreadCore(); }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle handle, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, System.Text.StringBuilder name, ref uint size);
}
