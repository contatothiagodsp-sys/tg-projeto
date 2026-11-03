using System.Diagnostics.CodeAnalysis;

namespace ThgOptimization.Core.Oc;

/// <summary>Acesso aos registradores do SMU (pelo PawnIO, nunca pelo WinRing0). Sem implementação real nesta versão.</summary>
[Experimental(OcGate.Diagnostic)]
public interface ISmuTransport
{
    bool Read(uint address, out uint value);
    bool Write(uint address, uint value);
}

/// <summary>Endereços da caixa de mensagens e comando "margem em todos os núcleos". Fonte: docs/oc-engenharia-reversa.md.</summary>
public sealed record SmuFamily(string Name, uint MsgAddress, uint RspAddress, uint ArgAddress, uint SetAllCoreMargin)
{
    public static readonly SmuFamily Zen3 = new("Zen 3 (Vermeer, MP1)", 0x3B10530, 0x3B1057C, 0x3B109C4, 0x36);
    public static readonly SmuFamily Zen4 = new("Zen 4 (Raphael, RSMU)", 0x03B10524, 0x03B10570, 0x03B10A40, 0x7);
    public static readonly SmuFamily Zen5 = new("Zen 5 (Granite Ridge, RSMU)", 0x03B10524, 0x03B10570, 0x03B10A40, 0x7);
}

public enum SmuStatus : byte { Ok = 0x01, Failed = 0xFF, UnknownCommand = 0xFE, RejectedPrereq = 0xFD, RejectedBusy = 0xFC, Timeout = 0x31, TransportFailed = 0x33 }

/// <summary>Protocolo da caixa de mensagens: espera livre, zera a resposta, grava os argumentos, grava o comando, espera e lê o status.</summary>
[Experimental(OcGate.Diagnostic)]
public sealed class SmuMailbox
{
    private const int MaxPolls = 8192, MaxArgs = 6;
    private readonly ISmuTransport _io;
    private readonly SmuFamily _family;

    public SmuMailbox(ISmuTransport io, SmuFamily family) { _io = io; _family = family; }

    public SmuStatus Send(uint command, uint arg0)
    {
        if (!WaitDone()) return SmuStatus.Timeout;
        if (!_io.Write(_family.RspAddress, 0)) return SmuStatus.TransportFailed;
        for (uint i = 0; i < MaxArgs; i++)
            if (!_io.Write(_family.ArgAddress + i * 4, i == 0 ? arg0 : 0)) return SmuStatus.TransportFailed;
        if (!_io.Write(_family.MsgAddress, command)) return SmuStatus.TransportFailed;
        if (!WaitDone()) return SmuStatus.Timeout;
        if (!_io.Read(_family.RspAddress, out uint status)) return SmuStatus.TransportFailed;
        return status > byte.MaxValue ? SmuStatus.Failed : (SmuStatus)status;
    }

    private bool WaitDone()
    {
        for (int i = 0; i < MaxPolls; i++)
            if (_io.Read(_family.RspAddress, out uint r) && r != 0) return true;
        return false;
    }

    /// <summary>A margem vai nos 16 bits baixos do argumento, em complemento de dois.</summary>
    public static uint EncodeMargin(int counts) => unchecked((uint)counts) & 0xFFFF;
}

/// <summary>
/// Curve Optimizer em todos os núcleos, só negativo. O SMU não confirma a margem geral por leitura simples: vale o status do
/// comando. O valor não sobrevive a um reinício, o que limita o estrago de um ajuste ruim.
/// </summary>
[Experimental(OcGate.Diagnostic)]
public sealed class AmdCpuProvider : OverclockProviderBase
{
    private readonly SmuMailbox _smu;
    private readonly SmuFamily _family;
    private readonly Func<OcTelemetry> _telemetry;
    private int _current;

    public AmdCpuProvider(ISmuTransport io, SmuFamily family, Func<OcTelemetry> telemetry, bool isSimulated) : base(isSimulated)
    {
        _smu = new SmuMailbox(io, family);
        _family = family;
        _telemetry = telemetry;
    }

    public override OcVendor Vendor => OcVendor.AmdCpu;
    public override OcTelemetry ReadTelemetry() => _telemetry();
    protected override int? ReadValue() => _current;

    protected override string? WriteValue(int value)
    {
        var st = _smu.Send(_family.SetAllCoreMargin, SmuMailbox.EncodeMargin(value));
        if (st != SmuStatus.Ok) return $"O SMU recusou o comando: {st}.";
        _current = value;
        return null;
    }
}

[Experimental(OcGate.Diagnostic)]
public interface IMsrTransport
{
    bool Read(uint msr, out ulong value);
    bool Write(uint msr, ulong value);
}

/// <summary>Offset de tensão pelo MSR 0x150, nos planos do núcleo e do cache. Muitas CPUs bloqueiam isso na BIOS (Plundervolt): aí a leitura não bate e nada fica.</summary>
[Experimental(OcGate.Diagnostic)]
public sealed class IntelCpuProvider : OverclockProviderBase
{
    public const uint VoltageMsr = 0x150;
    public const int PlaneCore = 0, PlaneCache = 2;
    private readonly IMsrTransport _io;
    private readonly Func<OcTelemetry> _telemetry;

    public IntelCpuProvider(IMsrTransport io, Func<OcTelemetry> telemetry, bool isSimulated) : base(isSimulated)
    {
        _io = io;
        _telemetry = telemetry;
    }

    public override OcVendor Vendor => OcVendor.IntelCpu;
    public override OcTelemetry ReadTelemetry() => _telemetry();

    /// <summary>Ex.: -125 mV no núcleo = 0x80000011F0000000. O passo é 1/1024 V, em 11 bits com sinal a partir do bit 21.</summary>
    public static ulong EncodeWrite(int plane, int millivolts)
    {
        long steps = (long)Math.Round(millivolts * 1.024, MidpointRounding.AwayFromZero);
        ulong offset = unchecked((ulong)(steps << 21)) & 0xFFE00000UL;
        return 0x8000001100000000UL | ((ulong)plane << 40) | offset;
    }

    public static ulong EncodeReadRequest(int plane) => 0x8000001000000000UL | ((ulong)plane << 40);

    public static int DecodeMillivolts(ulong msr)
    {
        int raw = (int)((msr >> 21) & 0x7FF);
        if (raw >= 0x400) raw -= 0x800;
        return (int)Math.Round(raw / 1.024, MidpointRounding.AwayFromZero);
    }

    protected override int? ReadValue()
    {
        if (!_io.Write(VoltageMsr, EncodeReadRequest(PlaneCore)) || !_io.Read(VoltageMsr, out ulong v)) return null;
        return DecodeMillivolts(v);
    }

    protected override string? WriteValue(int value)
    {
        foreach (int plane in new[] { PlaneCore, PlaneCache })
            if (!_io.Write(VoltageMsr, EncodeWrite(plane, value))) return $"Falha ao gravar o MSR no plano {plane}.";
        return null;
    }
}
