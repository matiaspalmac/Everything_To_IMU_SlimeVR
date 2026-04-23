using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Everything_To_IMU_SlimeVR.UI.Controls;

/// <summary>
/// Minimal WPF polyline chart for 3 streaming series (X/Y/Z).
/// No dependencies, no allocations per sample, manual repaint.
/// </summary>
public class LiveChart : Control
{
    public static readonly DependencyProperty WindowSizeProperty =
        DependencyProperty.Register(nameof(WindowSize), typeof(int), typeof(LiveChart),
            new PropertyMetadata(200, (d, _) => ((LiveChart)d).Reset()));

    public static readonly DependencyProperty MinValueProperty =
        DependencyProperty.Register(nameof(MinValue), typeof(double), typeof(LiveChart),
            new PropertyMetadata(-Math.PI));

    public static readonly DependencyProperty MaxValueProperty =
        DependencyProperty.Register(nameof(MaxValue), typeof(double), typeof(LiveChart),
            new PropertyMetadata(Math.PI));

    public static readonly DependencyProperty StrokeXProperty =
        DependencyProperty.Register(nameof(StrokeX), typeof(Brush), typeof(LiveChart),
            new PropertyMetadata(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xED, 0x6A, 0x5A))));

    public static readonly DependencyProperty StrokeYProperty =
        DependencyProperty.Register(nameof(StrokeY), typeof(Brush), typeof(LiveChart),
            new PropertyMetadata(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4F, 0xCB, 0x70))));

    public static readonly DependencyProperty StrokeZProperty =
        DependencyProperty.Register(nameof(StrokeZ), typeof(Brush), typeof(LiveChart),
            new PropertyMetadata(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6E, 0x8C, 0xF0))));

    public static readonly DependencyProperty GridBrushProperty =
        DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(LiveChart),
            new PropertyMetadata(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x42))));

    public static readonly DependencyProperty ShowXProperty =
        DependencyProperty.Register(nameof(ShowX), typeof(bool), typeof(LiveChart),
            new PropertyMetadata(true, (d, _) => ((LiveChart)d).InvalidateVisual()));
    public static readonly DependencyProperty ShowYProperty =
        DependencyProperty.Register(nameof(ShowY), typeof(bool), typeof(LiveChart),
            new PropertyMetadata(true, (d, _) => ((LiveChart)d).InvalidateVisual()));
    public static readonly DependencyProperty ShowZProperty =
        DependencyProperty.Register(nameof(ShowZ), typeof(bool), typeof(LiveChart),
            new PropertyMetadata(true, (d, _) => ((LiveChart)d).InvalidateVisual()));
    public static readonly DependencyProperty IsPausedProperty =
        DependencyProperty.Register(nameof(IsPaused), typeof(bool), typeof(LiveChart),
            new PropertyMetadata(false, (d, _) => ((LiveChart)d).InvalidateVisual()));

    public bool ShowX { get => (bool)GetValue(ShowXProperty); set => SetValue(ShowXProperty, value); }
    public bool ShowY { get => (bool)GetValue(ShowYProperty); set => SetValue(ShowYProperty, value); }
    public bool ShowZ { get => (bool)GetValue(ShowZProperty); set => SetValue(ShowZProperty, value); }
    public bool IsPaused { get => (bool)GetValue(IsPausedProperty); set => SetValue(IsPausedProperty, value); }

    public int WindowSize { get => (int)GetValue(WindowSizeProperty); set => SetValue(WindowSizeProperty, value); }
    public double MinValue { get => (double)GetValue(MinValueProperty); set => SetValue(MinValueProperty, value); }
    public double MaxValue { get => (double)GetValue(MaxValueProperty); set => SetValue(MaxValueProperty, value); }
    public Brush StrokeX { get => (Brush)GetValue(StrokeXProperty); set => SetValue(StrokeXProperty, value); }
    public Brush StrokeY { get => (Brush)GetValue(StrokeYProperty); set => SetValue(StrokeYProperty, value); }
    public Brush StrokeZ { get => (Brush)GetValue(StrokeZProperty); set => SetValue(StrokeZProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    private double[] _bufX = new double[200];
    private double[] _bufY = new double[200];
    private double[] _bufZ = new double[200];
    private int _head;

    static LiveChart()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(LiveChart), new FrameworkPropertyMetadata(typeof(LiveChart)));
    }

    public LiveChart()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    private void Reset()
    {
        _bufX = new double[WindowSize];
        _bufY = new double[WindowSize];
        _bufZ = new double[WindowSize];
        _head = 0;
        InvalidateVisual();
    }

    public void AddSample(double x, double y, double z)
    {
        _bufX[_head] = x;
        _bufY[_head] = y;
        _bufZ[_head] = z;
        _head = (_head + 1) % _bufX.Length;
    }

    public void Render() => InvalidateVisual();

    public void Clear()
    {
        Array.Clear(_bufX);
        Array.Clear(_bufY);
        Array.Clear(_bufZ);
        _head = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        // Background transparent; caller's Border paints.
        // Zero line
        var zeroY = h * 0.5;
        var gridPen = new Pen(GridBrush, 1);
        gridPen.Freeze();
        dc.DrawLine(gridPen, new Point(0, zeroY), new Point(w, zeroY));

        // Horizontal gridlines at quarters
        dc.DrawLine(gridPen, new Point(0, h * 0.25), new Point(w, h * 0.25));
        dc.DrawLine(gridPen, new Point(0, h * 0.75), new Point(w, h * 0.75));

        double alpha = IsPaused ? 0.35 : 1.0;
        if (ShowX) DrawSeries(dc, _bufX, StrokeX, w, h, alpha);
        if (ShowY) DrawSeries(dc, _bufY, StrokeY, w, h, alpha);
        if (ShowZ) DrawSeries(dc, _bufZ, StrokeZ, w, h, alpha);
    }

    private void DrawSeries(DrawingContext dc, double[] buf, Brush stroke, double w, double h, double alpha)
    {
        int n = buf.Length;
        if (n < 2) return;

        var b = stroke;
        if (alpha < 1.0 && stroke is SolidColorBrush scb)
        {
            var c = scb.Color;
            b = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(255 * alpha), c.R, c.G, c.B));
            b.Freeze();
        }
        var pen = new Pen(b, 1.6);
        pen.Freeze();

        double range = MaxValue - MinValue;
        if (range <= 0) range = 1;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            double xStep = w / (n - 1);
            bool started = false;
            for (int i = 0; i < n; i++)
            {
                int idx = (_head + i) % n;
                double v = buf[idx];
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                double normalized = (v - MinValue) / range;
                normalized = Math.Clamp(normalized, 0, 1);
                double y = h - normalized * h;
                var p = new Point(i * xStep, y);
                if (!started) { ctx.BeginFigure(p, false, false); started = true; }
                else ctx.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
