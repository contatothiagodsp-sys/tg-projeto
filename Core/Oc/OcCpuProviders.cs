using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Core.Oc;

/// <summary>Acesso aos registradores do SMU (pelo PawnIO, nunca pelo WinRing0). Implementação real: PawnIoTransport.</summary>
[Experimental(OcGate.Diagnostic)]
public interface ISmuTransport
{
    bool Read(uint address, out uint value);
    bool Write(uint address, uint value);
}

/// <summary>
/// Mutex do barramento PCI, o mesmo que o LibreHardwareMonitor, o HWiNFO e os módulos do PawnIO usam. O índice e o dado do SMN
/// são dois registradores: sem o mutex, outro programa troca o índice no meio da conversa. Um Mutex do Windows é recursivo
/// na mesma thread, por isso a caixa de mensagens segura a conversa inteira e o transporte, cada acesso.
/// </summary>
public sealed class HardwareMutex : IDisposable
{
    public const string PciName = @"Global\Access_PCI";
    private readonly string _name;
    private readonly object _sync = new();
    private readonly Dictionary<int, int> _holds = new(); // thread → quantas entradas: só a dona solta, e só o que pegou
    private Mutex? _mutex;
    private bool _disposed;

    public HardwareMutex(string name = PciName) => _name = name;

    /// <summary>Para os testes: roda entre conseguir o mutex e registrar a entrada, onde um Dispose em outra thread pode cair.</summary>
    internal Action? AfterWaitForTests { get; set; }

    /// <summary>false = não conseguiu no tempo pedido, o Windows negou ou já foi descartado. Quem chama não pode tocar no hardware.</summary>
    public bool TryEnter(TimeSpan timeout)
    {
        Mutex mutex;
        try
        {
            lock (_sync)
            {
                if (_disposed) return false;
                mutex = _mutex ??= new Mutex(false, _name);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            AppLog.Error("oc", ex);
            return false;
        }
        bool owned;
        try { owned = mutex.WaitOne(timeout); }
        catch (AbandonedMutexException)
        {
            // O dono morreu sem soltar: agora é nosso, mas ele pode ter parado no meio de uma conversa com o barramento.
            AppLog.Error("oc", $"{_name} foi abandonado por outro programa.");
            owned = true;
        }
        catch (ObjectDisposedException) { return false; }
        if (!owned) return false;
        AfterWaitForTests?.Invoke();
        lock (_sync)
        {
            // O Dispose pode ter rodado durante a espera: sem esta conferência, quem chamou escreveria no barramento sem mutex válido.
            if (_disposed || _mutex == null)
            {
                try { mutex.ReleaseMutex(); }
                catch (Exception ex) when (ex is ObjectDisposedException or ApplicationException) { AppLog.Error("oc", ex); }
                return false;
            }
            int thread = Environment.CurrentManagedThreadId;
            _holds[thread] = _holds.GetValueOrDefault(thread) + 1;
        }
        return true;
    }

    /// <summary>Solta uma entrada desta thread. Sem entrada dela, não faz nada: soltar duas vezes ou da thread errada não derruba o app.</summary>
    public void Exit()
    {
        int thread = Environment.CurrentManagedThreadId;
        lock (_sync)
        {
            if (_mutex == null || !_holds.TryGetValue(thread, out int count)) return;
            if (count == 1) _holds.Remove(thread); else _holds[thread] = count - 1;
            _mutex.ReleaseMutex();
        }
    }

    /// <summary>Solta o que esta thread ainda tem e fecha o handle. O de outra thread, o Windows solta quando ela terminar.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_mutex != null && _holds.Remove(Environment.CurrentManagedThreadId, out int count))
                for (int i = 0; i < count; i++) _mutex.ReleaseMutex();
            _holds.Clear();
            _mutex?.Dispose();
            _mutex = null;
        }
    }
}

/// <summary>Endereços da caixa de mensagens e comandos de margem. Sem GetCoreMargin, a família não tem leitura simples da margem. Fonte: docs/oc-engenharia-reversa.md.</summary>
public sealed record SmuFamily(string Name, uint MsgAddress, uint RspAddress, uint ArgAddress, uint SetAllCoreMargin, uint? GetCoreMargin = null)
{
    public static readonly SmuFamily Zen3 = new("Zen 3 (Vermeer, MP1)", 0x3B10530, 0x3B1057C, 0x3B109C4, 0x36);
    public static readonly SmuFamily Zen4 = new("Zen 4 (Raphael, RSMU)", 0x03B10524, 0x03B10570, 0x03B10A40, 0x7, 0xD5);
    public static readonly SmuFamily Zen5 = new("Zen 5 (Granite Ridge, RSMU)", 0x03B10524, 0x03B10570, 0x03B10A40, 0x7, 0xD5);
}

public enum SmuStatus : byte { Ok = 0x01, Failed = 0xFF, UnknownCommand = 0xFE, RejectedPrereq = 0xFD, RejectedBusy = 0xFC, Timeout = 0x31, TransportFailed = 0x33 }

/// <summary>Protocolo da caixa de mensagens: espera livre, zera a resposta, grava os argumentos, grava o comando, espera e lê o status.</summary>
[Experimental(OcGate.Diagnostic)]
public sealed class SmuMailbox : IDisposable
{
    private const int MaxPolls = 8192, MaxArgs = 6;
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(2);
    private readonly ISmuTransport _io;
    private readonly SmuFamily _family;
    private readonly bool _isSimulated;
    private readonly HardwareMutex? _mutex;
    private readonly bool _ownsMutex;

    /// <param name="mutex">Null só na simulação: nela não há barramento a proteger.</param>
    public SmuMailbox(ISmuTransport io, SmuFamily family, bool isSimulated, HardwareMutex? mutex = null)
    {
        _io = io;
        _family = family;
        _isSimulated = isSimulated;
        _mutex = isSimulated ? null : mutex ?? new HardwareMutex();
        _ownsMutex = !isSimulated && mutex == null;
    }

    public void Dispose()
    {
        if (_ownsMutex) _mutex?.Dispose();
    }

    public SmuStatus Send(uint command, uint arg0) => Send(command, arg0, out _);

    /// <param name="result">O primeiro argumento depois do comando, que é onde o SMU responde.</param>
    public SmuStatus Send(uint command, uint arg0, out uint result)
    {
        result = 0;
        if (_mutex != null && !_mutex.TryEnter(MutexTimeout)) return SmuStatus.TransportFailed;
        try
        {
            if (!WaitDone()) return SmuStatus.Timeout;
            if (!_io.Write(_family.RspAddress, 0)) return SmuStatus.TransportFailed;
            for (uint i = 0; i < MaxArgs; i++)
                if (!_io.Write(_family.ArgAddress + i * 4, i == 0 ? arg0 : 0)) return SmuStatus.TransportFailed;
            if (!_io.Write(_family.MsgAddress, command)) return SmuStatus.TransportFailed;
            if (!WaitDone()) return SmuStatus.Timeout;
            if (!_io.Read(_family.RspAddress, out uint status)) return SmuStatus.TransportFailed;
            if (status > byte.MaxValue) return SmuStatus.Failed;
            if ((SmuStatus)status == SmuStatus.Ok && !_io.Read(_family.ArgAddress, out result)) return SmuStatus.TransportFailed;
            return (SmuStatus)status;
        }
        finally { _mutex?.Exit(); }
    }

    private bool WaitDone()
    {
        var spinner = new SpinWait();
        for (int i = 0; i < MaxPolls; i++)
        {
            if (_io.Read(_family.RspAddress, out uint r) && r != 0) return true;
            if (!_isSimulated) spinner.SpinOnce();
        }
        return false;
    }

    /// <summary>A margem vai nos 16 bits baixos do argumento, em complemento de dois.</summary>
    public static uint EncodeMargin(int counts) => unchecked((uint)counts) & 0xFFFF;

    /// <summary>Volta de EncodeMargin. Aceita a resposta com 16 ou 32 bits de sinal.</summary>
    public static int DecodeMargin(uint raw) => unchecked((short)raw);
}

/// <summary>
/// Curve Optimizer em todos os núcleos, só negativo. A confirmação lê a margem do primeiro núcleo. No Zen 3 não há leitura simples:
/// fora da simulação, ele recusa o ajuste antes de gravar. O valor não sobrevive a um reinício, o que limita o estrago de um ajuste ruim.
/// </summary>
[Experimental(OcGate.Diagnostic)]
public sealed class AmdCpuProvider : OverclockProviderBase, IDisposable
{
    /// <summary>Máscara do núcleo 0, CCD 0: (ccd &lt;&lt; 28) | (núcleo &lt;&lt; 20).</summary>
    private const uint FirstCoreMask = 0;
    private readonly SmuMailbox _smu;
    private readonly SmuFamily _family;
    private readonly Func<OcTelemetry> _telemetry;
    private int _current;

    public AmdCpuProvider(ISmuTransport io, SmuFamily family, Func<OcTelemetry> telemetry, bool isSimulated, HardwareMutex? mutex = null) : base(isSimulated)
    {
        _smu = new SmuMailbox(io, family, isSimulated, mutex);
        _family = family;
        _telemetry = telemetry;
    }

    /// <summary>Temperatura, Vcore e VSOC vêm dos sensores do app, com a idade da amostra. Sem qualquer um deles, ou com a amostra antiga, o ajuste é recusado.</summary>
    public AmdCpuProvider(ISmuTransport io, SmuFamily family, SensorMonitor sensors, bool isSimulated, HardwareMutex? mutex = null)
        : this(io, family, () => new OcTelemetry(sensors.CpuTemperature, sensors.CpuVcore, sensors.CpuVsoc, sensors.SampleAge), isSimulated, mutex) { }

    public void Dispose() => _smu.Dispose();

    public override OcVendor Vendor => OcVendor.AmdCpu;
    public override OcTelemetry ReadTelemetry() => _telemetry();

    protected override int? ReadValue()
    {
        if (IsSimulated) return _current;
        if (_family.GetCoreMargin is not uint command) return null;
        return _smu.Send(command, FirstCoreMask, out uint raw) == SmuStatus.Ok ? SmuMailbox.DecodeMargin(raw) : null;
    }

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

/// <summary>
/// Conversa direta com o driver PawnIO: abre o aparelho, carrega um módulo assinado e chama as funções dele. Cada handle guarda um
/// módulo só: RyzenSMU para o SMU e IntelMSR para o MSR, abertos no primeiro uso. Os módulos são os que vêm dentro do
/// LibreHardwareMonitorLib. O IntelMSR da versão atual dessa biblioteca só lê: gravar devolve false e o provedor Intel não aplica nada.
/// As interfaces são explícitas de propósito: Write(a, 5u) e Write(a, 5UL) não podem cair em barramentos diferentes por engano.
/// Com a trava fechada, nada aqui abre o driver: até ler o SMU exige gravar o índice do barramento.
/// Ordem das travas: mutex do PCI e depois _gate. Nenhum trecho com _gate pede o mutex, então não há espera cruzada.
/// </summary>
[Experimental(OcGate.Diagnostic)]
public sealed class PawnIoTransport : ISmuTransport, IMsrTransport, IDisposable
{
    private const string DevicePath = @"\\.\PawnIO", DeviceNtPath = @"\\?\GLOBALROOT\Device\PawnIO";
    private const string ModuleResource = "LibreHardwareMonitor.Resources.PawnIo.{0}.bin";
    // CTL_CODE(41394, função, METHOD_BUFFERED, FILE_ANY_ACCESS). Fonte: PawnIO/include/pawnio_um.h.
    private const uint IoctlLoadBinary = 0xA1B22084, IoctlExecuteFn = 0xA1B22104;
    private const int FnNameLength = 32;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, ShareAll = 7, OpenExisting = 3;
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly HardwareMutex _mutex = new();
    private SafeFileHandle? _smu, _msr;
    private bool _smuFailed, _msrFailed, _disposed;

    /// <summary>Por que o último acesso falhou (para o diagnóstico).</summary>
    public string? LastError { get; private set; }

    private bool GateClosed()
    {
        if (OcGate.Block(isSimulated: false) is not string gate) return false;
        LastError = gate;
        return true;
    }

    bool ISmuTransport.Read(uint address, out uint value)
    {
        value = 0;
        if (GateClosed()) return false;
        if (!_mutex.TryEnter(MutexTimeout)) { LastError = "O mutex do PCI não ficou livre."; return false; }
        try
        {
            var handle = Smu();
            if (handle == null) return false;
            Span<ulong> output = stackalloc ulong[1];
            if (!Execute(handle, "ioctl_read_smu_register", stackalloc ulong[] { address }, output)) return false;
            value = (uint)output[0];
            return true;
        }
        finally { _mutex.Exit(); }
    }

    bool ISmuTransport.Write(uint address, uint value)
    {
        if (GateClosed()) return false;
        if (!_mutex.TryEnter(MutexTimeout)) { LastError = "O mutex do PCI não ficou livre."; return false; }
        try
        {
            var handle = Smu();
            return handle != null && Execute(handle, "ioctl_write_smu_register", stackalloc ulong[] { address, value }, Span<ulong>.Empty);
        }
        finally { _mutex.Exit(); }
    }

    bool IMsrTransport.Read(uint msr, out ulong value)
    {
        value = 0;
        if (GateClosed()) return false;
        var handle = Msr();
        if (handle == null) return false;
        Span<ulong> output = stackalloc ulong[1];
        if (!Execute(handle, "ioctl_read_msr", stackalloc ulong[] { msr }, output)) return false;
        value = output[0];
        return true;
    }

    bool IMsrTransport.Write(uint msr, ulong value)
    {
        if (GateClosed()) return false;
        var handle = Msr();
        return handle != null && Execute(handle, "ioctl_write_msr", stackalloc ulong[] { msr, value }, Span<ulong>.Empty);
    }

    private SafeFileHandle? Smu()
    {
        lock (_gate)
        {
            if (_smu == null && !_smuFailed && !_disposed) { _smu = OpenModule("RyzenSMU"); _smuFailed = _smu == null; }
            return _smu;
        }
    }

    private SafeFileHandle? Msr()
    {
        lock (_gate)
        {
            if (_msr == null && !_msrFailed && !_disposed) { _msr = OpenModule("IntelMSR"); _msrFailed = _msr == null; }
            return _msr;
        }
    }

    private SafeFileHandle? OpenModule(string module)
    {
        var handle = OpenDevice();
        if (handle == null) return null;
        bool loaded = false;
        try
        {
            using var stream = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetManifestResourceStream(string.Format(ModuleResource, module));
            if (stream == null) { Fail($"O módulo {module} não está na biblioteca de sensores."); return null; }
            byte[] blob = new byte[stream.Length];
            stream.ReadExactly(blob);
            loaded = DeviceIoControl(handle, IoctlLoadBinary, blob, blob.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (loaded) return handle;
            Fail($"O PawnIO recusou o módulo {module} (erro {Marshal.GetLastWin32Error()}).");
        }
        catch (IOException ex) { Fail($"Não deu para ler o módulo {module}: {ex.Message}"); }
        finally { if (!loaded) handle.Dispose(); }
        return null;
    }

    private SafeFileHandle? OpenDevice()
    {
        // O nome em \\.\ é marcado como obsoleto pelo PawnIO; o caminho NT é o oficial, por isso fica de reserva.
        foreach (string path in new[] { DevicePath, DeviceNtPath })
        {
            var handle = CreateFileW(path, GenericRead | GenericWrite, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (!handle.IsInvalid) return handle;
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            Fail($"Não deu para abrir o driver PawnIO: {new Win32Exception(error).Message}");
        }
        return null;
    }

    /// <summary>Entrada: 32 bytes com o nome da função e depois os valores de 64 bits. Saída: valores de 64 bits.</summary>
    private unsafe bool Execute(SafeFileHandle handle, string function, ReadOnlySpan<ulong> input, Span<ulong> output)
    {
        byte[] request = new byte[FnNameLength + input.Length * sizeof(ulong)];
        System.Text.Encoding.ASCII.GetBytes(function, request);
        MemoryMarshal.AsBytes(input).CopyTo(request.AsSpan(FnNameLength));
        fixed (ulong* result = output)
        {
            int outBytes = output.Length * sizeof(ulong);
            int written;
            try
            {
                if (!DeviceIoControl(handle, IoctlExecuteFn, request, request.Length, (IntPtr)result, outBytes, out written, IntPtr.Zero))
                {
                    LastError = $"{function}: erro {Marshal.GetLastWin32Error()} do driver.";
                    return false;
                }
            }
            catch (ObjectDisposedException) // Dispose em outra thread: o SafeHandle recusa com segurança, e a chamada não chega ao driver
            {
                LastError = $"{function}: o transporte foi fechado.";
                return false;
            }
            if (written < outBytes)
            {
                LastError = $"{function}: resposta menor que a esperada.";
                return false;
            }
            return true;
        }
    }

    private void Fail(string message)
    {
        LastError = message;
        AppLog.Error("oc", message);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _smu?.Dispose();
            _msr?.Dispose();
            _smu = _msr = null;
        }
        _mutex.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
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
