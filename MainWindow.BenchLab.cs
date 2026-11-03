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

    private void InitBenchLab()
    {
        LabTweakCombo.ItemsSource = _tweaks;
        var run = _settings.BenchLabTweakRun;
        LabTweakCombo.SelectedItem = _tweaks.FirstOrDefault(t => t.Id == run?.TweakId) ?? _tweaks.FirstOrDefault();
        UpdateLabUi();
    }

    private Tweak? LabTweak => LabTweakCombo.SelectedItem as Tweak;

    private void LabTweakCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => _ = RefreshLabTweakGuarded();

    private Task RefreshLabTweakGuarded() =>
        UiGuard.RunAsync("lab", RefreshLabTweakAsync, ex => LabTweakHint.Text = $"Não foi possível ler o ajuste: {Err(ex)}");

    /// <summary>Lê o estado real antes de decidir os botões: o ajuste pode ter mudado na página Otimizar.</summary>
    private async Task RefreshLabTweakAsync()
    {
        if (LabTweak is not { } tweak) return;
        tweak.IsOn = await Task.Run(() => tweak.Read() ?? false);
        if (ReferenceEquals(LabTweak, tweak)) UpdateLabTweakUi(tweak);
    }

    private static DateTime BootTime => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    private void UpdateLabTweakUi(Tweak t)
    {
        var run = _settings.BenchLabTweakRun;
        int before = run?.TweakId == t.Id ? _labSessions.Count(s => s.ExperimentId == run.ExperimentId && s.Slot == "A") : 0;
        var step = BenchLabFlow.StepOf(t.Id, t.IsOn, t.NeedsRestart, run, before, BootTime);

        BtnLabTweakBefore.IsEnabled = !_capturing && step is LabTweakStep.NeedsBefore or LabTweakStep.ReadyToApply;
        BtnLabTweakApply.IsEnabled = !_capturing && step is LabTweakStep.ReadyToApply or LabTweakStep.ReadyToMeasure;
        BtnLabTweakApply.Content = step == LabTweakStep.ReadyToMeasure ? "MEDIR COM O AJUSTE" : "APLICAR E MEDIR";
        BtnLabTweakRevert.IsEnabled = !_capturing && step is LabTweakStep.WaitingRestart or LabTweakStep.ReadyToMeasure;

        string hint = step switch
        {
            LabTweakStep.NeedsBefore => "Abra o jogo e grave o \"antes\". Repita a mesma cena: 3 capturas de cada lado dão um veredito confiável.",
            LabTweakStep.ReadyToApply => $"{LabCountText(before)} sem o ajuste. Repita a medição ou aplique.",
            LabTweakStep.WaitingRestart => "Ajuste aplicado. Ele só vale depois de reiniciar o PC: reinicie e volte aqui para medir. Medir agora mediria a configuração antiga.",
            LabTweakStep.ReadyToMeasure => "Grave a mesma cena com o ajuste ligado. Depois, mantenha o ajuste com NOVO TESTE ou desfaça com REVERTER AJUSTE.",
            LabTweakStep.AlreadyApplied => "Este ajuste já está aplicado, e sem ele desligado não há \"antes\" para comparar. Desligue na página Otimizar para testar.",
            _ => $"O teste de outro ajuste está em andamento. Volte a ele e reverta, ou use NOVO TESTE para mantê-lo aplicado.",
        };
        if (t.NeedsRestart && step is LabTweakStep.NeedsBefore or LabTweakStep.ReadyToApply) hint += " Este ajuste exige reiniciar o PC antes da segunda medição.";
        if (t.AvoidFor(_cpuName) is { } avoid) hint += $" Atenção: {avoid}";
        LabTweakHint.Text = hint;
    }

    private async void BtnLabTweakBefore_Click(object sender, RoutedEventArgs e)
    {
        if (LabTweak is not { } tweak) return;
        if (_settings.BenchLabTweakRun?.TweakId != tweak.Id)
        {
            StartNewLabExperiment();
            (LabLabelA.Text, LabLabelB.Text) = BenchLabFlow.TweakLabels(tweak.Title);
            _settings.BenchLabTweakRun = new LabTweakRun(tweak.Id, _labExperiment);
            _settings.Save();
        }
        await LabCaptureGuarded("A", countdown: true);
    }

    private async void BtnLabTweakApply_Click(object sender, RoutedEventArgs e)
    {
        if (LabTweak is not { } tweak || _settings.BenchLabTweakRun is not { } run || run.TweakId != tweak.Id) return;
        await UiGuard.RunAsync("lab", async () =>
        {
            // Aplica também se foi desligado por fora depois de aplicado: senão a captura "com o ajuste" mediria sem ele.
            if (run.AppliedAt == null || !await Task.Run(() => tweak.Read() ?? false))
            {
                await Task.Run(() => tweak.Write(true, _settings));
                _settings.Save();
                tweak.IsOn = await Task.Run(() => tweak.Read() ?? false);
                if (!tweak.IsOn)
                {
                    LabStatus.Text = "O Windows não aceitou o ajuste. Nada foi medido.";
                    return;
                }
                _settings.BenchLabTweakRun = run with { AppliedAt = DateTime.Now };
                _settings.Save();
                if (tweak.NeedsRestart)
                {
                    LabStatus.Text = $"{tweak.Title}: aplicado. Reinicie o PC e volte aqui para a segunda medição.";
                    return;
                }
            }
            await LabCaptureAsync("B", countdown: true);
        }, ex => LabStatus.Text = $"Não foi possível aplicar e medir: {Err(ex)}");
        await RefreshLabTweakGuarded();
    }

    private async void BtnLabTweakRevert_Click(object sender, RoutedEventArgs e)
    {
        if (LabTweak is not { } tweak) return;
        await UiGuard.RunAsync("lab", async () =>
        {
            bool off = await Task.Run(() => BenchLabFlow.RevertTweak(tweak, _settings));
            _settings.BenchLabTweakRun = null;
            _settings.Save();
            LabStatus.Text = !off ? "O Windows não aceitou desfazer o ajuste. Confira na página Otimizar."
                : tweak.NeedsRestart ? $"{tweak.Title}: revertido. Reinicie o PC para valer."
                : $"{tweak.Title}: revertido.";
        }, ex => LabStatus.Text = $"Não foi possível reverter: {Err(ex)}");
        await RefreshLabTweakGuarded();
    }

    private void UpdateLabUi()
    {
        int a = LabCaptures("A").Count, b = LabCaptures("B").Count;
        LabCountA.Text = LabCountText(a);
        LabCountB.Text = LabCountText(b);
        // O rótulo fica preso às capturas já feitas: editar depois mudaria o nome do que foi medido.
        LabLabelA.IsEnabled = a == 0;
        LabLabelB.IsEnabled = b == 0;
        // No teste de um ajuste, cada lado depende do estado dele: quem grava são os botões da seção do ajuste.
        bool free = !_capturing && _settings.BenchLabTweakRun == null;
        foreach (var button in new[] { BtnLabCaptureA, BtnLabCaptureB, BtnLabRepeat }) button.IsEnabled = free;
        BtnLabNew.IsEnabled = !_capturing;
        BtnLabCompare.IsEnabled = !_capturing && a > 0 && b > 0;
        BtnLabExport.IsEnabled = _labResult != null;
        if (LabTweak is { } tweak) UpdateLabTweakUi(tweak);
    }

    private static string LabCountText(int n) => n == 1 ? "1 captura" : $"{n} capturas";

    private async void BtnLabCaptureA_Click(object sender, RoutedEventArgs e) => await LabCaptureGuarded("A", countdown: true);
    private async void BtnLabCaptureB_Click(object sender, RoutedEventArgs e) => await LabCaptureGuarded("B", countdown: true);

    private async void BtnLabRepeat_Click(object sender, RoutedEventArgs e) =>
        await LabCaptureGuarded(BenchLabFlow.NextSlot(LabCaptures("A").Count, LabCaptures("B").Count), countdown: true);

    private void BtnLabCompare_Click(object sender, RoutedEventArgs e) => LabCompare();

    private void BtnLabNew_Click(object sender, RoutedEventArgs e)
    {
        var kept = _settings.BenchLabTweakRun is { AppliedAt: not null } run ? _tweaks.FirstOrDefault(t => t.Id == run.TweakId) : null;
        StartNewLabExperiment();
        _settings.BenchLabTweakRun = null;
        _settings.Save();
        LabStatus.Text = kept != null
            ? $"Novo teste. {kept.Title} continua aplicado: para desfazer, use a página Otimizar."
            : "Novo teste: grave a configuração A.";
        UpdateLabUi();
    }

    private void StartNewLabExperiment()
    {
        _labExperiment = NewLabExperiment();
        _labResult = null;
        LabVerdict.Text = "";
        LabDetail.Text = "";
        LabChart.Visibility = Visibility.Collapsed;
        LabLabelA.IsEnabled = LabLabelB.IsEnabled = true;
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
        BtnLabExport.IsEnabled = true;
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

    private async void BtnLabExport_Click(object sender, RoutedEventArgs e)
    {
        if (_labResult is not { } r) return;
        await UiGuard.RunAsync("lab", async () =>
        {
            string path = await SaveLabReportAsync(r);
            _labResult = r with { ReportPath = path };
            OpenLabReport(path);
            LabStatus.Text = $"Relatório salvo: {path}";
        }, ex => LabStatus.Text = $"Não foi possível salvar o relatório: {Err(ex)}");
    }

    private static async Task<string> SaveLabReportAsync(AbComparison r)
    {
        var (a, b) = await Task.Run(() => (FrametimeSeries.Load(r.A.CsvPath), FrametimeSeries.Load(r.B.CsvPath)));
        byte[] png = Views.FrametimeCompareChart.RenderPng(r, a, b); // WPF: precisa da thread da janela
        return await Task.Run(() => BenchmarkReport.Save(r, png));
    }

    /// <summary>Pelo Explorer, para o navegador não herdar o administrador do tHg.</summary>
    private static void OpenLabReport(string path) => Unelevated.Start("explorer.exe", $"\"{path}\"", null);

    /// <summary>O CSV de uma captura tem dezenas de milhares de linhas: é lido fora da janela.</summary>
    private async Task ShowLabChartAsync(AbComparison r)
    {
        var (a, b) = await Task.Run(() => (FrametimeSeries.Load(r.A.CsvPath), FrametimeSeries.Load(r.B.CsvPath)));
        if (!ReferenceEquals(_labResult, r)) return; // outra comparação chegou enquanto lia
        LabChart.Visibility = Visibility.Visible;
        LabChart.Show(r, a, b);
    }
}
