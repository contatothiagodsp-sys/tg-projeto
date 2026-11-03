using System.Text;
using System.Windows;
using ThgOptimization.Infrastructure;

namespace ThgOptimization;

/// <summary>Desempenho · Engasgos · Diagnóstico: tudo aqui só lê e só roda quando o usuário pede. Nada muda no Windows nem nos adaptadores.</summary>
public partial class MainWindow
{
    private readonly IPresentationModeMonitor _presentation = new EtwPresentationModeMonitor();
    private PresentationReport? _lastPresentation;

    private async void SetDiagRun_Click(object sender, RoutedEventArgs e)
    {
        SetDiagRun.IsEnabled = false;
        SetDiagOutput.Text = "Coletando (uns 5 segundos)…";
        var presentation = _lastPresentation;
        try { SetDiagOutput.Text = await Task.Run(() => BuildDiagnosticsAsync(presentation)); }
        catch (Exception ex)
        {
            AppLog.Error("diagnóstico", ex);
            SetDiagOutput.Text = "Não foi possível coletar: " + Err(ex);
        }
        finally { SetDiagRun.IsEnabled = true; }
    }

    private static async Task<string> BuildDiagnosticsAsync(PresentationReport? presentation)
    {
        var sb = new StringBuilder();
        var cpu = CpuTopologySnapshot.Current;
        sb.AppendLine("CPU").AppendLine($"  {cpu.Summary}").AppendLine($"  Plano do Modo Jogo: {CpuSetPlanner.Plan(cpu, new CpuPlanOptions(false, false, true)).Reason}");

        sb.AppendLine().AppendLine("MEMÓRIA");
        if (MemoryDiagnostics.Read() is { } m)
            sb.AppendLine($"  {m.AvailableMb} de {m.TotalMb} MB disponíveis · memória confirmada em {m.CommitPercent:0}% · pressão {MemoryDiagnostics.LevelText(m.Level)}");
        var mm = MemoryDiagnostics.ReadMmAgent() ?? MemoryDiagnostics.ParseMmAgent(SecurityAudit.Run("powershell.exe", $"-NoProfile -NonInteractive -Command \"{MemoryDiagnostics.ReadMmAgentCommand}\"", 15_000).Output);
        sb.AppendLine(mm == null ? "  Compressão e Page Combining: não foi possível ler."
                                 : $"  Compressão: {OnOff(mm.MemoryCompression)} · Page Combining: {OnOff(mm.PageCombining)}. Só informativo: o app não altera isso.");

        sb.AppendLine().AppendLine("INTERRUPÇÕES (1 s, sem jogo aberto o número não diz muito)");
        var loads = await InterruptDiagnostics.SampleAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        sb.AppendLine("  " + (loads != null ? InterruptDiagnostics.Summary(loads) : "indisponível"));

        sb.AppendLine().AppendLine("APRESENTAÇÃO DO JOGO");
        sb.AppendLine("  " + (presentation?.Text ?? "Sem coleta ainda: use o botão abaixo ou o item Modo de apresentação do overlay, com o jogo aberto."));

        sb.AppendLine().AppendLine("REDE");
        try
        {
            foreach (var a in NetworkDiagnostics.ReadAdapters().Where(a => a.Connected && !a.Virtual))
            {
                sb.AppendLine($"  {a.Name} ({a.Description})");
                foreach (var (keyword, label) in NetworkDiagnostics.Keywords) sb.AppendLine($"    {label}: {a.Value(keyword)}");
            }
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            sb.AppendLine("  Adaptadores: não foi possível ler (" + ex.Message + ")");
        }
        var link = await NetworkDiagnostics.ProbeGatewayAsync().ConfigureAwait(false);
        sb.AppendLine(link is { } l ? $"  Até o roteador: {l.AvgMs:0.0} ms · jitter {l.JitterMs:0.0} ms · perda {l.LossPercent:0}% ({l.Received}/{l.Sent})" : "  Roteador não encontrado.");
        sb.AppendLine("  Desligar Interrupt Moderation pode aumentar o uso de CPU e não ajuda em toda placa: nada é mudado aqui.");
        return sb.ToString().TrimEnd();
    }

    private static string OnOff(bool? value) => value switch { true => "ligada", false => "desligada", _ => "não informado" };

    /// <summary>Como a medição de frametime: dá tempo de voltar ao jogo, e a amostra é do programa em primeiro plano.</summary>
    private async void SetDiagPresent_Click(object sender, RoutedEventArgs e)
    {
        if (_hudPresentationBusy) { SetDiagOutput.Text = "O overlay está coletando agora. Tente de novo em alguns segundos."; return; }
        SetDiagPresent.IsEnabled = false;
        await UiGuard.RunAsync("diag", async () =>
        {
            for (int i = 5; i > 0; i--)
            {
                SetDiagOutput.Text = $"Volte para o jogo: a coleta começa em {i} s…";
                await Task.Delay(1000);
            }
            int pid = ForegroundPid();
            if (pid == 0 || pid == Environment.ProcessId) { SetDiagOutput.Text = "Nenhum jogo em primeiro plano."; return; }
            string game = ProcessUtil.NameOf(pid) ?? $"PID {pid}";
            SetDiagOutput.Text = $"Coletando {game} por 5 s…";
            _lastPresentation = await Task.Run(() => _presentation.SampleAsync(pid, TimeSpan.FromSeconds(5)));
            SetDiagOutput.Text = $"{game}: {_lastPresentation.Text}" + (_lastPresentation.Alert ? "\nAtenção: há composição adicional ou apresentação legada. Tela cheia sem bordas ou exclusiva pode mudar isso." : "");
        }, ex => SetDiagOutput.Text = "Erro: " + Err(ex));
        SetDiagPresent.IsEnabled = true;
    }

    private bool _hudPresentationBusy;
    private int _hudPresentationPid;
    private DateTime _hudPresentationAt;

    /// <summary>O item do overlay coleta sozinho, com o jogo em primeiro plano: sem Alt+Tab e sem botão. Uma amostra por vez, e nunca junto com a coleta manual.</summary>
    private async void SampleHudPresentation(int pid)
    {
        if (!PresentationHud.ShouldSample(pid, _hudPresentationPid, _hudPresentationAt, DateTime.Now, _hudPresentationBusy || !SetDiagPresent.IsEnabled)) return;
        _hudPresentationBusy = true;
        _hudPresentationPid = pid;
        _hudPresentationAt = DateTime.Now;
        await UiGuard.RunAsync("diag", async () =>
        {
            _lastPresentation = await Task.Run(() => _presentation.SampleAsync(pid, PresentationHud.SampleLength));
        }, _ => { });
        _hudPresentationBusy = false;
    }
}

/// <summary>Quando o overlay coleta o modo de apresentação. A sessão de ETW tem custo: uma amostra por jogo e, com o jogo aberto, de novo só de tempos em tempos.</summary>
internal static class PresentationHud
{
    public static readonly TimeSpan SampleLength = TimeSpan.FromSeconds(5), RefreshEvery = TimeSpan.FromMinutes(1);

    public static bool ShouldSample(int pid, int lastPid, DateTime lastAt, DateTime now, bool busy) =>
        pid > 0 && !busy && (pid != lastPid || now - lastAt >= RefreshEvery);
}
