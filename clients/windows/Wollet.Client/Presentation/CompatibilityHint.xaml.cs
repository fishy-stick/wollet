using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Wollet.Client;

internal partial class CompatibilityHint : UserControl
{
    public static readonly DependencyProperty PresentationProperty = DependencyProperty.Register(nameof(Presentation), typeof(CompatibilityPresentation), typeof(CompatibilityHint),
        new PropertyMetadata(CompatibilityPresentation.From(null), (owner, args) =>
        {
            if (!Equals(args.OldValue, args.NewValue)) ((CompatibilityHint)owner).DetailsPopup.IsOpen = false;
        }));
    private readonly DispatcherTimer _dismiss = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _suppressFocus;
    public CompatibilityPresentation Presentation { get => (CompatibilityPresentation)GetValue(PresentationProperty); set => SetValue(PresentationProperty, value); }
    public CompatibilityHint()
    {
        InitializeComponent();
        _dismiss.Tick += (_, _) =>
        {
            _dismiss.Stop();
            if (!Badge.IsMouseOver && !DetailsPanel.IsMouseOver && !Badge.IsKeyboardFocusWithin && !DetailsPanel.IsKeyboardFocusWithin)
                DetailsPopup.IsOpen = false;
        };
        Unloaded += (_, _) => { _dismiss.Stop(); DetailsPopup.IsOpen = false; };
    }
    private void Open() { _dismiss.Stop(); DetailsPopup.IsOpen = true; }
    private void OpenClicked(object sender, RoutedEventArgs e) => Open();
    private void OpenHovered(object sender, MouseEventArgs e) => Open();
    private void OpenFocused(object sender, KeyboardFocusChangedEventArgs e) { if (!_suppressFocus) Open(); }
    private void ScheduleDismiss(object sender, RoutedEventArgs e) => _dismiss.Start();
    private void EscapePressed(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        DetailsPopup.IsOpen = false;
        _suppressFocus = true;
        try { Badge.Focus(); }
        finally { _suppressFocus = false; }
        e.Handled = true;
    }
}
