using System.Windows;
using ThgOptimization.Core.Benchmark;
using ThgOptimization.Infrastructure;

namespace ThgOptimization;

/// <summary>Performance Lab: grava a mesma cena em duas configurações e diz se a diferença é real ou ruído.</summary>
public partial class MainWindow
{
    private readonly List<BenchmarkSession> _labSessions = new();
    private string _labExperiment = NewLabExperiment();
    private CancellationTokenSource? _labStop;
    private AbComparison? _labResult;

    private static string NewLabExperiment() => Guid.NewGuid().ToString("N");

    private List<BenchmarkSession> LabCaptures(string slot) =>
        _labSessions.Where(s => s.ExperimentId == _labExperiment && s.Slot == slot).ToList();

    private string? LabGame => _labSessions.FirstOrDefault(s => s.ExperimentId == _labExperiment)?.Game;

    private string LabLabel(string slot)
    {
        string text = (slot == "A" ? LabLabelA : LabLabelB).Text.Trim();
        return text.Length > 0 ? text : slot;
    }

    private void InitBenchLab() => UpdateLabUi();

    private void UpdateLabUi()
    {
        int a = LabCaptures("A").Count, b = LabCaptures("B").Count;
        LabCountA.Text = LabCountText(a);
        LabCountB.Text = LabCountText(b);
        // O rótulo fica preso às capturas já feitas: editar depois mudaria o nome do que foi medido.
        LabLabelA.IsEnabled = a == 0;
        LabLabelB.IsEnabled = b == 0;
        foreach (var button in new[] { BtnLabCaptureA, BtnLabCaptureB, BtnLabRepeat, BtnLabNew }) button.IsEnabled = !_capturing;
        BtnLabCompare.IsEnabled = !_capturing && a > 0 && b > 0;
    }

    private static string LabCountText(int n) => n == 1 ? "1 captura" : $"{n} capturas";

    private async void BtnLabCaptureA_Click(object sender, RoutedEventArgs e) => await LabCaptureGuarded("A", countdown: true);
    private async void BtnLabCaptureB_Click(object sender, RoutedEventArgs e) => await LabCaptureGuarded("B", countdown: true);

    private async void BtnLabRepeat_Click(object sender, RoutedEventArgs e) =>
        await LabCaptureGuarded(BenchLabFlow.NextSlot(LabCaptures("A").Count, LabCaptures("B").Count), countdown: true);

    private void BtnLabCompare_Click(object sender, RoutedEventArgs e) => LabCompare();

    private void BtnLabNew_Click(object sender, RoutedEventArgs e)
    {
        _labExperiment = NewLabExperiment();
        _labResult = null;
        LabVerdict.Text = "";
        LabDetail.Text = "";
        LabChart.Visibility = Visibility.Collapsed;
        LabStatus.Text = "Novo teste: grave a configuração A.";
        UpdateLabUi();
    }

    private Task LabCaptureGuarded(string slot, bool countdown) =>
        UiGuard.RunAsync("lab", () => LabCaptureAsync(slot, countdown), ex => LabStatus.Text = $"A captura falhou: {Err(ex)}");

    private async Task LabCaptureAsync(string slot, bool countdown)
    {
        if (_capturing) return;
        _capturing = true;
        using var stop = _labStop = new CancellationTokenSource();
        BtnCapture.IsEnabled = false;
        UpdateLabUi();
        ManageFrameMonitor(); // liga o medidor, se estava desligado
        try
        {
            string label = LabLabel(slot);
            if (countdown)
                for (int i = 5; i > 0; i--)
                {
                    LabStatus.Text = $"Confira se \"{label}\" está ativa e volte para o jogo: a captura {slot} começa em {i} s…";
                    await Task.Delay(1000);
                }
            int pid = ForegroundPid();
            if (pid == 0 || pid == Environment.ProcessId)
            {
                LabStatus.Text = "Nenhum jogo em primeiro plano. Clique em capturar e volte para o jogo em até 5 segundos.";
                return;
            }
            string game = ProcessUtil.NameOf(pid) ?? $"PID {pid}";
            if (BenchLabFlow.GameConflict(LabGame, game) is { } conflict)
            {
                LabStatus.Text = conflict;
                return;
            }

            Toast($"Capturando {slot}...", $"{game} · {label}: repita a mesma cena.", "");
            var result = await RecordFramesAsync(pid, game, s => LabStatus.Text = $"Capturando {slot} ({label}) em {game}… {s} s", stop.Token);
            if (result == null)
            {
                LabStatus.Text = $"{game} quase não apresentou frames (jogo minimizado, ou usa uma API que o Windows não informa).";
                return;
            }
            if (result.Seconds < BenchLabFlow.MinKeptSeconds)
            {
                LabStatus.Text = $"Captura interrompida com {result.Seconds:0} s: descartada, para não comparar com as de 60 s.";
                return;
            }

            _labSessions.Insert(0, BenchmarkSession.From(result, label, _labExperiment, slot));
            LabStatus.Text = $"Captura {slot} ({label}): {result.Summary}";
            Toast($"Captura {slot} pronta", result.Summary, "");
            if (LabCaptures("A").Count > 0 && LabCaptures("B").Count > 0) LabCompare();
        }
        finally
        {
            _labStop = null;
            _capturing = false;
            BtnCapture.IsEnabled = true;
            UpdateLabUi();
        }
    }

    private void LabCompare()
    {
        var a = LabCaptures("A");
        var b = LabCaptures("B");
        if (a.Count == 0 || b.Count == 0)
        {
            LabStatus.Text = "Faltam capturas: grave as duas configurações.";
            return;
        }
        ShowLabResult(FrameTimeStatistics.Compare(a, b, aIsBaseline: true) with { ComparedAt = DateTime.Now });
    }

    private void ShowLabResult(AbComparison r)
    {
        _labResult = r;
        LabVerdict.Text = r.Verdict;
        LabVerdict.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, r.Kind switch
        {
            AbVerdictKind.Improvement or AbVerdictKind.SmallImprovement => "Good",
            AbVerdictKind.Regression => "Bad",
            _ => "Muted",
        });
        LabDetail.Text = $"{r.A.ConfigLabel} ({r.CapturesA}) x {r.B.ConfigLabel} ({r.CapturesB}): " +
                         $"FPS {r.AvgFpsDeltaPct:+0.0;-0.0}% · 1% low {r.Low1DeltaPct:+0.0;-0.0}% · 0,1% low {r.Low01DeltaPct:+0.0;-0.0}% · engasgos {r.StutterDeltaPct:+0.0;-0.0}%" +
                         (r.CapturesA < 3 || r.CapturesB < 3 ? ". Faça 3 capturas de cada lado para um resultado confiável." : "");
        _ = UiGuard.RunAsync("lab", () => ShowLabChartAsync(r), ex => LabStatus.Text = $"Não foi possível montar o gráfico: {Err(ex)}");
    }

    /// <summary>O CSV de uma captura tem dezenas de milhares de linhas: é lido fora da janela.</summary>
    private async Task ShowLabChartAsync(AbComparison r)
    {
        var (a, b) = await Task.Run(() => (FrametimeSeries.Load(r.A.CsvPath), FrametimeSeries.Load(r.B.CsvPath)));
        if (!ReferenceEquals(_labResult, r)) return; // outra comparação chegou enquanto lia
        LabChart.Visibility = Visibility.Visible;
        LabChart.Show(r, a, b);
    }
}
