using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ThgOptimization.Core.Benchmark;

namespace ThgOptimization.Views;

/// <summary>
/// Gráfico de barras agrupadas para percentis de frametime (P50, P95, P99, P99,9 ms) entre A e B,
/// destacando reduções ou aumentos de latência na cauda.
/// </summary>
public sealed class FrametimeDistributionChart : FrameworkElement
{
    private const double LeftPad = 48, RightPad = 16, TopPad = 24, BottomPad = 28;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    private AbComparison? _result;

    public FrametimeDistributionChart()
    {
        ClipToBounds = true;
    }

    public void Show(AbComparison? result)
    {
        _result = result;
        InvalidateVisual();
    }

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private FormattedText Text(string text, Brush brush, double size = 10) =>
        new(text, Pt, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_result is not { } r) return;

        var items = new List<(string Label, double A, double B)>
        {
            ("P50", r.A.MedianFrameTimeMs, r.B.MedianFrameTimeMs),
        };
        if (r.A.P95Ms > 0 || r.B.P95Ms > 0) items.Add(("P95", r.A.P95Ms, r.B.P95Ms));
        items.Add(("P99", r.A.P99Ms, r.B.P99Ms));
        items.Add(("P99,9", r.A.P999Ms, r.B.P999Ms));

        double maxMs = Math.Max(1, items.SelectMany(x => new[] { x.A, x.B }).DefaultIfEmpty(1).Max() * 1.18);
        double plotW = Math.Max(ActualWidth - LeftPad - RightPad, 1);
        double plotH = Math.Max(ActualHeight - TopPad - BottomPad, 10);

        dc.DrawText(Text("DISTRIBUIÇÃO DE FRAMETIME (ms · menor é melhor)", Res("Faint"), 10), new Point(LeftPad, 2));

        var grid = new Pen(Res("Line"), 1);
        grid.Freeze();
        for (int k = 0; k <= 2; k++)
        {
            double val = maxMs * k / 2.0;
            double y = TopPad + plotH * (1 - val / maxMs);
            dc.DrawLine(grid, new Point(LeftPad, y), new Point(LeftPad + plotW, y));
            dc.DrawText(Text($"{val:0.#}", Res("Faint")), new Point(4, Math.Clamp(y - 7, 0, ActualHeight - BottomPad)));
        }

        double groupW = plotW / items.Count;
        double barW = Math.Clamp(groupW * 0.24, 12, 34);
        double gap = 6;
        Brush brushA = Res("Muted"), brushB = Res("Accent");

        for (int i = 0; i < items.Count; i++)
        {
            var (label, va, vb) = items[i];
            double cx = LeftPad + groupW * (i + 0.5);

            void DrawBar(double x, double val, Brush fill)
            {
                double h = Math.Max(2, plotH * Math.Clamp(val / maxMs, 0, 1));
                double y = TopPad + plotH - h;
                dc.DrawRoundedRectangle(fill, null, new Rect(x, y, barW, h), 3, 3);
                var vt = Text(val > 0 ? val.ToString("0.0", Pt) : "—", Res("Fg"), 9);
                dc.DrawText(vt, new Point(x + (barW - vt.Width) / 2, Math.Max(TopPad - 12, y - 13)));
            }

            DrawBar(cx - barW - gap / 2, va, brushA);
            DrawBar(cx + gap / 2, vb, brushB);

            var lt = Text(label, Res("Fg"), 11);
            dc.DrawText(lt, new Point(cx - lt.Width / 2, TopPad + plotH + 4));

            if (va > 0 && vb > 0)
            {
                double delta = (vb - va) / va * 100;
                if (Math.Abs(delta) >= 0.5)
                {
                    Brush db = delta < 0 ? Res("Good") : Res("Bad");
                    var dt = Text(delta.ToString("+0.#;-0.#", Pt) + "%", db, 9);
                    dc.DrawText(dt, new Point(cx - dt.Width / 2, TopPad + plotH + 17));
                }
            }
        }
    }
}
