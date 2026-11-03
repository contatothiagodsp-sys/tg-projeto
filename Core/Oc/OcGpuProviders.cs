using System.Diagnostics.CodeAnalysis;

namespace ThgOptimization.Core.Oc;

[Experimental(OcGate.Diagnostic)]
public interface INvmlOcTransport
{
    /// <summary>Código NVML; 0 = sucesso.</summary>
    int GetGpcOffset(out int mhz);
    int SetGpcOffset(int mhz);
    double? TempC();
}

/// <summary>Chamadas reais da NVML. A OcGate barra a escrita antes de chegar aqui enquanto a constante estiver em false.</summary>
[Experimental(OcGate.Diagnostic)]
public sealed class NvmlOcTransport : INvmlOcTransport
{
    private readonly IntPtr _device;
    public NvmlOcTransport(IntPtr device) => _device = device;
    public int GetGpcOffset(out int mhz) => Nvml.GetGpcClkVfOffset(_device, out mhz);
    public int SetGpcOffset(int mhz) => Nvml.SetGpcClkVfOffset(_device, mhz);
    public double? TempC() => Nvml.GpuTempC(_device);
}

/// <summary>Offset de clock da GPU NVIDIA, no máximo +300 MHz e abaixo de 83 °C. Vale até reiniciar.</summary>
[Experimental(OcGate.Diagnostic)]
public sealed class NvidiaGpuProvider : OverclockProviderBase
{
    private readonly INvmlOcTransport _io;

    public NvidiaGpuProvider(INvmlOcTransport io, bool isSimulated) : base(isSimulated) => _io = io;

    public override OcVendor Vendor => OcVendor.NvidiaGpu;
    public override OcTelemetry ReadTelemetry() => new(_io.TempC(), null);
    protected override int? ReadValue() => _io.GetGpcOffset(out int mhz) == 0 ? mhz : null;

    protected override string? WriteValue(int value)
    {
        int code = _io.SetGpcOffset(value);
        return code == 0 ? null : Nvml.Explain(code) ?? $"erro NVML {code}";
    }
}

/// <summary>Sem implementação real nesta versão: a ligação com o ADLX (AMD) ainda não existe. Só os testes usam esta interface.</summary>
[Experimental(OcGate.Diagnostic)]
public interface IAmdOverdriveTransport
{
    bool TryGetOffsetPercent(out int percent);
    bool TrySetOffsetPercent(int percent);
    double? TempC();
}

/// <summary>Offset de clock da GPU AMD, no máximo +10% e abaixo de 85 °C.</summary>
[Experimental(OcGate.Diagnostic)]
public sealed class AmdGpuProvider : OverclockProviderBase
{
    private readonly IAmdOverdriveTransport _io;

    public AmdGpuProvider(IAmdOverdriveTransport io, bool isSimulated) : base(isSimulated) => _io = io;

    public override OcVendor Vendor => OcVendor.AmdGpu;
    public override OcTelemetry ReadTelemetry() => new(_io.TempC(), null);
    protected override int? ReadValue() => _io.TryGetOffsetPercent(out int p) ? p : null;
    protected override string? WriteValue(int value) => _io.TrySetOffsetPercent(value) ? null : "O driver recusou o offset.";
}

/// <summary>
/// Vigia do ajuste. Volta ao valor original se a temperatura ficar no limite por 3 leituras seguidas, se surgir erro de
/// hardware (WHEA, TDR) ou se o usuário não confirmar a estabilidade a tempo. Não tem timer próprio: quem usa chama Tick.
/// Implementado e testado com simulação; nada no app o inicia nesta versão.
/// </summary>
[Experimental(OcGate.Diagnostic)]
public sealed class OcRollbackWatchdog
{
    public const int StrikesToRollback = 3;
    private readonly IOverclockProvider _provider;
    private readonly Func<int> _hardwareErrors;
    private DateTime _deadline;
    private int _errorsAtArm, _strikes;

    public OcRollbackWatchdog(IOverclockProvider provider, Func<int> hardwareErrors)
    {
        _provider = provider;
        _hardwareErrors = hardwareErrors;
    }

    public bool Armed { get; private set; }

    public void Arm(DateTime now, TimeSpan confirmWithin)
    {
        _deadline = now + confirmWithin;
        _errorsAtArm = _hardwareErrors();
        _strikes = 0;
        Armed = true;
    }

    /// <summary>O usuário confirmou que está estável: o ajuste fica, mas a temperatura e os erros continuam vigiados.</summary>
    public void Confirm() => _deadline = DateTime.MaxValue;

    public void Disarm() => Armed = false;

    /// <summary>Motivo da volta ao original, ou null se nada aconteceu nesta leitura.</summary>
    public string? Tick(DateTime now)
    {
        if (!Armed) return null;
        string? why = null;
        if (_hardwareErrors() > _errorsAtArm) why = "Erro de hardware depois do ajuste.";
        else if (now >= _deadline) why = "O ajuste não foi confirmado a tempo.";
        else
        {
            _strikes = OcLimits.CheckTelemetry(_provider.Vendor, _provider.ReadTelemetry()) != null ? _strikes + 1 : 0;
            if (_strikes >= StrikesToRollback) why = "Temperatura ou tensão no limite, ou sensor sem resposta.";
        }
        if (why == null) return null;
        Armed = false;
        _provider.Restore();
        return why;
    }
}
