using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace ThgOptimization.Core.Oc;

[Experimental(OcGate.Diagnostic)]
public interface INvmlOcTransport
{
    /// <summary>Código NVML; 0 = sucesso.</summary>
    int GetGpcOffset(out int mhz);
    int SetGpcOffset(int mhz);
    double? TempC();
}

/// <summary>Chamadas reais da NVML.</summary>
[Experimental(OcGate.Diagnostic)]
public sealed class NvmlOcTransport : INvmlOcTransport
{
    private readonly IntPtr _device;
    public NvmlOcTransport(IntPtr device) => _device = device;
    public int GetGpcOffset(out int mhz) => Nvml.GetGpcClkVfOffset(_device, out mhz);
    public int SetGpcOffset(int mhz) => Nvml.SetGpcClkVfOffset(_device, mhz);
    public double? TempC() => Nvml.GpuTempC(_device);
}

/// <summary>Offset de clock da GPU NVIDIA: 0 ou de +100 a +300 MHz, abaixo de 83 °C. Vale até reiniciar.</summary>
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

[Experimental(OcGate.Diagnostic)]
public interface IAmdOverdriveTransport
{
    bool TryGetOffsetPercent(out int percent);
    bool TrySetOffsetPercent(int percent);
    double? TempC();
}

/// <summary>
/// Overdrive 8 pelo ADL (atiadlxx.dll, instalada com o driver AMD). O offset é a diferença do clock máximo (GFXCLK FMAX) para o
/// padrão da placa, em %. Placa sem Overdrive 8, sem clock máximo ajustável ou cujo padrão não é um clock em MHz: tudo devolve false,
/// sem gravar. Valor fora da faixa que o driver informa também é recusado aqui. Fonte: docs/oc-engenharia-reversa.md.
/// </summary>
[Experimental(OcGate.Diagnostic)]
public sealed unsafe class AmdAdlOverdriveTransport : IAmdOverdriveTransport, IDisposable
{
    private const string Dll = "atiadlxx.dll";
    private const int AdlOk = 0, AmdVendorId = 1002, OverdriveVersion8 = 8;
    private const int Od8GfxclkFmax = 0, Od8Count = 77, GfxclkLimitsCapability = 1;
    private const int PmLogMaxSensors = 256, PmLogTemperatureEdge = 8;
    private const int InitFields = 4, SetFields = 3, PmLogFields = 2;

    [StructLayout(LayoutKind.Sequential)] private struct InitSetting { public int Count, Capabilities; public fixed int Table[Od8Count * InitFields]; } // featureID, min, max, padrão
    [StructLayout(LayoutKind.Sequential)] private struct CurrentSetting { public int Count; public fixed int Table[Od8Count]; }
    [StructLayout(LayoutKind.Sequential)] private struct SetSetting { public int Count; public fixed int Table[Od8Count * SetFields]; } // valor, pedido, voltar ao padrão
    [StructLayout(LayoutKind.Sequential)] private struct PmLogOutput { public int Size; public fixed int Sensors[PmLogMaxSensors * PmLogFields]; } // suportado, valor

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterInfo
    {
        public int Size, AdapterIndex;
        public fixed byte Udid[256];
        public int BusNumber, DeviceNumber, FunctionNumber, VendorId;
        public fixed byte AdapterName[256];
        public fixed byte DisplayName[256];
        public int Present, Exist;
        public fixed byte DriverPath[256];
        public fixed byte DriverPathExt[256];
        public fixed byte PnpString[256];
        public int OsDisplayIndex;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr AllocCallback(int size);
    private static readonly AllocCallback Alloc = size => Marshal.AllocCoTaskMem(size); // estático: o ADL guarda o ponteiro

    [DllImport(Dll)] private static extern int ADL2_Main_Control_Create(AllocCallback callback, int enumConnectedAdapters, out IntPtr context);
    [DllImport(Dll)] private static extern int ADL2_Main_Control_Destroy(IntPtr context);
    [DllImport(Dll)] private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int count);
    [DllImport(Dll)] private static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, AdapterInfo* info, int inputSize);
    [DllImport(Dll)] private static extern int ADL2_Overdrive_Caps(IntPtr context, int adapter, out int supported, out int enabled, out int version);
    [DllImport(Dll)] private static extern int ADL2_Overdrive8_Init_Setting_Get(IntPtr context, int adapter, InitSetting* setting);
    [DllImport(Dll)] private static extern int ADL2_Overdrive8_Current_Setting_Get(IntPtr context, int adapter, CurrentSetting* setting);
    [DllImport(Dll)] private static extern int ADL2_Overdrive8_Setting_Set(IntPtr context, int adapter, SetSetting* setting, CurrentSetting* current);
    [DllImport(Dll)] private static extern int ADL2_New_QueryPMLogData_Get(IntPtr context, int adapter, PmLogOutput* output);

    private IntPtr _context;
    private int _adapter = -1;
    private bool _opened;

    private bool Open()
    {
        if (_opened) return _adapter >= 0;
        _opened = true;
        try
        {
            if (ADL2_Main_Control_Create(Alloc, 1, out _context) != AdlOk) return false;
            if (ADL2_Adapter_NumberOfAdapters_Get(_context, out int count) != AdlOk || count <= 0) return false;
            var infos = new AdapterInfo[count];
            fixed (AdapterInfo* p = infos)
                if (ADL2_Adapter_AdapterInfo_Get(_context, p, count * sizeof(AdapterInfo)) != AdlOk) return false;
            foreach (var info in infos)
            {
                if (info.VendorId != AmdVendorId || info.Present == 0) continue;
                if (ADL2_Overdrive_Caps(_context, info.AdapterIndex, out int supported, out _, out int version) != AdlOk) continue;
                if (supported == 0 || version != OverdriveVersion8) continue;
                _adapter = info.AdapterIndex;
                return true;
            }
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return false;
    }

    private bool ReadFmax(out int min, out int max, out int standard, out int current, out int tableSize)
    {
        min = max = standard = current = tableSize = 0;
        if (!Open()) return false;
        try
        {
            InitSetting init = default;
            CurrentSetting now = default;
            init.Count = now.Count = Od8Count;
            if (ADL2_Overdrive8_Init_Setting_Get(_context, _adapter, &init) != AdlOk) return false;
            if (ADL2_Overdrive8_Current_Setting_Get(_context, _adapter, &now) != AdlOk) return false;
            if ((init.Capabilities & GfxclkLimitsCapability) == 0) return false;
            min = init.Table[Od8GfxclkFmax * InitFields + 1];
            max = init.Table[Od8GfxclkFmax * InitFields + 2];
            standard = init.Table[Od8GfxclkFmax * InitFields + 3];
            current = now.Table[Od8GfxclkFmax];
            tableSize = Math.Clamp(init.Count, 1, Od8Count);
            return standard > 0; // sem padrão em MHz não há o que calcular em %
        }
        catch (EntryPointNotFoundException) { return false; }
    }

    public bool TryGetOffsetPercent(out int percent)
    {
        percent = 0;
        if (!ReadFmax(out _, out _, out int standard, out int current, out _)) return false;
        percent = (int)Math.Round((current - standard) * 100.0 / standard, MidpointRounding.AwayFromZero);
        return true;
    }

    public bool TrySetOffsetPercent(int percent)
    {
        if (!ReadFmax(out int min, out int max, out int standard, out _, out int tableSize)) return false;
        int mhz = (int)Math.Round(standard * (100 + percent) / 100.0, MidpointRounding.AwayFromZero);
        if (mhz < min || mhz > max) return false;
        try
        {
            SetSetting set = default;
            CurrentSetting result = default;
            set.Count = result.Count = tableSize;
            set.Table[Od8GfxclkFmax * SetFields] = mhz;
            set.Table[Od8GfxclkFmax * SetFields + 1] = 1; // pedido: os demais itens ficam como estão
            return ADL2_Overdrive8_Setting_Set(_context, _adapter, &set, &result) == AdlOk;
        }
        catch (EntryPointNotFoundException) { return false; }
    }

    public double? TempC()
    {
        if (!Open()) return null;
        try
        {
            PmLogOutput log = default;
            log.Size = sizeof(PmLogOutput);
            if (ADL2_New_QueryPMLogData_Get(_context, _adapter, &log) != AdlOk) return null;
            return log.Sensors[PmLogTemperatureEdge * PmLogFields] != 0 ? log.Sensors[PmLogTemperatureEdge * PmLogFields + 1] : null;
        }
        catch (EntryPointNotFoundException) { return null; }
    }

    public void Dispose()
    {
        if (_context != IntPtr.Zero) ADL2_Main_Control_Destroy(_context);
        _context = IntPtr.Zero;
        _adapter = -1;
    }
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
/// hardware (WHEA, TDR) ou se o usuário não confirmar a estabilidade a tempo. Com StartBackgroundMonitor roda sozinho, fora da
/// janela; Tick continua público para quem quiser conduzir as leituras. Nada no app o inicia nesta versão.
/// </summary>
[Experimental(OcGate.Diagnostic)]
public sealed class OcRollbackWatchdog
{
    public const int StrikesToRollback = 3;
    private readonly IOverclockProvider _provider;
    private readonly Func<int> _hardwareErrors;
    private readonly object _gate = new();
    private DateTime _deadline;
    private int _errorsAtArm, _strikes;
    private PeriodicTimer? _timer;

    public OcRollbackWatchdog(IOverclockProvider provider, Func<int> hardwareErrors)
    {
        _provider = provider;
        _hardwareErrors = hardwareErrors;
    }

    public bool Armed { get; private set; }

    /// <summary>Intervalo das leituras do monitor de fundo. Vale a partir do próximo StartBackgroundMonitor.</summary>
    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>O ajuste voltou ao original pelo monitor de fundo. Chega fora da thread da janela, com o motivo.</summary>
    public event Action<string>? RolledBack;

    public void Arm(DateTime now, TimeSpan confirmWithin)
    {
        lock (_gate)
        {
            _deadline = now + confirmWithin;
            _errorsAtArm = _hardwareErrors();
            _strikes = 0;
            Armed = true;
        }
    }

    /// <summary>O usuário confirmou que está estável: o ajuste fica, mas a temperatura e os erros continuam vigiados.</summary>
    public void Confirm()
    {
        lock (_gate) _deadline = DateTime.MaxValue;
    }

    public void Disarm()
    {
        lock (_gate) Armed = false;
    }

    /// <summary>Arma o vigia e passa a ler sozinho, até voltar ao original ou até StopBackgroundMonitor.</summary>
    public void StartBackgroundMonitor(TimeSpan confirmWithin)
    {
        StopBackgroundMonitor();
        Arm(DateTime.Now, confirmWithin);
        var timer = new PeriodicTimer(MonitorInterval);
        lock (_gate) _timer = timer;
        _ = Task.Run(() => MonitorLoop(timer));
    }

    /// <summary>Para as leituras e desarma, mantendo o ajuste. Não espera a leitura em andamento: ela confere Armed na trava.</summary>
    public void StopBackgroundMonitor()
    {
        PeriodicTimer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
            Armed = false;
        }
        timer?.Dispose(); // WaitForNextTickAsync devolve false e o laço termina
    }

    private async Task MonitorLoop(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            string? why;
            try { why = Tick(DateTime.Now); }
            catch (Exception ex)
            {
                // Vigia que não consegue ler não pode deixar o ajuste valendo sem vigilância.
                why = "Falha ao vigiar o ajuste: " + ex.Message;
                Disarm();
                try { _provider.Restore(); } catch (Exception restore) { Infrastructure.AppLog.Error("oc", restore); }
            }
            if (why == null) continue;
            Infrastructure.AppLog.Error("oc", "Ajuste desfeito: " + why);
            try { RolledBack?.Invoke(why); } catch (Exception ex) { Infrastructure.AppLog.Error("oc", ex); }
            lock (_gate) if (ReferenceEquals(_timer, timer)) _timer = null;
            timer.Dispose();
            return;
        }
    }

    /// <summary>Motivo da volta ao original, ou null se nada aconteceu nesta leitura.</summary>
    public string? Tick(DateTime now)
    {
        lock (_gate)
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
            var restored = _provider.Restore();
            if (!restored.Ok) Infrastructure.AppLog.Error("oc", "Não foi possível desfazer o ajuste: " + restored.Message);
            return why;
        }
    }
}
