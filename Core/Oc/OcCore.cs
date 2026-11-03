using System.Diagnostics.CodeAnalysis;

namespace ThgOptimization.Core.Oc;

public enum OcVendor { AmdCpu, IntelCpu, NvidiaGpu, AmdGpu }

public enum OcStatus { Applied, Restored, RejectedByLimit, BlockedByGate, Unsupported, Failed }

/// <summary>
/// Valor pedido, na unidade de cada fabricante: AmdCpu = contagens do Curve Optimizer, IntelCpu = mV de offset,
/// NvidiaGpu e AmdGpu = MHz de offset.
/// </summary>
/// <param name="AboveSafeConfirmed">O usuário aceitou sair da faixa segura da GPU AMD. Nunca passa do teto rígido.</param>
public sealed record OcRequest(OcVendor Vendor, int Value, bool AboveSafeConfirmed = false);

public sealed record OcResult(OcStatus Status, string Message)
{
    public bool Ok => Status is OcStatus.Applied or OcStatus.Restored;
}

/// <summary>Leitura do componente. Null = o sensor não respondeu. VsocVolts só existe na CPU AMD.</summary>
/// <param name="Age">Idade da amostra. Zero = lida agora, direto do driver; quem entrega uma leitura guardada informa há quanto tempo ela foi feita.</param>
public readonly record struct OcTelemetry(double? TempC, double? Volts, double? VsocVolts = null, TimeSpan Age = default);

/// <summary>
/// Trava do overclock. Fechada: nenhuma escrita chega ao hardware, em nenhum provedor nem transporte, e o modo simulado continua disponível.
/// [Experimental] só avisa o compilador e não protege nada. Abrir esta trava é uma mudança de código, revista junto com PendingCriteria.
/// </summary>
public static class OcGate
{
    /// <summary>Id do [Experimental]: quem usa estes tipos fora deste namespace não compila sem suprimir o aviso de propósito.</summary>
    public const string Diagnostic = "THGOC001";

    public const bool RealHardwareEnabled = false;

    /// <summary>O que falta para abrir a trava. Cada item precisa de evidência registrada em docs/oc-engenharia-reversa.md.</summary>
    public static readonly IReadOnlyList<string> PendingCriteria = new[]
    {
        "Escrita e reversão testadas em hardware real, em cada fabricante.",
        "Reversão por TDR real conferida numa GPU AMD e numa NVIDIA.",
        "Tela com aviso de risco e confirmação do usuário ligada ao provedor.",
        "Módulo do PawnIO com escrita de MSR, para a CPU Intel.",
    };

    private static readonly AsyncLocal<bool> TestScope = new();

    /// <summary>Só os testes usam, com transportes simulados, para exercitar o caminho "real" dos provedores. Vale para quem chamou e o que ele iniciar.</summary>
    internal static IDisposable AllowInTests()
    {
        TestScope.Value = true;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => TestScope.Value = false;
    }

    public static string? Block(bool isSimulated) =>
        isSimulated || RealHardwareEnabled || TestScope.Value ? null : "Overclock experimental desligado nesta versão: ainda não foi testado em hardware real.";
}

/// <summary>Limites rígidos da Seção 10.8. Nenhum provedor passa disso, nem com a trava aberta.</summary>
public static class OcLimits
{
    public const double AmdCpuMaxVolts = 1.35, AmdCpuMaxTempC = 90;
    /// <summary>Acima disso o VSOC arrisca danificar a CPU AMD (controlador de memória): bloqueia qualquer ajuste.</summary>
    public const double AmdVsocSafeMaxVolts = 1.25;
    public const double IntelCpuMaxVolts = 1.35, IntelCpuMaxTempC = 95;
    /// <summary>Amostra mais velha que isto não diz nada sobre o estado atual do componente.</summary>
    public static readonly TimeSpan MaxTelemetryAge = TimeSpan.FromSeconds(5);
    /// <summary>NVIDIA: de 0 (volta ao padrão) a +300 MHz. Passos pequenos (+30, +50, +75) valem: é assim que se acha o limite da placa. Offset negativo não é aceito.</summary>
    public const int NvidiaMaxOffsetMhz = 300;
    public const double NvidiaMaxTempC = 83;
    /// <summary>GPU AMD: de 0 (volta ao padrão) a +250 MHz. De +251 a +300 só com confirmação explícita. Acima de +300, nunca.</summary>
    public const int AmdGpuMaxSafeOcMhz = 250, AmdGpuMaxOffsetMhz = 300;
    public const double AmdGpuMaxTempC = 85;
    /// <summary>Só undervolt nas CPUs: nada de contagem positiva nem de offset acima de 0.</summary>
    public const int AmdCoMin = -30, AmdCoMax = 0, IntelOffsetMinMv = -150, IntelOffsetMaxMv = 0;

    public static double MaxTempC(OcVendor v) => v switch
    {
        OcVendor.AmdCpu => AmdCpuMaxTempC,
        OcVendor.IntelCpu => IntelCpuMaxTempC,
        OcVendor.NvidiaGpu => NvidiaMaxTempC,
        _ => AmdGpuMaxTempC,
    };

    /// <summary>Null nas GPUs: nelas o limite é o offset, e o driver cuida da tensão.</summary>
    public static double? MaxVolts(OcVendor v) => v switch
    {
        OcVendor.AmdCpu => AmdCpuMaxVolts,
        OcVendor.IntelCpu => IntelCpuMaxVolts,
        _ => null,
    };

    /// <summary>Motivo para recusar o valor pedido, ou null se está dentro da faixa.</summary>
    public static string? CheckValue(OcRequest r)
    {
        if (r.Vendor == OcVendor.NvidiaGpu)
            return r.Value >= 0 && r.Value <= NvidiaMaxOffsetMhz
                ? null
                : $"Valor {r.Value} MHz fora da faixa permitida (0 a +{NvidiaMaxOffsetMhz}).";
        if (r.Vendor == OcVendor.AmdGpu) return CheckAmdGpuValue(r);
        var (min, max, unit) = r.Vendor == OcVendor.AmdCpu
            ? (AmdCoMin, AmdCoMax, " (Curve Optimizer)")
            : (IntelOffsetMinMv, IntelOffsetMaxMv, " mV");
        return r.Value < min || r.Value > max ? $"Valor {r.Value}{unit} fora da faixa permitida ({min} a {max})." : null;
    }

    private static string? CheckAmdGpuValue(OcRequest r)
    {
        if (r.Value >= 0 && r.Value <= AmdGpuMaxSafeOcMhz) return null;
        if (r.Value < 0 || r.Value > AmdGpuMaxOffsetMhz)
            return $"Valor {r.Value} MHz fora da faixa permitida (0 a +{AmdGpuMaxOffsetMhz}).";
        return r.AboveSafeConfirmed ? null
            : $"Valor {r.Value} MHz acima da faixa segura (0 a +{AmdGpuMaxSafeOcMhz}): exige confirmação, e o teto é +{AmdGpuMaxOffsetMhz}.";
    }

    /// <summary>
    /// Motivo para não mexer agora, ou null. Fail-closed: sem uma leitura válida e recente de tudo o que tem limite, não há ajuste.
    /// NaN passaria por qualquer comparação, por isso todo valor é conferido com IsFinite antes de comparar.
    /// </summary>
    public static string? CheckTelemetry(OcVendor v, OcTelemetry t)
    {
        if (t.Age < TimeSpan.Zero || t.Age > MaxTelemetryAge) return "Telemetria antiga: ajuste recusado.";
        if (t.TempC is not double temp || !double.IsFinite(temp)) return "Temperatura indisponível: ajuste recusado.";
        if (temp >= MaxTempC(v)) return $"Temperatura {temp:0} °C no limite de {MaxTempC(v):0} °C.";
        if (MaxVolts(v) is double maxV)
        {
            if (t.Volts is not double volts || !double.IsFinite(volts)) return "Vcore indisponível: ajuste recusado.";
            if (volts > maxV) return $"Tensão {volts:0.000} V acima do limite de {maxV:0.00} V.";
        }
        if (v == OcVendor.AmdCpu)
        {
            if (t.VsocVolts is not double vsoc || !double.IsFinite(vsoc)) return "VSOC indisponível: ajuste recusado.";
            if (vsoc > AmdVsocSafeMaxVolts)
                return $"Segurança: VSOC {vsoc:0.000} V acima do limite de {AmdVsocSafeMaxVolts:0.00} V. Corrija na BIOS antes de qualquer ajuste.";
        }
        return null;
    }
}

[Experimental(OcGate.Diagnostic)]
public interface IOverclockProvider
{
    OcVendor Vendor { get; }
    /// <summary>True só em testes: nenhum hardware é tocado.</summary>
    bool IsSimulated { get; }
    OcTelemetry ReadTelemetry();
    OcResult Apply(OcRequest request);
    /// <summary>Volta ao valor de antes do primeiro ajuste. Sem ajuste feito por aqui, não grava nada. Restored só depois de ler o valor de volta.</summary>
    OcResult Restore();
    /// <summary>Reabre a conexão com o driver, para quando ele reiniciou (TDR). Sem conexão própria, não faz nada.</summary>
    void Reconnect() { }
}

/// <summary>
/// Toda escrita passa por aqui, nesta ordem: fabricante, faixa, trava, lock do hardware, sensores, validação, escrita, leitura de volta e, se não bater, reversão.
/// Os sensores são lidos com o lock na mão: entre a validação e a escrita, ninguém muda o estado que acabou de ser conferido.
/// </summary>
[Experimental(OcGate.Diagnostic)]
public abstract class OverclockProviderBase : IOverclockProvider
{
    private readonly object _io = new(); // a janela aplica e o vigia desfaz, de threads diferentes
    private int? _baseline;

    /// <summary>Para os testes: esta thread está com o lock do hardware?</summary>
    internal bool IoLockHeld => Monitor.IsEntered(_io);

    protected OverclockProviderBase(bool isSimulated) => IsSimulated = isSimulated;

    public abstract OcVendor Vendor { get; }
    public bool IsSimulated { get; }
    public abstract OcTelemetry ReadTelemetry();
    public virtual void Reconnect() { }
    /// <summary>Valor em uso, ou null se não deu para ler.</summary>
    protected abstract int? ReadValue();
    /// <summary>Grava o valor. Devolve null quando deu certo, ou o motivo da falha.</summary>
    protected abstract string? WriteValue(int value);
    /// <summary>Motivo para recusar antes da primeira escrita, com o lock na mão: o estado atual não pode ser reproduzido depois.</summary>
    protected virtual string? BlockWrite() => null;
    /// <summary>Conferência extra depois de desfazer. Devolve null quando tudo voltou ao que era.</summary>
    protected virtual string? CheckRestored() => null;

    public OcResult Apply(OcRequest request)
    {
        if (request.Vendor != Vendor) return new(OcStatus.Unsupported, $"Pedido de {request.Vendor} enviado ao provedor {Vendor}.");
        if (OcLimits.CheckValue(request) is string range) return new(OcStatus.RejectedByLimit, range);
        if (OcGate.Block(IsSimulated) is string gate) return new(OcStatus.BlockedByGate, gate);

        lock (_io)
        {
            if (OcLimits.CheckTelemetry(Vendor, ReadTelemetry()) is string limit) return new(OcStatus.RejectedByLimit, limit);
            if (BlockWrite() is string blocked) return new(OcStatus.RejectedByLimit, blocked);
            if (ReadValue() is not int current) return new(OcStatus.Failed, "Não foi possível ler o valor atual: nada foi alterado.");
            _baseline ??= current;

            if (WriteValue(request.Value) is string error) return new(OcStatus.Failed, error + UndoFailedAttempt());
            if (ReadValue() != request.Value)
                return new(OcStatus.Failed, "O valor lido depois de gravar é diferente do pedido: ajuste desfeito." + UndoFailedAttempt());
            return new(OcStatus.Applied, $"Valor {request.Value} aplicado.");
        }
    }

    /// <summary>Volta depois de uma tentativa que falhou e diz se não conseguiu: "desfeito" sem confirmação seria mentira.</summary>
    private string UndoFailedAttempt()
    {
        int target = _baseline!.Value;
        if (WriteValue(target) is string error) return $" Também não foi possível voltar ao valor original: {error}";
        return ReadValue() == target ? "" : " Não foi possível confirmar a volta ao valor original.";
    }

    public OcResult Restore()
    {
        if (OcGate.Block(IsSimulated) is string gate) return new(OcStatus.BlockedByGate, gate);
        lock (_io)
        {
            // Sem ajuste feito por este provedor, o valor em uso é do usuário ou de outro programa: não é para sobrescrever.
            if (_baseline is not int target) return new(OcStatus.Restored, "Nenhum ajuste feito por aqui: nada a desfazer.");
            if (WriteValue(target) is string error) return new(OcStatus.Failed, error);
            if (ReadValue() is not int read) return new(OcStatus.Failed, "O driver não confirmou o valor depois de desfazer.");
            if (read != target) return new(OcStatus.Failed, $"Depois de desfazer, o valor lido é {read}, e não {target}.");
            if (CheckRestored() is string notBack) return new(OcStatus.Failed, notBack);
            _baseline = null;
            return new(OcStatus.Restored, $"Valor original ({target}) de volta.");
        }
    }
}
