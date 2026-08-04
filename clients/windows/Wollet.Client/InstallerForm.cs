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
    private readonly Label _statusLabel = new();

    public InstallerForm(InstallCoordinator coordinator)
    {
        _coordinator = coordinator;
        Text = "Wollet";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 245);
        MinimumSize = new Size(480, 245);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        BuildLayout();
        Shown += OnShown;
        FormClosed += (_, _) => _lifetime.Cancel();
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 2,
            RowCount = 5,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var introduction = new Label
        {
            AutoSize = true,
            Text = "将此电脑绑定到 Wollet，并安装后台服务。",
            Margin = new Padding(0, 0, 0, 18),
        };
        layout.Controls.Add(introduction, 0, 0);
        layout.SetColumnSpan(introduction, 2);

        layout.Controls.Add(CreateFieldLabel("服务端地址"), 0, 1);
        _serverTextBox.Dock = DockStyle.Fill;
        _serverTextBox.PlaceholderText = "例如 192.168.1.10:8080";
        _serverTextBox.Margin = new Padding(12, 3, 0, 8);
        layout.Controls.Add(_serverTextBox, 1, 1);

        layout.Controls.Add(CreateFieldLabel("绑定 Token"), 0, 2);
        _tokenTextBox.Dock = DockStyle.Fill;
        _tokenTextBox.PlaceholderText = "已有有效配置时可以留空";
        _tokenTextBox.CharacterCasing = CharacterCasing.Upper;
        _tokenTextBox.Margin = new Padding(12, 3, 0, 12);
        layout.Controls.Add(_tokenTextBox, 1, 2);

        _installButton.AutoSize = true;
        _installButton.Text = "安装并绑定";
        _installButton.Anchor = AnchorStyles.Right;
        _installButton.Padding = new Padding(12, 4, 12, 4);
        _installButton.Click += InstallButtonOnClick;
        layout.Controls.Add(_installButton, 1, 3);

        _statusLabel.AutoSize = true;
        _statusLabel.ForeColor = SystemColors.GrayText;
        _statusLabel.Margin = new Padding(0, 16, 0, 0);
        layout.Controls.Add(_statusLabel, 0, 4);
        layout.SetColumnSpan(_statusLabel, 2);

        Controls.Add(layout);
        AcceptButton = _installButton;
    }

    private static Label CreateFieldLabel(string text) => new()
    {
        AutoSize = true,
        Text = text,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 6, 0, 0),
    };

    private async void OnShown(object? sender, EventArgs eventArgs)
    {
        try
        {
            var existing = await _coordinator.TryLoadExistingAsync(_lifetime.Token);
            if (existing is not null)
            {
                _serverTextBox.Text = existing.Server.AbsoluteUri.TrimEnd('/');
                SetStatus("检测到现有配置；可直接修复安装，或使用新地址和 Token 重新绑定。", isError: false);
            }
        }
        catch (Exception exception)
        {
            SetStatus("现有配置无法读取：" + exception.Message, isError: true);
        }
    }

    private async void InstallButtonOnClick(object? sender, EventArgs eventArgs)
    {
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => SetStatus(message, isError: false));
            var result = await _coordinator.InstallAsync(
                _serverTextBox.Text,
                _tokenTextBox.Text,
                progress,
                _lifetime.Token);
            _tokenTextBox.Clear();
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
            SetStatus(exception.Message, isError: true);
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
        _serverTextBox.Enabled = !busy;
        _tokenTextBox.Enabled = !busy;
        _installButton.Enabled = !busy;
        UseWaitCursor = busy;
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
}
