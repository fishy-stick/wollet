using System.Windows;
using System.Windows.Media;

namespace Wollet.Client;

internal sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(Brushes.Blue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext context)
    {
        const double thickness = 7;
        var radius = Math.Max(0, (Math.Min(ActualWidth, ActualHeight) - thickness) / 2);
        if (radius == 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        context.DrawEllipse(null, new Pen(Track, thickness), center, radius, radius);
        var progress = Math.Clamp(Progress, 0, 1);
        if (progress <= 0) return;
        var pen = new Pen(Stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (progress >= 1) { context.DrawEllipse(null, pen, center, radius, radius); return; }
        var angle = progress * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            path.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, progress > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }
}
