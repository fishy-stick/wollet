using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Wollet.Client;

internal partial class CountdownWindow : Window
{
    private readonly CountdownState _state;
    private readonly DispatcherTimer _textTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private bool _allowClose;
    public event Action<string>? Requested;

    public CountdownWindow(CountdownState state)
    {
        _state = state;
        InitializeComponent();
        FocusManager.SetFocusedElement(this, CancelAction);
        SourceInitialized += (_, _) => NativeWindowPlacement.ConstrainToWorkArea(this);
        Loaded += (_, _) => NativeWindowPlacement.CenterInWorkArea(this);
        DpiChanged += (_, _) => NativeWindowPlacement.ConstrainToWorkArea(this);
        LocationChanged += (_, _) => NativeWindowPlacement.ConstrainToWorkArea(this);
        Activated += (_, _) => { if (!IsKeyboardFocusWithin) CancelAction.Focus(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Cancel();
            e.Handled = true;
        };
        Closing += OnClosing;
        _textTimer.Tick += (_, _) => RefreshText();
        Closed += (_, _) =>
        {
            _textTimer.Stop();
            Ring.BeginAnimation(ProgressRing.ProgressProperty, null);
            Requested = null;
        };
        RefreshState();
    }

    public void RefreshState()
    {
        var remaining = _state.RemainingMilliseconds;
        var progress = remaining / CountdownState.DurationMilliseconds;
        // Replace the previous animation using the authoritative remaining duration.
        Ring.BeginAnimation(ProgressRing.ProgressProperty, null);
        Ring.Progress = progress;
        if (_state.Plan?.State == "scheduled" && remaining > 0)
        {
            Ring.BeginAnimation(ProgressRing.ProgressProperty,
                new DoubleAnimation(progress, 0, TimeSpan.FromMilliseconds(remaining)) { FillBehavior = FillBehavior.HoldEnd });
            _textTimer.Start();
        }
        else _textTimer.Stop();
        RefreshText();
    }

    private void RefreshText()
    {
        var display = _state.Display;
        if (Number.Text != display) Number.Text = display;
        Number.FontSize = _state.Plan?.State == "scheduled" && _state.RemainingMilliseconds > 0 ? 58 : 25;
        Seconds.Visibility = Number.FontSize == 58 ? Visibility.Visible : Visibility.Collapsed;
        if (Status.Text != _state.Status) Status.Text = _state.Status;
        CancelAction.Content = _state.IsFailed ? "关闭" : "取消关机";
        System.Windows.Automation.AutomationProperties.SetName(CancelAction, (string)CancelAction.Content);
        CancelAction.IsEnabled = _state.IsFailed || _state.CanAct;
        ExecuteAction.IsEnabled = _state.CanAct;
        if (_state.RemainingMilliseconds <= 0) _textTimer.Stop();
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => Cancel();
    private void ExecuteClicked(object sender, RoutedEventArgs e) { if (_state.CanAct) Requested?.Invoke("execute"); }
    private void Cancel()
    {
        if (_state.IsFailed) Finish();
        else if (_state.CanAct) Requested?.Invoke("cancel");
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _state.IsFailed) return;
        e.Cancel = true;
        Cancel();
    }
    public void Finish() { _allowClose = true; Close(); }
}
