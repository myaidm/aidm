using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AiDownloadManager.Presentation.Controls;

/// <summary>
/// A simple live throughput chart: a polyline over recent aggregate
/// speed samples. Rendering only — no business logic. The parent supplies
/// samples (bytes/s, oldest first) and the scale maximum.
/// </summary>
public partial class SpeedGraphControl : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty SamplesProperty =
        DependencyProperty.Register(
            nameof(Samples), typeof(IReadOnlyList<double>), typeof(SpeedGraphControl),
            new PropertyMetadata(null, OnGraphPropertyChanged));

    public static readonly DependencyProperty MaxValueProperty =
        DependencyProperty.Register(
            nameof(MaxValue), typeof(double), typeof(SpeedGraphControl),
            new PropertyMetadata(1.0, OnGraphPropertyChanged));

    public SpeedGraphControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateGraph();
    }

    public IReadOnlyList<double>? Samples
    {
        get => (IReadOnlyList<double>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    private static void OnGraphPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpeedGraphControl control)
            control.UpdateGraph();
    }

    private void UpdateGraph()
    {
        var width = GraphCanvas.ActualWidth;
        var height = GraphCanvas.ActualHeight;
        Baseline.X1 = 0;
        Baseline.Y1 = Math.Max(0, height - 1);
        Baseline.X2 = Math.Max(0, width);
        Baseline.Y2 = Math.Max(0, height - 1);

        var samples = Samples;
        if (samples is null || samples.Count == 0 || width <= 0 || height <= 0)
        {
            GraphLine.Points = new PointCollection();
            return;
        }

        var max = Math.Max(1, MaxValue);
        var points = new PointCollection(samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            var x = samples.Count == 1 ? width : i * width / (samples.Count - 1);
            var y = height - Math.Clamp(samples[i] / max, 0, 1) * height;
            points.Add(new System.Windows.Point(x, y));
        }
        GraphLine.Points = points;
    }
}
