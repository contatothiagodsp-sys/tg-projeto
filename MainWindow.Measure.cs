using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ThgOptimization.Core.Performance;

namespace ThgOptimization;

/// <summary>
/// Medir o efeito de um ajuste do Windows que pede reinício: guarda a nota do benchmark, o usuário muda o ajuste e, na abertura seguinte, o mesmo benchmark
/// roda de novo. Proteções nunca são desligadas pelo tHg: a tela do Windows é aberta, e quem muda é o usuário.
/// </summary>
public partial class MainWindow
{
    /// <summary>O "depois" já pode valer: o Windows mudou. Sem isto, qualquer benchmark rodado antes do reinício fecharia a medição com um número errado.</summary>
    private bool _measurementStateChanged;

    private void CheckSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GamerCheck c }) return;
        switch (c.Id)
        {
            case "sec.hvci.impact": BeginHvciMeasurement(c); break;
            case "gpu.hags.advice": BeginHagsMeasurement(c); break;
        }
    }

    /// <summary>O agendamento de GPU não é proteção: com a confirmação, o tHg muda, guarda o valor anterior e pede para reiniciar.</summary>
    private void BeginHagsMeasurement(GamerCheck c)
    {
        if (_tweaks.FirstOrDefault(t => t.Id == "hags") is not { } tweak || tweak.Read() is not bool on) return;
        if (MeasurementBaseline() is not { } baseline) return;
        if (!ThgDialog.Confirm("Medir o agendamento de GPU",
                $"Vou {(on ? "desligar" : "ligar")} o agendamento de GPU e pedir para você reiniciar. Depois de reiniciar, rodo o mesmo benchmark e comparo. Cerca de 10 minutos. " +
                "O ajuste pode ser desfeito em DESFAZER TUDO. Continuar?", "Continuar", "Cancelar")) return;
        SaveMeasurement("hags", baseline, stateAtStart: on);
        tweak.Write(!on, _settings);
        _settings.Save();
        c.Status = "Reinicie o PC e abra o tHg de novo para continuar a medição.";
    }

    private BenchRecord? MeasurementBaseline()
    {
        var baseline = MeasurementFlow.Baseline(_settings.CopyList(s => s.BenchRecords), DateTime.Now);
        if (baseline != null) return baseline;
        ThgDialog.Info("Rode um benchmark antes", "A medição compara o mesmo teste antes e depois da mudança. Rode um benchmark de GPU ou de ray tracing na página BENCH, com o PC como está hoje, e volte aqui.");
        BenchIndividual.IsExpanded = true;
        NavigateTo("PageBench");
        return null;
    }

    private void BeginHvciMeasurement(GamerCheck c)
    {
        if (MeasurementBaseline() is not { } baseline) return;
        if (!ThgDialog.Confirm("Medir o impacto da Integridade de Memória",
                "Vou pedir para você desligar a Integridade de Memória e reiniciar. Depois de reiniciar, rodo o mesmo benchmark e comparo. Cerca de 10 minutos.\n\n" +
                "Enquanto ela estiver desligada, o PC fica sem essa proteção contra drivers maliciosos. Religue depois se preferir a proteção. Continuar?",
                "Continuar", "Cancelar")) return;
        SaveMeasurement("hvci", baseline, stateAtStart: true);
        c.Status = "Desligue a Integridade de Memória na tela do Windows, reinicie o PC e abra o tHg de novo.";
        try { Process.Start(new ProcessStartInfo("windowsdefender://coreisolation") { UseShellExecute = true }); }
        catch (Exception ex) { c.Status = $"Não foi possível abrir: {Err(ex)}"; }
    }

    private void SaveMeasurement(string kind, BenchRecord baseline, bool stateAtStart)
    {
        _settings.PendingMeasurement = new PendingMeasurement
        {
            Kind = kind, BenchmarkKind = baseline.Test, ScoreBefore = baseline.Score, Timestamp = DateTime.Now, StateAtStart = stateAtStart,
        };
        _settings.Save();
    }

    private async Task<bool?> ReadMeasuredStateAsync(string kind) => kind switch
    {
        "hvci" => await Task.Run(() => SystemSecurityAdvisor.ReadStatus().HvciOn),
        "hags" => _tweaks.FirstOrDefault(t => t.Id == "hags")?.Read(),
        _ => null,
    };

    /// <summary>Na abertura: se o ajuste mudou, oferece rodar o "depois". Se não mudou, avisa uma vez só e continua esperando.</summary>
    private async Task ResumeMeasurementAsync()
    {
        if (_settings.PendingMeasurement is not { } pending) return;
        if (await ReadMeasuredStateAsync(pending.Kind) is not bool state)
        {
            Infrastructure.AppLog.Warn("medição", $"Medição descartada: tipo desconhecido ou estado ilegível ({pending.Kind}).");
            _settings.PendingMeasurement = null;
            _settings.Save();
            return;
        }
        string name = pending.Kind == "hvci" ? "A Integridade de Memória" : "O agendamento de GPU";
        switch (MeasurementFlow.Decide(pending, state))
        {
            case MeasurementStep.RemindOnce:
                _settings.PendingMeasurement = pending with { Reminded = true };
                _settings.Save();
                Toast("Medição em espera", $"{name} continua como antes, então o benchmark não rodou. Mude o ajuste, reinicie e abra o tHg de novo.", "");
                break;
            case MeasurementStep.RunBenchmark:
                _measurementStateChanged = true;
                if (ThgDialog.Confirm("Continuar a medição", $"{name} mudou. Rodar agora o mesmo benchmark para comparar? Feche o que estiver aberto antes.", "Rodar agora", "Depois"))
                    await RunMeasurementBenchmarkAsync(pending);
                break;
        }
    }

    private async Task RunMeasurementBenchmarkAsync(PendingMeasurement pending)
    {
        BenchIndividual.IsExpanded = true;
        NavigateTo("PageBench");
        string test = pending.BenchmarkKind;
        if (test == "gpu")
        {
            SelectTagged(BenchTestPanel, tag => tag == "gpu");
            BtnGpuBench_Click(BtnGpuBench, new RoutedEventArgs());
            return;
        }
        // "rt:alta:1440": a mesma cena e a mesma resolução, senão a comparação não vale.
        var parts = test.Split(':');
        if (parts.Length != 3) return;
        SelectTagged(BenchTestPanel, tag => tag == "rt");
        SelectTagged(RtPresetPanel, tag => tag == parts[1]);
        SelectTagged(RtResPanel, tag => tag.EndsWith("x" + parts[2], StringComparison.Ordinal));
        await Infrastructure.UiGuard.RunAsync("bench", RunRtBenchAsync, ex => RtStatus.Text = "O teste parou: " + Err(ex));
    }

    private static void SelectTagged(Panel panel, Func<string, bool> match)
    {
        if (panel.Children.OfType<RadioButton>().FirstOrDefault(r => r.Tag is string tag && match(tag)) is { } chip) chip.IsChecked = true;
    }

    /// <summary>Chamado ao gravar um benchmark: se é o "depois" da medição, compara, avisa e encerra.</summary>
    private async void ConcludeMeasurement(string test, double score)
    {
        if (!_measurementStateChanged || _settings.PendingMeasurement is not { } pending || pending.BenchmarkKind != test || pending.ScoreBefore is not double before) return;
        await Infrastructure.UiGuard.RunAsync("medição", async () =>
        {
            if (await ReadMeasuredStateAsync(pending.Kind) is not bool state || state == pending.StateAtStart) return;
            string message = MeasurementFlow.Message(pending.Kind, MeasurementFlow.DeltaPct(before, score), state);
            _settings.PendingMeasurement = null;
            _measurementStateChanged = false;
            _settings.Save();
            Infrastructure.AppLog.Info("medição", message);
            Toast("Medição concluída", message, "");
        }, ex => Toast("Medição", "Não foi possível concluir: " + Err(ex), ""));
    }
}
