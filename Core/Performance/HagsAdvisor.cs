namespace ThgOptimization.Core.Performance;

/// <summary>
/// Agendamento de GPU acelerado por hardware (HAGS), conforme a peça. Ajuda mais em CPU fraca e reserva parte da VRAM, o que pode pesar em placas pequenas.
/// É uma recomendação de partida, não uma promessa: o efeito varia por jogo e driver, e a medição antes e depois é quem decide neste PC.
/// </summary>
public static class HagsAdvisor
{
    public sealed record HagsRecommendation(bool Recommended, string Reason, double? VramGb, string CpuTier);

    public const double LargeVramGb = 12, SmallVramGb = 8;

    /// <summary>
    /// A CPU de entrada vem primeiro: é onde o agendamento mais ajuda. Com CPU comum, VRAM de sobra liga e VRAM pequena desliga. Sem saber a VRAM, fica como está:
    /// sem dado não há motivo para mudar o que o usuário já tem.
    /// </summary>
    public static HagsRecommendation Evaluate(bool hagsEnabled, GpuInfo? gpu, string cpuName)
    {
        bool entry = Core.Performance.CpuTier.IsEntry(cpuName) || Core.Performance.CpuTier.IsOld(cpuName);
        string tier = entry ? "entrada" : "comum";
        double? vram = gpu is { VramBytes: > 0 } ? Math.Ceiling(gpu.VramBytes / (double)(1UL << 30) - 0.15) : null;
        if (entry) return new(true, "Com CPU de entrada, o agendamento pela placa tira mais trabalho do processador.", vram, tier);
        if (vram == null) return new(hagsEnabled, "A VRAM desta placa não foi identificada: sem motivo para mudar o ajuste atual.", null, tier);
        if (vram >= LargeVramGb) return new(true, $"Com {vram:0} GB de VRAM, a parte que o agendamento reserva pesa pouco.", vram, tier);
        return new(false, $"Com {vram:0} GB de VRAM, a parte que o agendamento reserva pode fazer falta nos jogos.", vram, tier);
    }

    /// <summary>
    /// Item do TURBO. TurboSkip faz o "corrigir tudo" deixar este item de fora e o botão pedir confirmação: muda o agendamento de todos os jogos e exige reiniciar.
    /// Peso 0, porque é uma recomendação que depende da medição, e o ajuste em si já conta na nota.
    /// </summary>
    public static GamerCheck BuildCheck(bool hagsEnabled, GpuInfo? gpu, string cpuName, Tweak? tweak)
    {
        var advice = Evaluate(hagsEnabled, gpu, cpuName);
        bool differs = advice.Recommended != hagsEnabled && tweak != null;
        var check = new GamerCheck
        {
            Id = "gpu.hags.advice", Area = "WINDOWS", Title = "Agendamento de GPU para esta placa e CPU", Weight = 0, NeedsRestart = true,
            Level = differs ? CheckLevel.Warn : CheckLevel.Ok,
            Found = $"{(hagsEnabled ? "Ligado" : "Desligado")} · para este PC, recomendação de partida: {(advice.Recommended ? "ligado" : "desligado")}",
            Advice = advice.Reason + " O efeito varia por jogo e por driver: use MEDIR para comparar neste PC.",
            Fix = differs ? FixKind.Auto : FixKind.None,
            FixLabel = advice.Recommended ? "LIGAR" : "DESLIGAR",
            TurboSkip = differs ? "Muda o agendamento de todos os jogos e só vale depois de reiniciar o PC. Pode ser desfeito em DESFAZER TUDO." : "",
            SecondaryLabel = tweak != null ? "MEDIR" : "",
        };
        if (differs)
            check.AutoFix = s =>
            {
                tweak!.Write(advice.Recommended, s);
                return "";
            };
        return check;
    }
}
