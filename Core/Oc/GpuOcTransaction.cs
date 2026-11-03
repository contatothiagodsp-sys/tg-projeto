namespace ThgOptimization.Core.Oc;

/// <summary>Limite de energia e teto de clock de uma GPU. Códigos NVML: 0 = sucesso. Implementação real: NvmlGpuTuningDevice.</summary>
public interface IGpuTuningDevice
{
    uint? PowerLimitMw();
    int SetPowerLimitMw(uint mw);
    uint? ClockMhz();
    int LockClocks(uint minMhz, uint maxMhz);
    int ResetClocks();
}

public sealed class NvmlGpuTuningDevice : IGpuTuningDevice
{
    private readonly IntPtr _device;
    public NvmlGpuTuningDevice(IntPtr device) => _device = device;
    public uint? PowerLimitMw() => Nvml.PowerLimitMw(_device);
    public int SetPowerLimitMw(uint mw) => Nvml.SetPowerLimitMw(_device, mw);
    public uint? ClockMhz() => Nvml.Clock(_device, Nvml.ClockGraphics);
    public int LockClocks(uint minMhz, uint maxMhz) => Nvml.LockGpuClocks(_device, minMhz, maxMhz);
    public int ResetClocks() => Nvml.ResetGpuClocks(_device);
}

/// <param name="Applied">True só depois de ler de volta o que foi pedido.</param>
/// <param name="Restored">Falhou e o estado anterior voltou. Falso com Applied falso = pode ter ficado parte do ajuste, e a mensagem diz.</param>
public sealed record GpuOcOutcome(bool Applied, bool Restored, string Message);

/// <summary>
/// Aplica energia e teto como uma transação: guarda o estado, grava, lê de volta e, se algo não bater, volta tudo.
/// O driver pode responder sucesso e aplicar outra coisa (BIOS travada, limite da placa), por isso a resposta dele sozinha não basta.
/// A NVML não informa o teto em vigor: o que dá para conferir é se o clock atual passou dele. Com a GPU parada, essa conferência
/// só prova que o teto não foi desrespeitado até agora.
/// </summary>
public static class GpuOcTransaction
{
    public const uint PowerToleranceMw = 5000, ClockToleranceMhz = 50, MinLockMhz = 210;
    /// <summary>O clock leva um instante para descer depois de travar. O teto só é dado como desrespeitado depois de todas as leituras.</summary>
    private const int ClockChecks = 5;
    private static readonly TimeSpan ClockCheckEvery = TimeSpan.FromMilliseconds(100);

    /// <param name="previousCap">Teto em vigor, que a NVML não informa: quem chama guarda. Null = nenhum aplicado pelo tHg.</param>
    /// <param name="wait">Espera entre as leituras do clock. Os testes passam uma que não espera.</param>
    public static GpuOcOutcome Apply(IGpuTuningDevice device, uint requestedMw, uint? capMhz, uint? previousCap, Action<TimeSpan>? wait = null)
    {
        if (device.PowerLimitMw() is not uint previousMw)
            return new(false, true, "Não foi possível ler o limite de energia atual: nada foi alterado.");

        string? why;
        try { why = PowerProblem(device, requestedMw) ?? ClockProblem(device, capMhz, previousCap, wait ?? Thread.Sleep); }
        catch (Exception ex) { why = ex.Message; }
        if (why == null)
            return new(true, false, $"Energia: {requestedMw / 1000.0:0} W confirmado. " +
                                    (capMhz is uint cap ? $"Teto de clock: {cap} MHz aplicado." : "Teto de clock: removido."));

        string? restore = RestoreProblem(device, previousMw, previousCap);
        return restore == null
            ? new(false, true, $"{why} Nada ficou valendo: o estado anterior foi restaurado.")
            : new(false, false, $"{why} Também não foi possível voltar ao estado anterior ({restore}): reinicie o PC para voltar ao padrão.");
    }

    private static string? PowerProblem(IGpuTuningDevice device, uint requestedMw)
    {
        if (Nvml.Explain(device.SetPowerLimitMw(requestedMw)) is string error) return $"Energia não aplicada ({error}).";
        if (device.PowerLimitMw() is not uint applied) return "O driver não confirmou o limite de energia.";
        uint difference = applied > requestedMw ? applied - requestedMw : requestedMw - applied;
        return difference > PowerToleranceMw ? $"O driver respondeu OK, mas aplicou {applied / 1000.0:0} W em vez de {requestedMw / 1000.0:0} W." : null;
    }

    private static string? ClockProblem(IGpuTuningDevice device, uint? capMhz, uint? previousCap, Action<TimeSpan> wait)
    {
        if (capMhz is not uint cap)
        {
            int code = device.ResetClocks();
            // Placa sem suporte a teto, sem nenhum teto do tHg em vigor: não há o que desfazer, e isso não invalida o limite de energia.
            return code != 0 && previousCap != null ? $"O teto de clock não foi removido ({Nvml.Explain(code)})." : null;
        }
        if (Nvml.Explain(device.LockClocks(MinLockMhz, cap)) is string error) return $"Teto de clock não aplicado ({error}).";
        uint? clock = null;
        for (int i = 0; i < ClockChecks; i++)
        {
            if (i > 0) wait(ClockCheckEvery);
            clock = device.ClockMhz();
            if (clock is uint now && now <= cap + ClockToleranceMhz) return null;
        }
        return clock == null ? "O driver não informou o clock depois de aplicar o teto." : $"O clock está em {clock} MHz, acima do teto de {cap} MHz.";
    }

    private static string? RestoreProblem(IGpuTuningDevice device, uint previousMw, uint? previousCap)
    {
        var problems = new List<string>();
        try
        {
            if (Nvml.Explain(device.SetPowerLimitMw(previousMw)) is string power) problems.Add($"energia: {power}");
            else if (device.PowerLimitMw() is not uint read) problems.Add("energia sem confirmação");
            else if ((read > previousMw ? read - previousMw : previousMw - read) > PowerToleranceMw) problems.Add($"energia em {read / 1000.0:0} W");
        }
        catch (Exception ex) { problems.Add($"energia: {ex.Message}"); }
        try
        {
            int code = previousCap is uint cap ? device.LockClocks(MinLockMhz, cap) : device.ResetClocks();
            // Sem teto anterior, a placa que não suporta teto também não tem nada a soltar.
            if (code != 0 && (previousCap != null || code != 3)) problems.Add($"clock: {Nvml.Explain(code)}");
        }
        catch (Exception ex) { problems.Add($"clock: {ex.Message}"); }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
}
