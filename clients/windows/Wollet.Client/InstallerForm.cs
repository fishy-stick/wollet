using System.Drawing;
using Wollet.Client.Core;

namespace Wollet.Client;

internal sealed class InstallerForm : Form
{
    private readonly InstallCoordinator _coordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBox _serverTextBox = new();
    private readonly TextBox _tokenTextBox = new();
    private readonly Label _serverLabel = CreateFieldLabel("服务端地址");
    private readonly Label _tokenLabel = CreateFieldLabel("绑定 Token");
    private readonly Button _installButton = new();
    private readonly Button _updateButton = new();
    private readonly Label _introduction = new();
    private readonly Button _uninstallButton = new();
    private readonly Label _statusLabel = new();
    private readonly Label _connectionStatus = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 5_000 };
    private CancellationTokenSource? _inspection;
    private int _operationVersion;
    private readonly CompatibilityHint _compatibility = new();
    private readonly LinkLabel _refresh = new() { Text = "刷新状态", AutoSize = true };
    private bool _busy;
    private bool _canUpdate;
    private bool _canInstall;

    public InstallerForm(InstallCoordinator coordinator)
    {
        _coordinator = coordinator;
        SuspendLayout();
        Text = "Wollet";
        StartPosition = FormStartPosition.CenterScreen;
        // All layout dimensions below are authored at 100% (96 DPI).
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 360);
        MinimumSize = SizeFromClientSize(ClientSize);
        AutoScroll = true;
        MaximizeBox = false;
        BuildLayout();
        ResumeLayout(performLayout: true);
        Shown += OnShown;
        _refreshTimer.Tick += async (_, _) => await RefreshStatusAsync();
        FormClosed += (_, _) => { _refreshTimer.Stop(); _lifetime.Cancel(); };
        FormClosing += (_, args) => { if (_busy) args.Cancel = true; };
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            // Keep the available width, but measure height from the contents.
            // Long status messages can then scroll instead of being clipped.
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24),
            ColumnCount = 2,
            RowCount = 6,
        };
        layout.SuspendLayout();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _introduction.AutoSize = true;
        _introduction.Dock = DockStyle.Fill;
        _introduction.Text = "正在检查客户端版本与绑定配置…";
        _introduction.Margin = new Padding(0, 0, 0, 18);
        layout.Controls.Add(_introduction, 0, 0);
        layout.SetColumnSpan(_introduction, 2);

        var status = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 18),
        };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var row = 0; row < 3; row++) status.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        status.Controls.Add(new Label { Text = "运行状态", AutoSize = true, Margin = new Padding(0, 0, 8, 8) }, 0, 0);
        _refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refresh.Margin = new Padding(0, 0, 0, 8);
        _refresh.LinkClicked += async (_, _) => await RefreshStatusAsync();
        status.Controls.Add(_refresh, 1, 0);
        _connectionStatus.AutoSize = true;
        _connectionStatus.Dock = DockStyle.Fill;
        _connectionStatus.UseMnemonic = false;
        _connectionStatus.Margin = Padding.Empty;
        _connectionStatus.Text = "正在检查后台服务状态…";
        status.Controls.Add(_connectionStatus, 0, 1);
        status.SetColumnSpan(_connectionStatus, 2);
        _compatibility.Margin = new Padding(0, 4, 0, 0);
        status.Controls.Add(_compatibility, 0, 2);
        status.SetColumnSpan(_compatibility, 2);
        layout.Controls.Add(status, 0, 1);
        layout.SetColumnSpan(status, 2);

        layout.Controls.Add(_serverLabel, 0, 2);
        _serverTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _serverTextBox.PlaceholderText = "例如 192.168.1.10:8080";
        _serverTextBox.Margin = new Padding(12, 0, 0, 12);
        _serverTextBox.TabIndex = 0;
        layout.Controls.Add(_serverTextBox, 1, 2);

        layout.Controls.Add(_tokenLabel, 0, 3);
        _tokenTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _tokenTextBox.PlaceholderText = "首次绑定请输入网页端生成的 Token";
        _tokenTextBox.CharacterCasing = CharacterCasing.Upper;
        _tokenTextBox.Margin = new Padding(12, 0, 0, 12);
        _tokenTextBox.TabIndex = 1;
        layout.Controls.Add(_tokenTextBox, 1, 3);

        _installButton.AutoSize = true;
        _installButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _installButton.TabIndex = 0;
        _installButton.Text = "安装并绑定";
        _installButton.Padding = new Padding(12, 4, 12, 4);
        _installButton.Click += InstallButtonOnClick;

        _updateButton.AutoSize = true;
        _updateButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _updateButton.Text = "更新";
        _updateButton.Padding = new Padding(12, 4, 12, 4);
        _updateButton.Click += UpdateButtonOnClick;

        _uninstallButton.AutoSize = true;
        _uninstallButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _uninstallButton.TabIndex = 1;
        _uninstallButton.Text = "卸载客户端";
        _uninstallButton.Padding = new Padding(12, 4, 12, 4);
        _uninstallButton.Click += UninstallButtonOnClick;

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            Margin = Padding.Empty,
            TabIndex = 2,
        };
        actions.Controls.Add(_updateButton);
        actions.Controls.Add(_installButton);
        actions.Controls.Add(_uninstallButton);
        layout.Controls.Add(actions, 0, 4);
        layout.SetColumnSpan(actions, 2);

        _statusLabel.AutoSize = true;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.ForeColor = SystemColors.GrayText;
        _statusLabel.Margin = new Padding(0, 16, 0, 0);
        _statusLabel.TextAlign = ContentAlignment.TopLeft;
        _statusLabel.UseMnemonic = false;
        layout.Controls.Add(_statusLabel, 0, 5);
        layout.SetColumnSpan(_statusLabel, 2);
        Controls.Add(layout);
        layout.ResumeLayout(performLayout: true);
        AcceptButton = _installButton;
        UpdateActionAvailability();
    }

    private static Label CreateFieldLabel(string text) => new()
    {
        AutoSize = true,
        Text = text,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 0, 0, 12),
    };

    private async void OnShown(object? sender, EventArgs eventArgs)
    {
        await RefreshStatusAsync(populateAddress: true);
        if (!IsDisposed && !_lifetime.IsCancellationRequested) _refreshTimer.Start();
    }

    private async Task RefreshStatusAsync(bool populateAddress = false)
    {
        if (_busy || _inspection is not null || IsDisposed || _lifetime.IsCancellationRequested) return;
        using var inspection = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var operationVersion = _operationVersion;
        _inspection = inspection;
        inspection.CancelAfter(TimeSpan.FromSeconds(8));
        _refresh.Enabled = false;
        try
        {
            var result = await _coordinator.InspectAsync(inspection.Token);
            if (inspection.IsCancellationRequested || _busy || IsDisposed) return;
            if (populateAddress && !_serverTextBox.Modified && result.Credentials is not null)
                _serverTextBox.Text = result.Credentials.Server.AbsoluteUri.TrimEnd('/');

            RefreshVersionState(result.Credentials is not null);
            _connectionStatus.Text = result.Message;
            _connectionStatus.ForeColor = result.IsError ? Color.Firebrick
                : result.IsSuccess ? Color.ForestGreen : SystemColors.GrayText;
            _compatibility.ShowResult(result.Compatibility);
            UpdateActionAvailability();
        }
        catch (OperationCanceledException) when (inspection.IsCancellationRequested)
        {
            if (operationVersion == _operationVersion && !_busy && !IsDisposed && !_lifetime.IsCancellationRequested)
                ShowConnectionError("状态检查超时，将自动重试。更新／修复不受网络状态影响。");
        }
        catch (Exception exception)
        {
            if (operationVersion == _operationVersion && !_busy && !IsDisposed && !_lifetime.IsCancellationRequested)
                ShowConnectionError("后台服务状态检查失败：" + exception.Message);
        }
        finally
        {
            _inspection = null;
            if (!IsDisposed) _refresh.Enabled = !_busy;
        }
    }

    private void ShowConnectionError(string message)
    {
        _connectionStatus.Text = message;
        _connectionStatus.ForeColor = Color.Firebrick;
        _compatibility.ShowResult(null);
    }

    private void RefreshVersionState(bool hasCredentials)
    {
        var versions = _coordinator.GetVersions();
        _canInstall = versions.Action is not (ClientUpdateAction.Downgrade or ClientUpdateAction.Unknown);
        _canUpdate = hasCredentials && _canInstall;
        var updateAvailable = hasCredentials && versions.Action == ClientUpdateAction.Update;
        _serverLabel.Visible = _serverTextBox.Visible = !updateAvailable;
        _tokenLabel.Visible = _tokenTextBox.Visible = !updateAvailable;
        _installButton.Visible = !updateAvailable;
        if (updateAvailable) _canInstall = false;
        _updateButton.Visible = hasCredentials;
        _updateButton.Text = versions.Action == ClientUpdateAction.Repair ? "修复" : "更新";
        _installButton.Text = hasCredentials ? "绑定／更换服务端" : "安装并绑定";
        _tokenTextBox.PlaceholderText = hasCredentials ? "更新／修复无需填写 Token" : "首次绑定请输入网页端生成的 Token";
        var hint = versions.Action switch
        {
            ClientUpdateAction.Downgrade => "已安装更高版本，请运行相同或更新版本的客户端。",
            ClientUpdateAction.Unknown => "版本信息无法比较，请使用具有有效版本号的发布文件。",
            _ when updateAvailable => "发现新版。点击“更新”即可保留现有配对，无需重新绑定。",
            _ when hasCredentials => "更新／修复会保留现有配对，无需填写 Token。",
            _ => "填入服务端地址和 Token，安装并绑定此电脑。",
        };
        _introduction.Text = $"已安装：{(versions.IsInstalled ? versions.Installed ?? "未知" : "未安装")}    当前程序：{versions.Available ?? "未知"}" + Environment.NewLine + hint;
        AcceptButton = _canUpdate ? _updateButton : _installButton;
    }

    private async void UpdateButtonOnClick(object? sender, EventArgs eventArgs)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var progress = new ControlProgress(this, message => SetStatus(message, isError: false));
            await _coordinator.UpdateAsync(progress, _lifetime.Token);
            RefreshVersionState(hasCredentials: true);
            SetStatus("程序已更新／修复，现有配对保持不变。", false, true);
        }
        catch (Exception exception) { ShowError(exception.Message); }
        finally { if (!IsDisposed) { SetBusy(false); await RefreshStatusAsync(); } }
    }

    private async void InstallButtonOnClick(object? sender, EventArgs eventArgs)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var progress = new ControlProgress(this, message => SetStatus(message, isError: false));
            var result = await _coordinator.InstallAsync(
                _serverTextBox.Text,
                _tokenTextBox.Text,
                progress,
                _lifetime.Token);
            _tokenTextBox.Clear();
            RefreshVersionState(hasCredentials: true);
            SetStatus(
                result.ReusedCredentials
                    ? "服务已修复并启动，现有设备凭据保持不变。"
                    : "安装成功，设备已绑定并启动后台服务。",
                isError: false,
                isSuccess: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            if (!IsDisposed)
            {
                SetBusy(false);
                await RefreshStatusAsync();
            }
        }
    }

    private async void UninstallButtonOnClick(object? sender, EventArgs eventArgs)
    {
        var confirmation = MessageBox.Show(
            this,
            "卸载会停止并删除 Wollet Windows Service、已安装程序和本地设备凭据。" +
            Environment.NewLine + Environment.NewLine +
            "服务端中的设备记录不会自动删除，仍需在管理页面中手动移除。是否继续？",
            "卸载 Wollet",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        if (_busy) return;
        SetBusy(true);
        try
        {
            var progress = new ControlProgress(this, message => SetStatus(message, isError: false));
            var result = await _coordinator.UninstallAsync(progress, _lifetime.Token);
            _serverTextBox.Clear();
            _tokenTextBox.Clear();
            RefreshVersionState(hasCredentials: false);
            _compatibility.ShowResult(null);
            var message = result.RebootRequired
                ? "客户端已卸载；已安装程序将在 Windows 重启后完成删除。"
                : "客户端、Windows Service 和本地凭据已卸载。";
            SetStatus(message, isError: false, isSuccess: true);
            MessageBox.Show(
                this,
                message,
                "卸载完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowError("卸载失败：" + exception.Message);
        }
        finally
        {
            if (!IsDisposed)
            {
                SetBusy(false);
                await RefreshStatusAsync();
            }
        }
    }

    private void SetBusy(bool busy)
    {
        if (busy)
        {
            _operationVersion++;
            _refreshTimer.Stop();
            _inspection?.Cancel();
        }
        else if (!_lifetime.IsCancellationRequested) _refreshTimer.Start();
        _refresh.Enabled = !busy && _inspection is null;
        _busy = busy;
        _serverTextBox.Enabled = !busy;
        _tokenTextBox.Enabled = !busy;
        UpdateActionAvailability();
        _uninstallButton.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void UpdateActionAvailability()
    {
        _installButton.Enabled = !_busy && _canInstall;
        _updateButton.Enabled = !_busy && _canUpdate;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _lifetime.Cancel();
        }
        base.Dispose(disposing);
    }

    private void ShowError(string message)
    {
        SetStatus(message, isError: true);
        MessageBox.Show(
            this,
            message,
            "操作失败",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void SetStatus(string message, bool isError, bool isSuccess = false)
    {
        _statusLabel.Text = message;
        _statusLabel.ForeColor = isError
            ? Color.Firebrick
            : isSuccess
                ? Color.ForestGreen
                : SystemColors.GrayText;
    }

    private sealed class ControlProgress : IProgress<string>
    {
        private readonly Control _control;
        private readonly Action<string> _handler;

        public ControlProgress(Control control, Action<string> handler)
        {
            _control = control;
            _handler = handler;
        }

        public void Report(string value)
        {
            if (_control.IsDisposed || _control.Disposing)
            {
                return;
            }

            if (_control.InvokeRequired)
            {
                _control.Invoke(_handler, value);
                return;
            }

            _handler(value);
        }
    }
}
