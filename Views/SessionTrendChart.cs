using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ThgOptimization.Core.Intelligence;

namespace ThgOptimization.Views;

/// <summary>
/// Gráfico nativo de tendência entre sessões do mesmo jogo (FPS médio, 1% low e saúde da sessão).
/// Desenhado em OnRender, sem alocações por ponto nem dependência externa.
/// </summary>
public sealed class SessionTrendChart : FrameworkElement
{
    private const double LeftAxis = 42, RightPad = 14, TopGap = 14, BottomAxis = 22;

    private List<SessionSummary> _sessions = new();
    private double? _baselineFps;
    private int? _hoverIndex;

    public SessionTrendChart()
    {
        ClipToBounds = true;
    }

    public void Show(IEnumerable<SessionSummary> history, double? baselineFps = null)
    {
        _sessions = history
            .Where(s => s.AvgFps is > 0)
            .OrderBy(s => s.Start)
            .TakeLast(20)
            .ToList();
        _baselineFps = baselineFps;
        _hoverIndex = null;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_sessions.Count < 2) return;
        double w = Math.Max(ActualWidth - LeftAxis - RightPad, 1);
        double frac = Math.Clamp((e.GetPosition(this).X - LeftAxis) / w, 0, 1);
        int idx = (int)Math.Round(frac * (_sessions.Count - 1));
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != null)
        {
            _hoverIndex = null;
            InvalidateVisual();
        }
    }

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private FormattedText Text(string text, Brush brush, double size = 10) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_sessions.Count < 2)
        {
            dc.DrawText(Text("São necessárias ao menos 2 sessões com FPS para traçar a tendência.", Res("Muted"), 12), new Point(8, 16));
            return;
        }

        double plotW = Math.Max(ActualWidth - LeftAxis - RightPad, 1);
        double plotH = Math.Max(ActualHeight - TopGap - BottomAxis, 10);

        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var s in _sessions)
        {
            if (s.AvgFps is double a) { lo = Math.Min(lo, a); hi = Math.Max(hi, a); }
            if (s.Low1Fps is double l) { lo = Math.Min(lo, l); hi = Math.Max(hi, l); }
        }
        if (_baselineFps is double bf) { lo = Math.Min(lo, bf); hi = Math.Max(hi, bf); }
        lo = Math.Max(0, Math.Floor(lo * 0.85));
        hi = Math.Ceiling(Math.Max(hi * 1.1, lo + 10));

        double XAt(int i) => LeftAxis + plotW * i / Math.Max(_sessions.Count - 1, 1);
        double YAt(double v) => TopGap + plotH * (1 - (v - lo) / (hi - lo));

        var gridPen = new Pen(Res("Line"), 1);
        gridPen.Freeze();
        for (int k = 0; k <= 2; k++)
        {
            double val = lo + (hi - lo) * k / 2.0;
            double y = YAt(val);
            dc.DrawLine(gridPen, new Point(LeftAxis, y), new Point(LeftAxis + plotW, y));
            dc.DrawText(Text($"{val:0}", Res("Faint")), new Point(4, Math.Clamp(y - 7, 0, ActualHeight - BottomAxis)));
        }

        if (_baselineFps is double baseVal)
        {
            double by = YAt(baseVal);
            var basePen = new Pen(Res("Muted"), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            basePen.Freeze();
            dc.DrawLine(basePen, new Point(LeftAxis, by), new Point(LeftAxis + plotW, by));
            dc.DrawText(Text($"ref {baseVal:0}", Res("Faint"), 9), new Point(LeftAxis + 4, Math.Max(0, by - 12)));
        }

        DrawSeries(dc, s => s.AvgFps, Res("Accent"), XAt, YAt);
        DrawSeries(dc, s => s.Low1Fps, Res("Good"), XAt, YAt);

        for (int i = 0; i < _sessions.Count; i++)
        {
            var s = _sessions[i];
            if (s.AvgFps is not double fps) continue;
            var pt = new Point(XAt(i), YAt(fps));
            Brush dot = s.HealthScore is int hs ? Res(hs >= 85 ? "Good" : hs >= 60 ? "Warn" : "Bad") : Res("Accent");
            dc.DrawEllipse(dot, null, pt, i == _hoverIndex ? 4.5 : 3, i == _hoverIndex ? 4.5 : 3);
        }

        dc.DrawText(Text(_sessions[0].Start.ToString("dd/MM"), Res("Faint")), new Point(LeftAxis, ActualHeight - BottomAxis + 4));
        var lastLbl = Text(_sessions[^1].Start.ToString("dd/MM"), Res("Faint"));
        dc.DrawText(lastLbl, new Point(LeftAxis + plotW - lastLbl.Width, ActualHeight - BottomAxis + 4));

        // Legenda no topo
        dc.DrawText(Text("■ FPS médio", Res("Accent"), 10), new Point(LeftAxis, 0));
        dc.DrawText(Text("■ 1% low", Res("Good"), 10), new Point(LeftAxis + 76, 0));

        if (_hoverIndex is int hiIdx && hiIdx >= 0 && hiIdx < _sessions.Count)
        {
            var s = _sessions[hiIdx];
            string tip = $"{s.Start:dd/MM HH:mm} · {s.AvgFps:0} FPS"
                       + (s.Low1Fps is double l1 ? $" · 1% {l1:0}" : "")
                       + (s.P99Ms is double p99 ? $" · P99 {p99:0.0} ms" : "")
                       + (s.HealthScore is int hs ? $" · Saúde {hs}" : "");
            var ft = Text(tip, Res("Fg"), 10);
            double x = Math.Clamp(XAt(hiIdx) - ft.Width / 2, LeftAxis, Math.Max(LeftAxis, ActualWidth - ft.Width - 10));
            var box = new Rect(x, TopGap, ft.Width + 10, ft.Height + 6);
            dc.DrawRoundedRectangle(Res("Bg1Brush"), new Pen(Res("Line"), 1), box, 4, 4);
            dc.DrawText(ft, new Point(box.X + 5, box.Y + 3));
        }
    }

    private void DrawSeries(DrawingContext dc, Func<SessionSummary, double?> read, Brush brush, Func<int, double> xAt, Func<double, double> yAt)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            bool open = false;
            for (int i = 0; i < _sessions.Count; i++)
            {
                if (read(_sessions[i]) is not double v) { open = false; continue; }
                var p = new Point(xAt(i), yAt(v));
                if (!open) { ctx.BeginFigure(p, false, false); open = true; }
                else ctx.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        var pen = new Pen(brush, 1.6);
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
