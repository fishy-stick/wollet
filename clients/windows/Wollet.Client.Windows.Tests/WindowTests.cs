using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Wollet.Client.Core;

namespace Wollet.Client.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed partial class WindowTests
{
    private static UiApplication _application = null!;
    private static Thread _thread = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        var ready = new TaskCompletionSource<UiApplication>();
        _thread = new Thread(() =>
        {
            try
            {
                var application = new UiApplication(ShutdownMode.OnExplicitShutdown);
                application.Startup += (_, _) => ready.SetResult(application);
                application.Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "Wollet WPF test dispatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _application = ready.Task.GetAwaiter().GetResult();
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        _application.Dispatcher.Invoke(_application.Shutdown);
        Assert.IsTrue(_thread.Join(TimeSpan.FromSeconds(5)));
    }

    private static Task OnUi(Func<Task> action) => _application.Dispatcher.InvokeAsync(action).Task.Unwrap();
    private static Task Rendered() => _application.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;
    private static ShutdownPlan Plan(string state = "scheduled", long remaining = 7000, long revision = 1, string id = "test") => new(id, revision, state, remaining);

    [TestMethod]
    public Task CountdownStartsWithoutActivationAndClosesOnlyAfterAcknowledgement() => OnUi(async () =>
    {
        var state = new CountdownState();
        state.Observe(Plan());
        var window = new CountdownWindow(state);
        var actions = new List<string>();
        window.Requested += actions.Add;
        window.Show();
        try
        {
            await Rendered();
            Assert.IsFalse(window.ShowActivated);
            Assert.IsFalse(window.IsActive);
            var cancel = (Button)window.FindName("CancelAction");
            Assert.IsTrue(cancel.IsDefault);
            Assert.AreSame(cancel, FocusManager.GetFocusedElement(window));
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.IsTrue(window.IsVisible);
            CollectionAssert.AreEqual(new[] { "cancel" }, actions);
            window.Close();
            Assert.IsTrue(window.IsVisible);
            Assert.AreEqual("cancel", actions[1]);
            Capture(window, "countdown");
        }
        finally { window.Finish(); }
        Assert.AreEqual(ShutdownMode.OnExplicitShutdown, _application.ShutdownMode);
        var next = new CountdownWindow(state);
        next.Show();
        Assert.IsTrue(next.IsVisible);
        next.Finish();
    });

    [TestMethod]
    public Task CountdownDisablesExpiredAndPendingActionsAndShowsFailure() => OnUi(async () =>
    {
        var state = new CountdownState();
        state.Observe(Plan());
        var window = new CountdownWindow(state);
        window.Show();
        try
        {
            await Rendered();
            var cancel = (Button)window.FindName("CancelAction");
            var execute = (Button)window.FindName("ExecuteAction");
            state.ActionPending = true;
            window.RefreshState();
            Assert.IsFalse(cancel.IsEnabled);
            Assert.IsFalse(execute.IsEnabled);
            state.ActionPending = false;
            state.Observe(Plan(remaining: 0));
            window.RefreshState();
            Assert.IsFalse(cancel.IsEnabled);
            Assert.IsFalse(execute.IsEnabled);
            Assert.AreEqual("正在关机", ((TextBlock)window.FindName("Number")).Text);
            state.Observe(Plan("failed", 0, 2));
            window.RefreshState();
            Assert.AreEqual("关闭", cancel.Content);
            Assert.IsTrue(cancel.IsEnabled);
            Assert.IsFalse(execute.IsEnabled);
            Capture(window, "countdown-failed");
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.IsFalse(window.IsVisible);
        }
        finally { if (window.IsVisible) window.Finish(); }
    });

    [TestMethod]
    public Task CountdownSupportsKeyboardCancellationAndStopsAnimationOnClose() => OnUi(async () =>
    {
        var state = new CountdownState();
        state.Observe(Plan());
        var window = new CountdownWindow(state);
        var action = "";
        window.Requested += request => action = request;
        window.Show();
        await Rendered();
        var ring = (ProgressRing)window.FindName("Ring");
        Assert.IsTrue(ring.HasAnimatedProperties);
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        Assert.AreEqual("cancel", action);
        window.Finish();
        Assert.IsFalse(ring.HasAnimatedProperties);
    });

    [TestMethod]
    public Task InstallerLoadsBindingsAndProtectsBusyClose() => OnUi(async () =>
    {
        var coordinator = new FakeCoordinator();
        var window = new InstallerWindow(coordinator);
        window.Show();
        try
        {
            await Rendered();
            var model = (InstallerViewModel)window.DataContext;
            await model.RefreshAsync(true);
            Assert.AreEqual("http://server", ((TextBox)window.FindName("ServerInput")).Text);
            Assert.AreEqual("修复", model.UpdateText);
            window.UpdateLayout();
            var scroll = (ScrollViewer)window.Content;
            Assert.AreEqual(0d, scroll.ScrollableHeight, "Default management layout must fit without scrolling.");
            Assert.AreEqual(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
            Capture(window, "installer");
            var blocked = new TaskCompletionSource<InstallationResult>();
            coordinator.Update = (_, _) => blocked.Task;
            var operation = model.UpdateCommand.ExecuteAsync();
            Assert.IsTrue(model.IsBusy);
            window.Close();
            Assert.IsTrue(window.IsVisible);
            blocked.SetResult(new("device", true));
            await operation;
            Assert.IsFalse(model.IsBusy);
            window.UpdateLayout();
            Assert.AreEqual(0d, ((ScrollViewer)window.Content).ScrollableHeight,
                "The completed-operation message must fit at the default window size.");
        }
        finally { window.EndSession(); }
    });

    [TestMethod]
    public Task CompatibilityPopupPreservesUnchangedDataAndSupportsEscape() => OnUi(async () =>
    {
        var window = new InstallerWindow(new FakeCoordinator());
        window.Show();
        try
        {
            await Rendered();
            var hint = Descendants<CompatibilityHint>(window).Single();
            var badge = (Button)hint.FindName("Badge");
            var popup = (Popup)hint.FindName("DetailsPopup");
            badge.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.IsTrue(popup.IsOpen);
            await Rendered();
            Assert.AreEqual(hint.Presentation.Details, Descendants<TextBlock>(popup.Child).Single().Text);
            Assert.IsTrue(((FrameworkElement)popup.Child).ActualWidth > 80);
            hint.Presentation = hint.Presentation with { };
            Assert.IsTrue(popup.IsOpen);
            hint.Presentation = new("服务端版本：1.2.0", "兼容", new string('长', 300) + "\n详情");
            Assert.IsFalse(popup.IsOpen);
            badge.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.IsTrue(popup.IsOpen);
            badge.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(badge), 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Assert.IsFalse(popup.IsOpen);
        }
        finally { window.EndSession(); }
    });

    [TestMethod]
    public Task LayoutRemainsScrollableAtLargeScaleWithLongErrors() => OnUi(async () =>
    {
        foreach (var scale in new[] { 1d, 1.5, 2 })
        {
            var window = new InstallerWindow(new FakeCoordinator { StatusMessage = "连接失败：" + string.Concat(Enumerable.Repeat("服务端暂时无法访问，请检查地址和网络。", 12)) });
            window.Show();
            try
            {
                await Rendered();
                ((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(scale, scale);
                var model = (InstallerViewModel)window.DataContext;
                model.ServerAddress = "http://a-long-example-server-address.invalid:8080";
                window.Height = 400;
                window.UpdateLayout();
                var scroll = (ScrollViewer)window.Content;
                Assert.IsTrue(scroll.ScrollableHeight > 0);
                Assert.IsTrue(window.ActualWidth <= window.MaxWidth);
                scroll.ScrollToBottom();
                await Rendered();
                Assert.AreEqual(scroll.ScrollableHeight, scroll.VerticalOffset, 1);
                Capture(window, $"installer-scale-{scale:0.0}");
            }
            finally { window.EndSession(); }
        }
    });

    [TestMethod]
    public Task DynamicPaletteUpdatesExistingControls() => OnUi(async () =>
    {
        var state = new CountdownState();
        state.Observe(Plan());
        var window = new CountdownWindow(state);
        window.Show();
        var original = _application.Resources["TextBrush"];
        try
        {
            await Rendered();
            _application.Resources["TextBrush"] = Brushes.Yellow;
            window.UpdateLayout();
            Assert.AreEqual(Brushes.Yellow, window.Foreground);
            Assert.AreEqual(Brushes.Yellow, ((TextBlock)window.FindName("Number")).Foreground);
        }
        finally { _application.Resources["TextBrush"] = original; window.Finish(); }
    });

    [TestMethod]
    public Task DesktopPresentationKeepsRunningAcrossPlansAndDiscardsStoppedResponses() => OnUi(async () =>
    {
        var response = new DesktopPlanResponse(Plan());
        using var controller = new DesktopPlanController((_, _) => Task.FromResult(response));
        using var desktop = new DesktopPresentation(controller, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".stop"), () => Assert.Fail("Unexpected shutdown"));
        await controller.PollAsync();
        Assert.AreEqual(1, _application.Windows.OfType<CountdownWindow>().Count());
        response = new(Plan("cancelled", 0, 2));
        await controller.PollAsync();
        Assert.AreEqual(0, _application.Windows.OfType<CountdownWindow>().Count());
        response = new(Plan(id: "second"));
        await controller.PollAsync();
        Assert.AreEqual(1, _application.Windows.OfType<CountdownWindow>().Count());
        desktop.Dispose();
        Assert.AreEqual(0, _application.Windows.OfType<CountdownWindow>().Count());
    });

    [TestMethod]
    public Task UpdateStopMarkerDrainsInFlightIpcBeforeShutdown() => OnUi(async () =>
    {
        var marker = Path.Combine(Path.GetTempPath(), "wollet-stop-test-" + Guid.NewGuid());
        var entered = new TaskCompletionSource();
        var shutdown = new TaskCompletionSource();
        var finished = false;
        using var controller = new DesktopPlanController(async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new DesktopPlanResponse(null); }
            finally { finished = true; }
        });
        await using var desktop = new DesktopPresentation(controller, marker, () =>
        {
            Assert.IsTrue(finished);
            shutdown.TrySetResult();
        });
        try
        {
            var start = desktop.StartAsync();
            await entered.Task;
            await File.WriteAllTextAsync(marker, "update");
            await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await start;
        }
        finally { File.Delete(marker); }
    });

    [TestMethod]
    public Task NativeWarningDisposalIsIdempotentAndCannotRegisterAgain() => OnUi(() =>
    {
        using var warning = new NativeConnectionWarning();
        warning.Dispose();
        warning.Dispose();
        Assert.IsFalse(warning.Show("Disposed warning must remain invisible"));
        return Task.CompletedTask;
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = Path.Combine(AppContext.BaseDirectory, "visual-checks");
        Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(file);
    }
    private sealed class FakeCoordinator : IInstallCoordinator
    {
        public string StatusMessage { get; init; } = "后台服务：已安装、正在运行，并已连接服务端。";
        public Func<IProgress<string>, CancellationToken, Task<InstallationResult>> Update = (_, _) => Task.FromResult(new InstallationResult("device", true));
        public ClientUpdateVersion GetVersions() => new("1.2.0+installed-build", "1.2.0+available-build", true);
        public Task<StartupInspectionResult> InspectAsync(CancellationToken token) => Task.FromResult(new StartupInspectionResult(new(new Uri("http://server"), "device", "secret"), StatusMessage, false, true));
        public Task<InstallationResult> InstallAsync(string server, string token, IProgress<string> progress, CancellationToken cancellationToken) => throw new AssertFailedException("No real installation is allowed in UI tests.");
        public Task<InstallationResult> UpdateAsync(IProgress<string> progress, CancellationToken token) => Update(progress, token);
        public Task<UninstallationResult> UninstallAsync(IProgress<string> progress, CancellationToken token) => throw new AssertFailedException("No real uninstallation is allowed in UI tests.");
    }
}
