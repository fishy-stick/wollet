using System.Drawing;
using Wollet.Client.Core;

namespace Wollet.Client;

internal sealed class InstallerForm : Form
{
    private readonly InstallCoordinator _coordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBox _serverTextBox = new();
    private readonly TextBox _tokenTextBox = new();
    private readonly Button _installButton = new();
    private readonly Button _updateButton = new();
    private readonly Label _introduction = new();
    private readonly Button _uninstallButton = new();
    private readonly Label _statusLabel = new();
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
        ClientSize = new Size(520, 310);
        MinimumSize = SizeFromClientSize(ClientSize);
        AutoScroll = true;
        MaximizeBox = false;
        BuildLayout();
        ResumeLayout(performLayout: true);
        Shown += OnShown;
        FormClosed += (_, _) => _lifetime.Cancel();
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
            RowCount = 7,
        };
        layout.SuspendLayout();
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
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

        layout.Controls.Add(CreateFieldLabel("服务端地址"), 0, 1);
        _serverTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _serverTextBox.PlaceholderText = "例如 192.168.1.10:8080";
        _serverTextBox.Margin = new Padding(12, 0, 0, 12);
        _serverTextBox.TabIndex = 0;
        layout.Controls.Add(_serverTextBox, 1, 1);

        layout.Controls.Add(CreateFieldLabel("绑定 Token"), 0, 2);
        _tokenTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _tokenTextBox.PlaceholderText = "首次绑定请输入网页端生成的 Token";
        _tokenTextBox.CharacterCasing = CharacterCasing.Upper;
        _tokenTextBox.Margin = new Padding(12, 0, 0, 12);
        _tokenTextBox.TabIndex = 1;
        layout.Controls.Add(_tokenTextBox, 1, 2);

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
        layout.Controls.Add(actions, 0, 3);
        layout.SetColumnSpan(actions, 2);

        _statusLabel.AutoSize = true;
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.ForeColor = SystemColors.GrayText;
        _statusLabel.Margin = new Padding(0, 16, 0, 0);
        _statusLabel.TextAlign = ContentAlignment.TopLeft;
        _statusLabel.UseMnemonic = false;
        layout.Controls.Add(_statusLabel, 0, 4);
        layout.SetColumnSpan(_statusLabel, 2);

        layout.Controls.Add(_compatibility, 0, 5);
        layout.SetColumnSpan(_compatibility, 2);
        _refresh.LinkClicked += (_, _) => OnShown(this, EventArgs.Empty);
        layout.Controls.Add(_refresh, 0, 6);
        layout.SetColumnSpan(_refresh, 2);

        Controls.Add(layout);
        layout.ResumeLayout(performLayout: true);
        AcceptButton = _installButton;
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
        SetBusy(true);
        SetStatus("正在检查后台服务状态…", isError: false);
        try
        {
            var inspection = await _coordinator.InspectAsync(_lifetime.Token);
            if (inspection.Credentials is not null)
            {
                _serverTextBox.Text = inspection.Credentials.Server.AbsoluteUri.TrimEnd('/');
            }

            RefreshVersionState(inspection.Credentials is not null);

            SetStatus(inspection.Message, inspection.IsError, inspection.IsSuccess);
            _compatibility.ShowResult(inspection.Compatibility);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _compatibility.ShowResult(null);
            SetStatus("后台服务状态检查失败：" + exception.Message, isError: true);
        }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private void RefreshVersionState(bool hasCredentials)
    {
        var versions = _coordinator.GetVersions();
        _canInstall = versions.Action is not (ClientUpdateAction.Downgrade or ClientUpdateAction.Unknown);
        _canUpdate = hasCredentials && _canInstall;
        _updateButton.Visible = hasCredentials;
        _updateButton.Text = versions.Action == ClientUpdateAction.Repair ? "修复" : "更新";
        _installButton.Text = hasCredentials ? "绑定／更换服务端" : "安装并绑定";
        _tokenTextBox.PlaceholderText = hasCredentials ? "更新／修复无需填写 Token" : "首次绑定请输入网页端生成的 Token";
        var hint = versions.Action switch
        {
            ClientUpdateAction.Downgrade => "已安装更高版本，请运行相同或更新版本的客户端。",
            ClientUpdateAction.Unknown => "版本信息无法比较，请使用具有有效版本号的发布文件。",
            _ when hasCredentials => "更新／修复会保留现有配对，无需填写 Token。",
            _ => "填入服务端地址和 Token，安装并绑定此电脑。",
        };
        _introduction.Text = $"已安装：{(versions.IsInstalled ? versions.Installed ?? "未知" : "未安装")}    当前程序：{versions.Available ?? "未知"}" + Environment.NewLine + hint;
        AcceptButton = _canUpdate ? _updateButton : _installButton;
    }

    private async void UpdateButtonOnClick(object? sender, EventArgs eventArgs)
    {
        SetBusy(true);
        try
        {
            var progress = new ControlProgress(this, message => SetStatus(message, isError: false));
            await _coordinator.UpdateAsync(progress, _lifetime.Token);
            _compatibility.ShowResult((await _coordinator.InspectAsync(_lifetime.Token)).Compatibility);
            RefreshVersionState(hasCredentials: true);
            SetStatus("程序已更新／修复，后台服务已启动，现有配对保持不变；连接状态请在管理页面确认。", false, true);
        }
        catch (Exception exception) { ShowError(exception.Message); }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private async void InstallButtonOnClick(object? sender, EventArgs eventArgs)
    {
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
            _compatibility.ShowResult((await _coordinator.InspectAsync(_lifetime.Token)).Compatibility);
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
            }
        }
    }

    private void SetBusy(bool busy)
    {
        _refresh.Enabled = !busy;
        _busy = busy;
        _serverTextBox.Enabled = !busy;
        _tokenTextBox.Enabled = !busy;
        _installButton.Enabled = !busy && _canInstall;
        _updateButton.Enabled = !busy && _canUpdate;
        _uninstallButton.Enabled = !busy;
        UseWaitCursor = busy;
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
