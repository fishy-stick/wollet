using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace Wollet.Client;

internal sealed class UiApplication : System.Windows.Application
{
    public UiApplication(ShutdownMode shutdownMode)
    {
        ShutdownMode = shutdownMode;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/wollet-client;component/Presentation/Styles.xaml", UriKind.Relative),
        });
        ApplyPalette();
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
    }

    private void SystemParametersChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.InvokeAsync(ApplyPalette);
    }

    private void ApplyPalette()
    {
        var contrast = SystemParameters.HighContrast;
        Set("SurfaceBrush", contrast ? SystemColors.WindowColor : Colors.White);
        Set("PanelBrush", contrast ? SystemColors.WindowColor : Color.FromRgb(246, 248, 251));
        Set("TextBrush", contrast ? SystemColors.WindowTextColor : Color.FromRgb(20, 28, 43));
        Set("MutedBrush", contrast ? SystemColors.WindowTextColor : Color.FromRgb(86, 98, 116));
        Set("AccentBrush", contrast ? SystemColors.HighlightColor : Color.FromRgb(25, 88, 224));
        Set("AccentTextBrush", contrast ? SystemColors.HighlightTextColor : Colors.White);
        Set("BorderBrush", contrast ? SystemColors.WindowTextColor : Color.FromRgb(210, 218, 229));
        Set("TrackBrush", contrast ? SystemColors.GrayTextColor : Color.FromRgb(229, 234, 242));
        Set("DangerBrush", contrast ? SystemColors.WindowTextColor : Color.FromRgb(184, 34, 48));
        Set("SuccessBrush", contrast ? SystemColors.WindowTextColor : Color.FromRgb(30, 111, 61));
    }

    private void Set(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Resources[key] = brush;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
        base.OnExit(e);
    }
}
