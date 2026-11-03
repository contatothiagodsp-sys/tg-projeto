using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ThgOptimization.Core.Benchmark;

namespace ThgOptimization.Views;

/// <summary>Curvas de frametime de A (cinza) e B (ciano) no mesmo eixo, com P99 e P99,9 de cada uma e os números da comparação ao lado.</summary>
public partial class FrametimeCompareChart : UserControl
{
    private const double LeftAxis = 44, BottomAxis = 20, MinSeconds = 60;

    private static readonly Brush GrayLine = Frozen(0x9C, 0xA3, 0xAF), CyanLine = Frozen(0x00, 0xE5, 0xFF);
    private static readonly Brush GridLine = Frozen(0x6B, 0x72, 0x80, 0x55), AxisText = Frozen(0x9C, 0xA3, 0xAF);
    private static readonly Brush GoodText = Frozen(0x00, 0xC8, 0x6E), BadText = Frozen(0xFF, 0x38, 0x60);
    private static readonly DoubleCollection P99Dash = FrozenDash(2, 3), P999Dash = FrozenDash(6, 3);

    private AbComparison? _result;
    private IReadOnlyList<FramePoint> _a = Array.Empty<FramePoint>(), _b = Array.Empty<FramePoint>();

    public FrametimeCompareChart()
    {
        InitializeComponent();
        LegendSwatchA.Fill = GrayLine;
        LegendSwatchB.Fill = CyanLine;
        Plot.SizeChanged += (_, _) => Draw();
    }

    public void Show(AbComparison result, IReadOnlyList<FramePoint> a, IReadOnlyList<FramePoint> b)
    {
        _result = result;
        _a = FrametimeSeries.Downsample(a);
        _b = FrametimeSeries.Downsample(b);
        LegendA.Text = $"A · {result.A.ConfigLabel} ({result.CapturesA})";
        LegendB.Text = $"B · {result.B.ConfigLabel} ({result.CapturesB})";
        NoData.Visibility = _a.Count == 0 && _b.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildNumbers(result);
        Draw();
    }

    private void Draw()
    {
        Plot.Children.Clear();
        double width = Plot.ActualWidth - LeftAxis, height = Plot.ActualHeight - BottomAxis;
        if (_result is not { } r || width <= 0 || height <= 0) return;

        double maxMs = FrametimeSeries.AxisMaxMs(r);
        double seconds = Math.Max(MinSeconds, Math.Max(_a.LastOrDefault().Seconds, _b.LastOrDefault().Seconds));

        for (int i = 0; i <= 4; i++)
        {
            double ms = maxMs * i / 4, y = FrametimeSeries.PlotY(ms, maxMs, height);
            AddLine(LeftAxis, y, LeftAxis + width, y, GridLine, null);
            AddLabel($"{ms:0.#}", 0, Math.Clamp(y - 8, 0, height - 8), AxisText);
        }
        for (int s = 0; s <= seconds; s += 10)
            AddLabel($"{s} s", LeftAxis + width * s / seconds - (s == 0 ? 0 : 12), height + 3, AxisText);

        AddCurve(_a, GrayLine, seconds, maxMs, width, height);
        AddCurve(_b, CyanLine, seconds, maxMs, width, height);
        // Os rótulos de A ficam à esquerda e os de B à direita: com configurações parecidas, as linhas quase se tocam.
        AddMarker("P99", r.A.P99Ms, GrayLine, P99Dash, maxMs, width, height, leftSide: true);
        AddMarker("P99,9", r.A.P999Ms, GrayLine, P999Dash, maxMs, width, height, leftSide: true);
        AddMarker("P99", r.B.P99Ms, CyanLine, P99Dash, maxMs, width, height, leftSide: false);
        AddMarker("P99,9", r.B.P999Ms, CyanLine, P999Dash, maxMs, width, height, leftSide: false);
    }

    private void AddCurve(IReadOnlyList<FramePoint> series, Brush stroke, double seconds, double maxMs, double width, double height)
    {
        if (series.Count < 2) return;
        var points = new PointCollection(series.Count);
        foreach (var p in series)
            points.Add(new Point(LeftAxis + width * p.Seconds / seconds, FrametimeSeries.PlotY(p.Ms, maxMs, height)));
        points.Freeze();
        Plot.Children.Add(new Polyline { Points = points, Stroke = stroke, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Bevel });
    }

    private void AddMarker(string name, double ms, Brush stroke, DoubleCollection dash, double maxMs, double width, double height, bool leftSide)
    {
        if (ms <= 0) return;
        double y = FrametimeSeries.PlotY(ms, maxMs, height);
        AddLine(LeftAxis, y, LeftAxis + width, y, stroke, dash);
        AddLabel(name, leftSide ? LeftAxis + 4 : LeftAxis + width - 40, Math.Max(0, y - 15), stroke);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush stroke, DoubleCollection? dash) =>
        Plot.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = 1, StrokeDashArray = dash });

    private void AddLabel(string text, double x, double y, Brush brush)
    {
        var label = new TextBlock { Text = text, FontSize = 11, Foreground = brush };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        Plot.Children.Add(label);
    }

    private void BuildNumbers(AbComparison r)
    {
        Numbers.Children.Clear();
        Numbers.Children.Add(new TextBlock { Text = r.Verdict, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Foreground = VerdictBrush(r.Kind) });
        Numbers.Children.Add(Row("", "A", "B", "Δ", AxisText));
        foreach (var row in AbTable.Rows(r))
            Numbers.Children.Add(Row(row.Metric, row.A, row.B, row.Delta, row.IsBetter switch { true => GoodText, false => BadText, null => AxisText }));
    }

    private static Brush VerdictBrush(AbVerdictKind kind) => kind switch
    {
        AbVerdictKind.Improvement or AbVerdictKind.SmallImprovement => GoodText,
        AbVerdictKind.Regression => BadText,
        _ => AxisText,
    };

    private static Grid Row(string metric, string a, string b, string delta, Brush deltaBrush)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        void Cell(string text, int column, Brush brush)
        {
            var cell = new TextBlock { Text = text, FontSize = 12, Foreground = brush, TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Right };
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
        Cell(metric, 0, AxisText);
        Cell(a, 1, GrayLine);
        Cell(b, 2, CyanLine);
        Cell(delta, 3, deltaBrush);
        return grid;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static DoubleCollection FrozenDash(double on, double off)
    {
        var dash = new DoubleCollection { on, off };
        dash.Freeze();
        return dash;
    }
}
