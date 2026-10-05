using Wollet.Client.Core;

namespace Wollet.Client;

internal interface IInstallerInteraction
{
    bool ConfirmUninstall();
    void ShowError(string message);
    void ShowUninstalled(string message);
}

internal sealed class InstallerViewModel : ObservableState, IDisposable
{
    private readonly IInstallCoordinator _coordinator;
    private readonly IInstallerInteraction _interaction;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _token;
    private CancellationTokenSource? _inspection;
    private int _operationVersion;
    private bool _disposed, _busy, _addressEdited, _canInstall, _canUpdate;
    private string _serverAddress = "", _tokenText = "";
    private string _versions = "正在检查客户端版本与绑定配置…", _introduction = "";
    private string _connectionStatus = "正在检查后台服务状态…", _operationStatus = "";
    private string _installText = "安装并绑定", _updateText = "更新", _tokenHint = "首次绑定请输入网页端生成的 Token";
    private StatusTone _connectionTone, _operationTone;
    private bool _showBindingFields = true, _showUpdate;
    private CompatibilityPresentation _compatibility = CompatibilityPresentation.From(null);

    public InstallerViewModel(IInstallCoordinator coordinator, IInstallerInteraction interaction)
    {
        _coordinator = coordinator;
        _interaction = interaction;
        _token = _lifetime.Token;
        InstallCommand = new(() => RunOperationAsync(Operation.Install), () => !_disposed && !IsBusy && _canInstall);
        UpdateCommand = new(() => RunOperationAsync(Operation.Update), () => !_disposed && !IsBusy && _canUpdate);
        UninstallCommand = new(UninstallAsync, () => !_disposed && !IsBusy);
        RefreshCommand = new(() => RefreshAsync(), () => !_disposed && !IsBusy && _inspection is null);
    }

    public AsyncCommand InstallCommand { get; }
    public AsyncCommand UpdateCommand { get; }
    public AsyncCommand UninstallCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public bool IsBusy => _busy;
    public bool InputsEnabled => !IsBusy;
    public bool UpdateIsDefault => _canUpdate;
    public bool InstallIsDefault => !_canUpdate;
    public string ServerAddress
    {
        get => _serverAddress;
        set { _addressEdited = true; Set(ref _serverAddress, value); }
    }
    public string TokenText { get => _tokenText; set => Set(ref _tokenText, value); }
    public string Versions { get => _versions; private set => Set(ref _versions, value); }
    public string Introduction { get => _introduction; private set => Set(ref _introduction, value); }
    public string ConnectionStatus { get => _connectionStatus; private set => Set(ref _connectionStatus, value); }
    public string OperationStatus { get => _operationStatus; private set => Set(ref _operationStatus, value); }
    public StatusTone ConnectionTone { get => _connectionTone; private set => Set(ref _connectionTone, value); }
    public StatusTone OperationTone { get => _operationTone; private set => Set(ref _operationTone, value); }
    public string InstallText { get => _installText; private set => Set(ref _installText, value); }
    public string UpdateText { get => _updateText; private set => Set(ref _updateText, value); }
    public string TokenHint { get => _tokenHint; private set => Set(ref _tokenHint, value); }
    public bool ShowBindingFields { get => _showBindingFields; private set => Set(ref _showBindingFields, value); }
    public bool ShowUpdate { get => _showUpdate; private set => Set(ref _showUpdate, value); }
    public CompatibilityPresentation Compatibility { get => _compatibility; private set => Set(ref _compatibility, value); }

    public async Task RefreshAsync(bool populateAddress = false)
    {
        if (_disposed || IsBusy || _inspection is not null) return;
        using var inspection = CancellationTokenSource.CreateLinkedTokenSource(_token);
        var operationVersion = _operationVersion;
        _inspection = inspection;
        inspection.CancelAfter(TimeSpan.FromSeconds(8));
        RefreshCommands();
        bool CanApply() => !_disposed && !IsBusy && operationVersion == _operationVersion;
        try
        {
            var result = await _coordinator.InspectAsync(inspection.Token);
            if (!CanApply() || inspection.IsCancellationRequested) return;
            if (populateAddress && !_addressEdited && result.Credentials is not null)
            {
                _serverAddress = result.Credentials.Server.AbsoluteUri.TrimEnd('/');
                Changed(nameof(ServerAddress));
            }
            RefreshVersions(result.Credentials is not null);
            ConnectionStatus = result.Message;
            ConnectionTone = result.IsError ? StatusTone.Error : result.IsSuccess ? StatusTone.Success : StatusTone.Neutral;
            Compatibility = CompatibilityPresentation.From(result.Compatibility);
        }
        catch (OperationCanceledException) when (inspection.IsCancellationRequested)
        {
            if (CanApply()) ConnectionError("状态检查超时，将自动重试。更新／修复不受网络状态影响。");
        }
        catch (Exception error)
        {
            if (CanApply()) ConnectionError("后台服务状态检查失败：" + error.Message);
        }
        finally
        {
            _inspection = null;
            if (!_disposed) RefreshCommands();
        }
    }

    private void ConnectionError(string message)
    {
        ConnectionStatus = message;
        ConnectionTone = StatusTone.Error;
        Compatibility = CompatibilityPresentation.From(null);
    }

    private void RefreshVersions(bool hasCredentials)
    {
        var versions = _coordinator.GetVersions();
        var allowed = versions.Action is not (ClientUpdateAction.Downgrade or ClientUpdateAction.Unknown);
        var updateAvailable = hasCredentials && versions.Action == ClientUpdateAction.Update;
        _canInstall = allowed && !updateAvailable;
        _canUpdate = allowed && hasCredentials;
        ShowBindingFields = !updateAvailable;
        ShowUpdate = hasCredentials;
        UpdateText = versions.Action == ClientUpdateAction.Repair ? "修复" : "更新";
        InstallText = hasCredentials ? "绑定／更换服务端" : "安装并绑定";
        TokenHint = hasCredentials ? "更新／修复无需填写 Token" : "首次绑定请输入网页端生成的 Token";
        Versions = $"已安装：{(versions.IsInstalled ? DisplayVersion(versions.Installed) : "未安装")}    当前程序：{DisplayVersion(versions.Available)}";
        Introduction = versions.Action switch
        {
            ClientUpdateAction.Downgrade => "已安装更高版本，请运行相同或更新版本的客户端。",
            ClientUpdateAction.Unknown => "版本信息无法比较，请使用具有有效版本号的发布文件。",
            _ when updateAvailable => "发现新版。点击“更新”即可保留现有配对，无需重新绑定。",
            _ when hasCredentials => "更新／修复会保留现有配对，无需填写 Token。",
            _ => "填入服务端地址和 Token，安装并绑定此电脑。",
        };
        Changed(nameof(UpdateIsDefault));
        Changed(nameof(InstallIsDefault));
        RefreshCommands();
    }

    private async Task UninstallAsync()
    {
        if (!_interaction.ConfirmUninstall() || _disposed || IsBusy) return;
        await RunOperationAsync(Operation.Uninstall);
    }

    private async Task RunOperationAsync(Operation operation)
    {
        if (_disposed || IsBusy) return;
        SetBusy(true);
        var version = _operationVersion;
        var progress = new Progress<string>(message =>
        {
            if (!_disposed && IsBusy && version == _operationVersion)
                SetStatus(message, StatusTone.Neutral);
        });
        try
        {
            switch (operation)
            {
                case Operation.Install:
                    var installed = await _coordinator.InstallAsync(ServerAddress, TokenText, progress, _token);
                    if (_disposed) return;
                    TokenText = "";
                    RefreshVersions(true);
                    SetStatus(installed.ReusedCredentials ? "服务已修复并启动，现有设备凭据保持不变。" : "安装成功，设备已绑定并启动后台服务。", StatusTone.Success);
                    break;
                case Operation.Update:
                    await _coordinator.UpdateAsync(progress, _token);
                    if (_disposed) return;
                    RefreshVersions(true);
                    SetStatus("程序已更新／修复，现有配对保持不变。", StatusTone.Success);
                    break;
                case Operation.Uninstall:
                    var uninstalled = await _coordinator.UninstallAsync(progress, _token);
                    if (_disposed) return;
                    ServerAddress = TokenText = "";
                    RefreshVersions(false);
                    Compatibility = CompatibilityPresentation.From(null);
                    var message = uninstalled.RebootRequired ? "客户端已卸载；已安装程序将在 Windows 重启后完成删除。" : "客户端、Windows Service 和本地凭据已卸载。";
                    SetStatus(message, StatusTone.Success);
                    _interaction.ShowUninstalled(message);
                    break;
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_disposed)
            {
                var message = (operation == Operation.Uninstall ? "卸载失败：" : "") + error.Message;
                SetStatus(message, StatusTone.Error);
                _interaction.ShowError(message);
            }
        }
        finally
        {
            if (!_disposed)
            {
                SetBusy(false);
                await RefreshAsync();
            }
        }
    }

    private void SetStatus(string message, StatusTone tone) { OperationStatus = message; OperationTone = tone; }
    private void SetBusy(bool busy)
    {
        if (busy) { _operationVersion++; _inspection?.Cancel(); }
        _busy = busy;
        Changed(nameof(IsBusy));
        Changed(nameof(InputsEnabled));
        RefreshCommands();
    }
    private void RefreshCommands()
    {
        InstallCommand.Refresh(); UpdateCommand.Refresh(); UninstallCommand.Refresh(); RefreshCommand.Refresh();
    }
    // ProductVersion carries source build metadata after '+'. It is useful for
    // diagnostics and comparison, but makes the primary window needlessly wrap.
    private static string DisplayVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "未知";
        var buildMetadata = version.IndexOf('+');
        return buildMetadata < 0 ? version : version[..buildMetadata];
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        RefreshCommands();
    }
    private enum Operation { Install, Update, Uninstall }
}
