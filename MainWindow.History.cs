using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThgOptimization.Core.Benchmark;

namespace ThgOptimization;

/// <summary>Jogos → Histórico: o que aconteceu em cada sessão, com frametime e sensores no mesmo eixo de tempo.</summary>
public partial class MainWindow
{
    private sealed record HistoryRow(string Path, string Game, string Line)
    {
        public string SummaryLabel => $"{Game} · {Line}";
    }
    private sealed record HistoryCompareNone(string SummaryLabel);

    private List<HistoryRow> _historyAll = new();
    private SessionTimeline? _historySession;
    private string? _historySelectedPath;
    private bool _historyLoading;

    private async void InitHistory()
    {
        if (_historyLoading) return;
        _historyLoading = true;
        await Infrastructure.UiGuard.RunAsync("sessões", async () =>
        {
            var list = await Task.Run(() => SessionStore.Default.List());
            _historyAll = list.Select(s => new HistoryRow(s.Path, s.Header.Game, s.Header.Line)).ToList();
            string? chosen = HistoryGame.SelectedItem as string;
            var games = new List<string> { "Todos os jogos" };
            games.AddRange(_historyAll.Select(r => r.Game).Distinct().OrderBy(g => g, StringComparer.CurrentCulture));
            HistoryGame.ItemsSource = games;
            HistoryGame.SelectedItem = chosen != null && games.Contains(chosen) ? chosen : games[0];
            HistorySub.Text = _historyAll.Count == 0
                ? "Nenhuma sessão ainda. Com o Modo Jogo ligado, cada jogo vira uma linha do tempo aqui."
                : $"{_historyAll.Count} sessões guardadas neste PC (as {SessionStore.MaxSessions} mais recentes)";
            FilterHistory();
        }, ex => HistorySub.Text = "Não foi possível ler as sessões: " + Err(ex));
        _historyLoading = false;
    }

    private void HistoryGame_Changed(object sender, SelectionChangedEventArgs e) => FilterHistory();

    private void FilterHistory()
    {
        string? game = HistoryGame.SelectedIndex > 0 ? HistoryGame.SelectedItem as string : null;
        var shown = _historyAll.Where(r => game == null || r.Game == game).ToList();
        HistoryList.ItemsSource = shown;
        if (HistoryList.SelectedItem == null && shown.Count > 0) HistoryList.SelectedIndex = 0;
    }

    private async void HistoryList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;
        _historySelectedPath = row.Path;
        await Infrastructure.UiGuard.RunAsync("sessões", async () =>
        {
            var session = await Task.Run(() => SessionStore.Load(row.Path));
            if (!ReferenceEquals(HistoryList.SelectedItem, row)) return; // o usuário já escolheu outra
            _historySession = session;
            HistoryChart.Show(session);
            HistoryStatus.Text = session == null ? "Este arquivo não pôde ser aberto: está corrompido ou incompleto." : "";
            ShowHistoryStats(session);
            RefreshHistoryCompareOptions(row);
        }, ex => HistoryStatus.Text = "Não foi possível abrir: " + Err(ex));
    }

    private void RefreshHistoryCompareOptions(HistoryRow current)
    {
        var candidates = new List<object> { new HistoryCompareNone("Selecione outra sessão para comparar…") };
        candidates.AddRange(_historyAll.Where(r => r.Path != current.Path && string.Equals(r.Game, current.Game, StringComparison.OrdinalIgnoreCase)));
        HistoryCompareCombo.ItemsSource = candidates;
        HistoryCompareCombo.SelectedIndex = 0;
        HistoryCompareText.Visibility = Visibility.Collapsed;
        HistoryCompareText.Text = "";
    }

    private void HistoryTrack_Click(object sender, RoutedEventArgs e)
    {
        HistoryChart.ShowLoadTrack = HistoryTrackLoad.IsChecked == true;
        HistoryChart.ShowResourceTrack = HistoryTrackMem.IsChecked == true;
        HistoryChart.ShowNetworkTrack = HistoryTrackNet.IsChecked == true;
    }

    private void HistoryWorst_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SessionFrameInfo info })
            HistoryChart.FocusAt(info.AtSeconds, windowSeconds: 16);
    }

    private async void HistoryCompare_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_historySession is not { } current || HistoryCompareCombo.SelectedItem is not HistoryRow other)
        {
            HistoryCompareText.Visibility = Visibility.Collapsed;
            return;
        }
        string? selectedPath = _historySelectedPath;
        await Infrastructure.UiGuard.RunAsync("sessões", async () =>
        {
            var baseline = await Task.Run(() => SessionStore.Load(other.Path));
            if (_historySelectedPath != selectedPath || !ReferenceEquals(HistoryCompareCombo.SelectedItem, other)) return;
            if (baseline == null)
            {
                HistoryCompareText.Text = "Não foi possível abrir a sessão escolhida para comparação.";
                HistoryCompareText.Visibility = Visibility.Visible;
                return;
            }
            var pA = baseline.Percentiles();
            var pB = current.Percentiles();
            string Delta(double before, double after, string fmt, string unit, bool higherBetter)
            {
                double diff = after - before;
                string arrow = Math.Abs(diff) < 0.05 ? "=" : (diff > 0 == higherBetter ? "▲" : "▼");
                return $"{after.ToString(fmt)}{unit} vs {before.ToString(fmt)}{unit} ({arrow} {diff:+0.#;-0.#;0}{unit})";
            }
            HistoryCompareText.Text = $"vs {baseline.Header.Start:dd/MM HH:mm} · FPS {Delta(baseline.Header.AvgFps, current.Header.AvgFps, "0", "", true)} · " +
                                      $"1% low {Delta(baseline.Header.Low1Fps, current.Header.Low1Fps, "0", "", true)} · " +
                                      $"P99 {Delta(pA.P99, pB.P99, "0.0", " ms", false)} · " +
                                      $"Engasgos {Delta(baseline.Header.Stutters, current.Header.Stutters, "0", "", false)}";
            HistoryCompareText.Visibility = Visibility.Visible;
        }, ex => HistoryStatus.Text = "Não foi possível comparar: " + Err(ex));
    }

    private void ShowHistoryStats(SessionTimeline? session)
    {
        if (session == null)
        {
            HistoryPercentiles.Text = "";
            HistoryWorst.ItemsSource = null;
            return;
        }
        var p = session.Percentiles();
        HistoryPercentiles.Text = $"p50 {p.P50:0.0} ms · p95 {p.P95:0.0} ms · p99 {p.P99:0.0} ms · p99,9 {p.P999:0.0} ms" +
                                  (session.FramesMs.Count == 0 ? " · sem gravação de frames: valores por segundo" : "");
        HistoryWorst.ItemsSource = session.WorstFrames();
    }

    private async void HistoryExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_historySession is not { } session) return;
        await Infrastructure.UiGuard.RunButtonAsync(sender as UIElement, "sessões", async () =>
        {
            Directory.CreateDirectory(FrameAnalysis.CaptureFolder);
            string path = Path.Combine(FrameAnalysis.CaptureFolder, $"{session.Header.Start:yyyyMMdd-HHmmss}-{SessionStore.Slug(session.Header.Game)}-presentmon.csv");
            string csv = await Task.Run(session.ToPresentMonCsv);
            await File.WriteAllTextAsync(path, csv);
            HistoryStatus.Text = $"CSV salvo: {path}";
        }, ex => HistoryStatus.Text = "Não foi possível salvar: " + Err(ex));
    }

    private void HistorySavePng_Click(object sender, RoutedEventArgs e)
    {
        if (_historySession is not { } session || HistoryChart.ActualWidth < 1) return;
        try
        {
            Directory.CreateDirectory(FrameAnalysis.CaptureFolder);
            string path = Path.Combine(FrameAnalysis.CaptureFolder, $"{session.Header.Start:yyyyMMdd-HHmmss}-{SessionStore.Slug(session.Header.Game)}.png");
            double dpi = VisualTreeHelper.GetDpi(HistoryChart).PixelsPerInchX;
            var bitmap = new RenderTargetBitmap((int)(HistoryChart.ActualWidth * dpi / 96), (int)(HistoryChart.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            // O gráfico não tem fundo próprio: sem este, o PNG sairia transparente, ilegível em visualizador de fundo claro.
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var rect = new Rect(0, 0, HistoryChart.ActualWidth, HistoryChart.ActualHeight);
                dc.DrawRectangle((Brush)FindResource("Bg1Brush"), null, rect);
                dc.DrawRectangle(new VisualBrush(HistoryChart), null, rect);
            }
            bitmap.Render(visual);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            png.Save(file);
            HistoryStatus.Text = $"Imagem salva: {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Infrastructure.AppLog.Warn("sessões", ex);
            HistoryStatus.Text = "Não foi possível salvar: " + Err(ex);
        }
    }

    private void HistoryOpenLab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(BenchmarkReport.Folder);
            OpenShell(BenchmarkReport.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { HistoryStatus.Text = "Não foi possível abrir: " + Err(ex); }
    }
}
