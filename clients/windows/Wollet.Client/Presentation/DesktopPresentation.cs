using System.Diagnostics;
using System.Windows.Threading;

namespace Wollet.Client;

internal sealed class DesktopPresentation : IDisposable, IAsyncDisposable
{
    private readonly Action _shutdown;
    private readonly DesktopPlanController _controller;
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly string _stopMarker;
    private CountdownWindow? _window;
    private NativeConnectionWarning? _warning;
    private bool _disposed;

    public DesktopPresentation(UiApplication application)
        : this(new DesktopPlanController(DesktopPlanClient.SendAsync), Path.Combine(new WindowsPaths().InstallDirectory, "desktop.stop"), application.Shutdown) { }

    internal DesktopPresentation(DesktopPlanController controller, string stopMarker, Action shutdown)
    {
        _shutdown = shutdown;
        _controller = controller;
        _stopMarker = stopMarker;
        _controller.Changed += Apply;
        _controller.ConnectionWarningChanged += ShowWarning;
        _poll.Tick += PollTick;
    }

    public Task StartAsync() { _poll.Start(); return PollAsync(); }
    private async void PollTick(object? sender, EventArgs args) => await PollAsync();
    private async Task PollAsync()
    {
        if (_disposed) return;
        // Check independently of in-flight IPC so an update need not wait for its timeout.
        if (File.Exists(_stopMarker)) { await DisposeAsync(); _shutdown(); return; }
        await _controller.PollAsync();
    }

    private void Apply()
    {
        if (_disposed) return;
        var state = _controller.State;
        if (!state.IsVisible)
        {
            _window?.Finish();
            return;
        }
        if (_window is null && state.Plan?.State == "scheduled")
        {
            var window = new CountdownWindow(state);
            _window = window;
            window.Closed += (_, _) => { if (ReferenceEquals(_window, window)) _window = null; };
            window.Requested += async action => await _controller.RequestActionAsync(action);
            window.Show();
        }
        _window?.RefreshState();
    }

    private void ShowWarning(string? message)
    {
        if (_disposed) return;
        if (message is null)
        {
            _warning?.Dispose();
            _warning = null;
            return;
        }
        _warning ??= new NativeConnectionWarning();
        _warning.Show(message);
        Trace.TraceWarning("Wollet desktop IPC: {0}", message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
        _poll.Tick -= PollTick;
        _controller.Dispose();
        _warning?.Dispose();
        _warning = null;
        _window?.Finish();
        _window = null;
    }

    public ValueTask DisposeAsync() { Dispose(); return _controller.DisposeAsync(); }
}
