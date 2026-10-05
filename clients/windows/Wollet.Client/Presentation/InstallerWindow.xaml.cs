using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Wollet.Client;

internal partial class InstallerWindow : Window
{
    private readonly InstallerViewModel _model;
    private readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private bool _sessionEnding;

    public InstallerWindow(IInstallCoordinator coordinator)
    {
        InitializeComponent();
        _model = new(coordinator, new InstallerInteraction(this));
        DataContext = _model;
        SourceInitialized += (_, _) => NativeWindowPlacement.ConstrainToWorkArea(this);
        DpiChanged += (_, _) => NativeWindowPlacement.ConstrainToWorkArea(this);
        LocationChanged += (_, _) => NativeWindowPlacement.ConstrainToWorkArea(this);
        _model.PropertyChanged += ModelChanged;
        _refresh.Tick += RefreshTick;
        ContentRendered += FirstRender;
        Closing += (_, args) => args.Cancel = _model.IsBusy && !_sessionEnding;
        Closed += (_, _) =>
        {
            _refresh.Stop();
            _refresh.Tick -= RefreshTick;
            _model.PropertyChanged -= ModelChanged;
            _model.Dispose();
        };
    }

    private async void FirstRender(object? sender, EventArgs args)
    {
        ContentRendered -= FirstRender;
        await _model.RefreshAsync(populateAddress: true);
        if (IsLoaded && !_model.IsBusy) _refresh.Start();
    }
    private async void RefreshTick(object? sender, EventArgs args) => await _model.RefreshAsync();
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(InstallerViewModel.IsBusy)) return;
        Cursor = _model.IsBusy ? Cursors.Wait : null;
        if (_model.IsBusy) _refresh.Stop();
        else if (IsLoaded) _refresh.Start();
    }
    public void EndSession() { _sessionEnding = true; Close(); }

    private sealed class InstallerInteraction(Window owner) : IInstallerInteraction
    {
        public bool ConfirmUninstall() => MessageBox.Show(owner,
            "卸载会停止并删除 Wollet Windows Service、已安装程序和本地设备凭据。\n\n服务端中的设备记录不会自动删除，仍需在管理页面中手动移除。是否继续？",
            "卸载 Wollet", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        public void ShowError(string message) => MessageBox.Show(owner, message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        public void ShowUninstalled(string message) => MessageBox.Show(owner, message, "卸载完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
