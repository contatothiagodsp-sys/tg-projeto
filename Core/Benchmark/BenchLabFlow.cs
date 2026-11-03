namespace ThgOptimization.Core.Benchmark;

/// <summary>Teste de um ajuste em andamento. Fica salvo: ajuste que exige reiniciar só é medido depois do reboot.</summary>
public sealed record LabTweakRun(string TweakId, string ExperimentId)
{
    public DateTime? AppliedAt { get; init; }
}

public enum LabTweakStep
{
    /// <summary>Ajuste desligado e nada medido: falta o "antes".</summary>
    NeedsBefore,
    ReadyToApply,
    /// <summary>Aplicado, mas ainda sem o reinício que ele exige. Medir agora mediria a configuração antiga.</summary>
    WaitingRestart,
    ReadyToMeasure,
    /// <summary>Já estava ligado sem ser por este teste: não há como medir o "antes".</summary>
    AlreadyApplied,
    /// <summary>Outro ajuste foi aplicado por um teste que não terminou.</summary>
    OtherTestActive,
}

/// <summary>Regras do fluxo do Performance Lab, fora da janela para poderem ser testadas.</summary>
public static class BenchLabFlow
{
    public const int MaxSessions = 200, MaxComparisons = 50, MaxLabelLength = 40;

    /// <summary>Lado que o atalho grava. Num teste de ajuste, depende do estado dele. Null: agora não dá para gravar nenhum dos dois.</summary>
    public static string? HotkeySlot(LabTweakStep? tweakStep, int capturesA, int capturesB) => tweakStep switch
    {
        null => NextSlot(capturesA, capturesB),
        LabTweakStep.NeedsBefore or LabTweakStep.ReadyToApply => "A",
        LabTweakStep.ReadyToMeasure => "B",
        _ => null,
    };

    public static LabTweakStep StepOf(string tweakId, bool tweakIsOn, bool needsRestart, LabTweakRun? run, int capturesBefore, DateTime bootTime)
    {
        if (run is { AppliedAt: not null } && run.TweakId != tweakId) return LabTweakStep.OtherTestActive;
        var mine = run?.TweakId == tweakId ? run : null;
        if (mine?.AppliedAt is { } appliedAt && tweakIsOn)
            return needsRestart && bootTime <= appliedAt ? LabTweakStep.WaitingRestart : LabTweakStep.ReadyToMeasure;
        if (tweakIsOn) return LabTweakStep.AlreadyApplied;
        return mine != null && capturesBefore > 0 ? LabTweakStep.ReadyToApply : LabTweakStep.NeedsBefore;
    }

    /// <summary>
    /// Volta ao estado de antes do teste, que era desligado. Restore devolve o valor guardado na primeira alteração, e esse
    /// valor pode ser o ajuste ligado, se o usuário o tinha ligado e depois desligado: por isso a conferência no fim.
    /// </summary>
    public static bool RevertTweak(Tweak tweak, AppSettings settings)
    {
        tweak.Restore(settings);
        if (tweak.Read() == true) tweak.Write(false, settings);
        return tweak.Read() != true;
    }

    public static (string A, string B) TweakLabels(string title)
    {
        const string Off = "Sem: ", On = "Com: ";
        int room = MaxLabelLength - Off.Length;
        string name = title.Length > room ? title[..(room - 1)] + "…" : title;
        return (Off + name, On + name);
    }

    public static void RememberSession(List<BenchmarkSession> sessions, BenchmarkSession session)
    {
        sessions.Insert(0, session);
        if (sessions.Count > MaxSessions) sessions.RemoveRange(MaxSessions, sessions.Count - MaxSessions);
    }

    /// <summary>Cada teste tem uma linha no histórico: comparar de novo, depois de uma repetição, atualiza a dele.</summary>
    public static void RememberComparison(List<AbComparison> comparisons, AbComparison comparison)
    {
        comparisons.RemoveAll(c => c.ExperimentId == comparison.ExperimentId);
        comparisons.Insert(0, comparison);
        if (comparisons.Count > MaxComparisons) comparisons.RemoveRange(MaxComparisons, comparisons.Count - MaxComparisons);
    }

    public static string HistoryLine(AbComparison c) =>
        $"{c.A.ConfigLabel} vs {c.B.ConfigLabel} · {c.A.Game} · {c.Verdict} · {c.ComparedAt:dd/MM/yyyy HH:mm}";

    /// <summary>Captura interrompida antes disso não representa os 60 s das outras, e é descartada.</summary>
    public const double MinKeptSeconds = 30;

    /// <summary>A repetição vai para o lado com menos capturas, para os dois crescerem juntos.</summary>
    public static string NextSlot(int capturesA, int capturesB) => capturesA <= capturesB ? "A" : "B";

    /// <summary>Comparar jogos diferentes não mede nada. Mensagem para o usuário, ou null se estiver tudo certo.</summary>
    public static string? GameConflict(string? experimentGame, string foregroundGame) =>
        experimentGame != null && !string.Equals(experimentGame, foregroundGame, StringComparison.OrdinalIgnoreCase)
            ? $"Este teste é de {experimentGame}, mas {foregroundGame} está em primeiro plano. Volte ao jogo ou comece um NOVO TESTE."
            : null;
}
