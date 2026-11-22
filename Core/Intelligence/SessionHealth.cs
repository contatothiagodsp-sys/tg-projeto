namespace ThgOptimization.Core.Intelligence;

/// <summary>
/// Índice transparente e auditável da saúde de uma sessão (0–100), dividido em fluidez, térmico, estabilidade e recursos.
/// </summary>
public sealed record SessionHealthScore(int Score, string Grade, string Summary, int FluencyScore, int ThermalScore, int StabilityScore, int ResourceScore)
{
    public static SessionHealthScore Evaluate(DiagnosisReport report) =>
        Evaluate(report, new SessionTelemetry(report.Context, Array.Empty<TelemetryPoint>()));

    public static SessionHealthScore Evaluate(DiagnosisReport report, SessionTelemetry session)
    {
        if (!report.Quality.EnoughTime)
        {
            int shortStability = report.Findings.Any(f => f.Kind == DiagnosisKind.Stability) ? 0 : 20;
            int total = Math.Clamp(30 + 25 + shortStability + 15, 0, 100);
            if (shortStability == 0) total = Math.Min(total, 50);
            return new SessionHealthScore(total, GradeOf(total),
                shortStability == 0 ? "Sessão curta com erro de hardware registrado." : "Sessão curta demais para pontuação completa.",
                30, 25, shortStability, 15);
        }

        int fluency = ScoreFluency(report);
        int thermal = ScoreThermal(report);
        int stability = report.Findings.Any(f => f.Kind == DiagnosisKind.Stability) ? 0 : 20;
        int resource = ScoreResources(report);

        int score = Math.Clamp(fluency + thermal + stability + resource, 0, 100);
        string grade = GradeOf(score);
        string summary = score switch
        {
            >= 90 => "Sessão fluida, estável e com folga térmica e de recursos.",
            >= 75 => "Bom desempenho geral, com pontos leves de atenção.",
            >= 55 => "Experiência irregular: há gargalos ou limites afetando a fluidez.",
            _ => "Sessão comprometida por instabilidade, calor ou engasgos recorrentes.",
        };
        return new SessionHealthScore(score, grade, summary, fluency, thermal, stability, resource);
    }

    private static int ScoreFluency(DiagnosisReport report)
    {
        if (report.Frames is not { Origin: MetricOrigin.Captured } fr)
            return 30; // coleta sem ETW de frames: nota neutra sem penalizar nem inventar fluidez

        int fluency = 40;
        double ratio = fr.P50Ms > 0 ? fr.P99Ms / fr.P50Ms : 1.0;
        if (ratio > 1.5)
            fluency -= (int)Math.Round(Math.Min(20, (ratio - 1.5) * 10));

        bool earlyOnly = report.Findings.Any(f => f.Kind == DiagnosisKind.ShaderStutter)
                         && !report.Findings.Any(f => f.Kind == DiagnosisKind.FramePacing);
        if (earlyOnly)
            fluency -= (int)Math.Round(Math.Min(6, fr.StuttersPerMinute * 1.5));
        else if (fr.StuttersPerMinute > 0.5)
            fluency -= (int)Math.Round(Math.Min(20, (fr.StuttersPerMinute - 0.5) * 3.5));

        return Math.Clamp(fluency, 0, 40);
    }

    private static int ScoreThermal(DiagnosisReport report)
    {
        int thermal = 25;
        foreach (var f in report.Findings)
        {
            if (f.Kind is DiagnosisKind.CpuTemperature or DiagnosisKind.GpuTemperature or DiagnosisKind.ThermalThrottling)
                thermal -= f.Severity == Severity.Problem ? 18 : 8;
        }
        return Math.Clamp(thermal, 0, 25);
    }

    private static int ScoreResources(DiagnosisReport report)
    {
        int resource = 15;
        foreach (var f in report.Findings)
        {
            if (f.Kind is DiagnosisKind.MemoryPressure or DiagnosisKind.VramPressure or DiagnosisKind.Storage or DiagnosisKind.ConcurrentProcesses or DiagnosisKind.DpcIsr)
                resource -= f.Severity == Severity.Problem ? 6 : 4;
            else if (f.Kind == DiagnosisKind.Network)
                resource -= 4;
        }
        return Math.Clamp(resource, 0, 15);
    }

    private static string GradeOf(int score) => score switch
    {
        >= 90 => "A",
        >= 75 => "B",
        >= 55 => "C",
        _ => "D",
    };
}
