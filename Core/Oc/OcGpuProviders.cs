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
    /// <summary>Diferença em MHz entre o clock máximo em uso e o padrão da placa.</summary>
    bool TryGetOffsetMhz(out int mhz);
    bool TrySetOffsetMhz(int mhz);
    double? TempC();
    /// <summary>Por que a última chamada falhou (para o diagnóstico).</summary>
    string? LastError => null;
}

/// <summary>
/// Offset de clock pelo ADLX (amdadlx64.dll, instalada com o driver AMD). A partir do Navi 4, o "clock máximo" do ADLX já é um offset
/// em MHz, com padrão 0; nas gerações anteriores é um clock absoluto. Offset = atual - padrão vale nos dois casos. O padrão só existe na
/// interface IADLXManualGraphicsTuning2_1: GPU sem ela, sem ajuste manual ou com outro modelo de tuning é recusada, sem gravar.
/// Os números de slot são as posições nas vtables de ISystem.h, IGPUTuning.h, IGPUManualGFXTuning.h, ICollections.h e IPerformanceMonitoring.h.
/// Todas as chamadas passam pela mesma trava: o vigia lê de outra thread.
/// </summary>
[Experimental(OcGate.Diagnostic)]
public sealed unsafe class AmdAdlxOverdriveTransport : IAmdOverdriveTransport, IDisposable
{
    private const string Dll = "amdadlx64.dll", TuningIid = "IADLXManualGraphicsTuning2_1";
    private const int AdlxResetNeeded = 18;
    private const int SlotRelease = 1, SlotQueryInterface = 2;
    private const int SystemGetGpus = 1, SystemGetTuningServices = 8, SystemGetPerformanceServices = 9;
    private const int ListSize = 3, ListBegin = 5, ListAtGpu = 11;
    private const int GpuName = 7;
    private const int TuningIsManualGfxSupported = 8, TuningGetManualGfx = 14;
    private const int ManualMaxRange = 6, ManualGetMax = 7, ManualSetMax = 8, ManualMaxDefault = 13;
    private const int PerfGetCurrentGpuMetrics = 18, MetricsTemperature = 7;

    [StructLayout(LayoutKind.Sequential)] private struct IntRange { public int Min, Max, Step; }

    private readonly object _gate = new();
    private nint _library, _system, _tuningServices, _performance, _gpu, _manual;
    private delegate* unmanaged[Cdecl]<int> _terminate;

    public string? LastError { get; private set; }

    /// <summary>Nome da GPU em uso, depois da primeira chamada que deu certo.</summary>
    public string? AdapterName { get; private set; }

    private static nint Slot(nint obj, int index) => (*(nint**)obj)[index];
    private static bool Ok(int result) => result is >= 0 and <= 2; // ADLX_SUCCEEDED: OK, ALREADY_ENABLED, ALREADY_INITIALIZED

    private static void Release(ref nint obj)
    {
        if (obj == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(obj, SlotRelease))(obj);
        obj = 0;
    }

    private bool Fail(string message)
    {
        LastError = message;
        return false;
    }

    private bool Open()
    {
        if (_manual != 0) return true;
        // Só da pasta do sistema: uma DLL com esse nome em outra pasta não pode ser carregada no lugar da do driver.
        if (_library == 0 && !NativeLibrary.TryLoad(Dll, typeof(AmdAdlxOverdriveTransport).Assembly, DllImportSearchPath.System32, out _library))
            return Fail("O driver AMD (amdadlx64.dll) não está instalado.");
        if (!NativeLibrary.TryGetExport(_library, "ADLXQueryFullVersion", out nint queryVersion)
            || !NativeLibrary.TryGetExport(_library, "ADLXInitialize", out nint initialize)
            || !NativeLibrary.TryGetExport(_library, "ADLXTerminate", out nint terminate))
            return Fail("Esta versão do ADLX não tem as funções esperadas.");

        // A versão vem da própria DLL: o driver pode ser mais antigo ou mais novo que o SDK consultado.
        ulong version;
        if (!Ok(((delegate* unmanaged[Cdecl]<ulong*, int>)queryVersion)(&version))) return Fail("Não deu para ler a versão do ADLX.");
        nint system;
        int result = ((delegate* unmanaged[Cdecl]<ulong, nint*, int>)initialize)(version, &system);
        if (!Ok(result) || system == 0) return Fail($"O ADLX não iniciou (código {result}).");
        _system = system;
        _terminate = (delegate* unmanaged[Cdecl]<int>)terminate;

        if (FindGpu()) return true;
        Close(unload: false);
        return false;
    }

    private bool FindGpu()
    {
        nint services, performance, list;
        if (!Ok(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_system, SystemGetTuningServices))(_system, &services)) || services == 0)
            return Fail("O driver não oferece ajuste de GPU.");
        _tuningServices = services;
        if (Ok(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_system, SystemGetPerformanceServices))(_system, &performance)))
            _performance = performance;
        if (!Ok(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(_system, SystemGetGpus))(_system, &list)) || list == 0)
            return Fail("O ADLX não listou nenhuma GPU.");
        try
        {
            uint size = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(list, ListSize))(list);
            uint begin = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(list, ListBegin))(list);
            for (uint i = begin; i < begin + size; i++)
            {
                nint gpu;
                if (!Ok(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(list, ListAtGpu))(list, i, &gpu)) || gpu == 0) continue;
                if (TryManualTuning(gpu, out nint manual))
                {
                    _gpu = gpu;
                    _manual = manual;
                    byte* name = null;
                    if (Ok(((delegate* unmanaged[Stdcall]<nint, byte**, int>)Slot(gpu, GpuName))(gpu, &name)) && name != null)
                        AdapterName = Marshal.PtrToStringAnsi((nint)name);
                    return true;
                }
                Release(ref gpu);
            }
        }
        finally { Release(ref list); }
        return Fail("Nenhuma GPU AMD com ajuste manual de clock e valor padrão informado.");
    }

    private bool TryManualTuning(nint gpu, out nint manual)
    {
        manual = 0;
        byte supported = 0;
        if (!Ok(((delegate* unmanaged[Stdcall]<nint, nint, byte*, int>)Slot(_tuningServices, TuningIsManualGfxSupported))(_tuningServices, gpu, &supported)) || supported == 0)
            return false;
        nint generic;
        if (!Ok(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(_tuningServices, TuningGetManualGfx))(_tuningServices, gpu, &generic)) || generic == 0)
            return false;
        nint specific = 0;
        int result;
        fixed (char* iid = TuningIid)
            result = ((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Slot(generic, SlotQueryInterface))(generic, iid, &specific);
        Release(ref generic);
        manual = specific;
        return Ok(result) && specific != 0;
    }

    private bool ReadLimits(out IntRange range, out int standard)
    {
        IntRange r;
        int d;
        range = default;
        standard = 0;
        if (!Ok(((delegate* unmanaged[Stdcall]<nint, IntRange*, int>)Slot(_manual, ManualMaxRange))(_manual, &r))) return Fail("O driver não informou a faixa do clock máximo.");
        if (!Ok(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(_manual, ManualMaxDefault))(_manual, &d))) return Fail("O driver não informou o padrão do clock máximo.");
        range = r;
        standard = d;
        return true;
    }

    public bool TryGetOffsetMhz(out int mhz)
    {
        mhz = 0;
        lock (_gate)
        {
            if (!Open() || !ReadLimits(out _, out int standard)) return false;
            int current;
            if (!Ok(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(_manual, ManualGetMax))(_manual, &current)))
                return Fail("O driver não informou o clock máximo em uso.");
            mhz = current - standard;
            return true;
        }
    }

    public bool TrySetOffsetMhz(int mhz)
    {
        lock (_gate)
        {
            if (TrySetOnce(mhz, out bool canRetry)) return true;
            if (!canRetry) return false;
            // Depois de um reinício do driver (TDR) os objetos do ADLX podem ter morrido: abre tudo de novo uma vez.
            Close(unload: false);
            return TrySetOnce(mhz, out _);
        }
    }

    private bool TrySetOnce(int mhz, out bool canRetry)
    {
        canRetry = false;
        if (!Open() || !ReadLimits(out var range, out int standard)) { canRetry = _system != 0; return false; }
        int target = standard + mhz;
        if (target < range.Min || target > range.Max) return Fail($"O driver só aceita de {range.Min} a {range.Max} MHz; o pedido daria {target} MHz.");
        if (range.Step > 1 && (target - range.Min) % range.Step != 0) return Fail($"O driver só aceita passos de {range.Step} MHz.");
        int result = ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(_manual, ManualSetMax))(_manual, target);
        if (Ok(result)) { LastError = null; return true; }
        // O ADLX pediria um reset de fábrica aqui. Isso apagaria os outros ajustes do usuário: fica para ele decidir.
        if (result == AdlxResetNeeded) return Fail("O Auto Tuning do Adrenalin está ligado. Desligue-o para ajustar à mão.");
        canRetry = true;
        return Fail($"O driver recusou o offset (código {result}).");
    }

    public double? TempC()
    {
        lock (_gate)
        {
            if (!Open() || _performance == 0) return null;
            nint metrics;
            if (!Ok(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(_performance, PerfGetCurrentGpuMetrics))(_performance, _gpu, &metrics)) || metrics == 0)
                return null;
            try
            {
                double temperature;
                return Ok(((delegate* unmanaged[Stdcall]<nint, double*, int>)Slot(metrics, MetricsTemperature))(metrics, &temperature)) ? temperature : null;
            }
            finally { Release(ref metrics); }
        }
    }

    private void Close(bool unload)
    {
        Release(ref _manual);
        Release(ref _gpu);
        Release(ref _performance);
        Release(ref _tuningServices);
        if (_system != 0 && _terminate != null) _terminate(); // o sistema do ADLX não tem Release
        _system = 0;
        if (unload && _library != 0)
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate) Close(unload: true);
    }
}

/// <summary>Offset de clock da GPU AMD em MHz: 0 ou de +100 a +250 (até +300 com confirmação), abaixo de 85 °C. Vale até reiniciar.</summary>
[Experimental(OcGate.Diagnostic)]
public sealed class AmdGpuProvider : OverclockProviderBase
{
    private readonly IAmdOverdriveTransport _io;

    public AmdGpuProvider(IAmdOverdriveTransport io, bool isSimulated) : base(isSimulated) => _io = io;

    public override OcVendor Vendor => OcVendor.AmdGpu;
    public override OcTelemetry ReadTelemetry() => new(_io.TempC(), null);
    protected override int? ReadValue() => _io.TryGetOffsetMhz(out int mhz) ? mhz : null;
    protected override string? WriteValue(int value) => _io.TrySetOffsetMhz(value) ? null : _io.LastError ?? "O driver recusou o offset.";
}

/// <summary>
/// Vigia do ajuste. Volta ao valor original na hora em que surge um erro de hardware (WHEA, TDR), se a temperatura ficar no limite
/// por 3 leituras seguidas ou se o usuário não confirmar a estabilidade a tempo. Com StartBackgroundMonitor roda sozinho, fora da
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

    /// <summary>Com o monitor, WHEA e TDR desfazem o ajuste no instante do evento; a contagem fica como conferência em cada leitura.</summary>
    public OcRollbackWatchdog(IOverclockProvider provider, HardwareErrorMonitor errors) : this(provider, () => errors.Count)
    {
        errors.ErrorDetected += ReportHardwareError;
    }

    public bool Armed { get; private set; }

    /// <summary>Intervalo das leituras do monitor de fundo. Vale a partir do próximo StartBackgroundMonitor.</summary>
    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Espera entre as tentativas de desfazer: logo depois de um TDR o driver ainda está voltando.</summary>
    public TimeSpan RestoreRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>O ajuste voltou ao original sem Tick direto (monitor de fundo ou erro de hardware). Chega fora da thread da janela, com o motivo.</summary>
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
                lock (_gate)
                {
                    if (!Armed) return;
                    Armed = false;
                    why += RestoreWithRetry();
                }
            }
            if (why == null) continue;
            Finish(why);
            return;
        }
    }

    /// <summary>
    /// Erro de hardware (WHEA, TDR): desfaz na hora, sem esperar a próxima leitura. Sem efeito se o vigia não estiver armado.
    /// O HardwareErrorMonitor chama aqui; pode ser chamado de qualquer thread.
    /// </summary>
    public void ReportHardwareError(string detail)
    {
        string why;
        lock (_gate)
        {
            if (!Armed) return;
            Armed = false;
            why = $"Erro de hardware depois do ajuste ({detail})." + RestoreWithRetry();
        }
        Finish(why);
    }

    /// <summary>Tenta 3 vezes. Devolve o aviso que acompanha o motivo, vazio se deu certo.</summary>
    private string RestoreWithRetry()
    {
        const int attempts = 3;
        string error = "";
        for (int i = 0; i < attempts; i++)
        {
            if (i > 0) Thread.Sleep(RestoreRetryDelay);
            try
            {
                var restored = _provider.Restore();
                if (restored.Ok) return "";
                error = restored.Message;
            }
            catch (Exception ex) { error = ex.Message; }
        }
        Infrastructure.AppLog.Error("oc", "Não foi possível desfazer o ajuste: " + error);
        return " Não foi possível desfazer o ajuste: reinicie o PC para voltar ao padrão.";
    }

    private void Finish(string why)
    {
        Infrastructure.AppLog.Error("oc", "Ajuste desfeito: " + why);
        PeriodicTimer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        try { RolledBack?.Invoke(why); } catch (Exception ex) { Infrastructure.AppLog.Error("oc", ex); }
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
            return why + RestoreWithRetry();
        }
    }
}
