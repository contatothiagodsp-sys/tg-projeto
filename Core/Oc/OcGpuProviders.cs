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
    /// <summary>4 = NVML_ERROR_NO_PERMISSION. Com a trava fechada, a chamada nem chega à NVML.</summary>
    public int SetGpcOffset(int mhz) => OcGate.Block(isSimulated: false) != null ? 4 : Nvml.SetGpcClkVfOffset(_device, mhz);
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
    /// <summary>Solta os objetos do driver: a próxima chamada abre tudo de novo. Para depois de um TDR.</summary>
    void Reconnect() { }
}

/// <summary>
/// Offset de clock pelo ADLX (amdadlx64.dll, instalada com o driver AMD). A partir do Navi 4, o "clock máximo" do ADLX já é um offset
/// em MHz, com padrão 0; nas gerações anteriores é um clock absoluto. Offset = atual - padrão vale nos dois casos. O padrão só existe na
/// interface IADLXManualGraphicsTuning2_1: GPU sem ela, sem ajuste manual ou com outro modelo de tuning é recusada, sem gravar.
/// Os números de slot são as posições nas vtables de ISystem.h, IGPUTuning.h, IGPUManualGFXTuning.h, ICollections.h e IPerformanceMonitoring.h.
/// Conferidos com os headers oficiais do SDK: slots, assinaturas, stdcall nos métodos, cdecl nas exportações e ADLX_RESET_NEEDED = 18.
/// Referências: cada interface adquirida é liberada uma vez; o sistema do ADLX não tem Release e termina em ADLXTerminate, por último.
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
    private readonly bool _injected;
    private nint _queryVersion, _initialize, _terminateExport;

    public AmdAdlxOverdriveTransport() { }

    /// <summary>Para os testes: um ADLX falso em memória, que confere as referências sem precisar do driver.</summary>
    internal AmdAdlxOverdriveTransport(nint queryVersion, nint initialize, nint terminate)
    {
        _injected = true;
        (_queryVersion, _initialize, _terminateExport) = (queryVersion, initialize, terminate);
    }

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
        if (!_injected && !LoadLibrary()) return false;
        nint queryVersion = _queryVersion, initialize = _initialize, terminate = _terminateExport;

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

    /// <summary>Só da pasta do sistema e com assinatura válida: uma DLL com esse nome em outra pasta não entra no lugar da do driver.</summary>
    private bool LoadLibrary()
    {
        if (_library != 0) return true;
        string path = System.IO.Path.Combine(Environment.SystemDirectory, Dll);
        if (!System.IO.File.Exists(path)) return Fail("O driver AMD (amdadlx64.dll) não está instalado.");
        int trust = CorsairSdkInstaller.WinVerifyTrustFile(path);
        if (trust != 0) return Fail($"A assinatura de {Dll} não confere (0x{trust:X8}): não foi carregada.");
        if (!NativeLibrary.TryLoad(path, out _library)) return Fail($"Não deu para carregar {Dll}.");
        if (NativeLibrary.TryGetExport(_library, "ADLXQueryFullVersion", out _queryVersion)
            && NativeLibrary.TryGetExport(_library, "ADLXInitialize", out _initialize)
            && NativeLibrary.TryGetExport(_library, "ADLXTerminate", out _terminateExport)) return true;
        NativeLibrary.Free(_library);
        _library = 0;
        return Fail("Esta versão do ADLX não tem as funções esperadas.");
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
        if (Ok(result) && specific != 0)
        {
            manual = specific;
            return true;
        }
        Release(ref specific); // sem isto, uma resposta de erro com ponteiro preenchido ficaria sem dono
        return false;
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
            if (TryGetOnce(out mhz)) return true;
            if (_system == 0) return false;
            Close(unload: false); // depois de um TDR os objetos do ADLX podem ter morrido
            return TryGetOnce(out mhz);
        }
    }

    private bool TryGetOnce(out int mhz)
    {
        mhz = 0;
        if (!Open() || !ReadLimits(out _, out int standard)) return false;
        int current;
        if (!Ok(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(_manual, ManualGetMax))(_manual, &current)))
            return Fail("O driver não informou o clock máximo em uso.");
        long offset = (long)current - standard;
        if (offset is < int.MinValue or > int.MaxValue) return Fail("O driver informou um clock fora de qualquer faixa possível.");
        mhz = (int)offset;
        return true;
    }

    public void Reconnect()
    {
        lock (_gate) Close(unload: false);
    }

    public bool TrySetOffsetMhz(int mhz)
    {
        if (OcGate.Block(isSimulated: false) is string gate) return Fail(gate);
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
        long target = (long)standard + mhz;
        if (target < range.Min || target > range.Max) return Fail($"O driver só aceita de {range.Min} a {range.Max} MHz; o pedido daria {target} MHz.");
        if (range.Step > 1 && (target - range.Min) % range.Step != 0) return Fail($"O driver só aceita passos de {range.Step} MHz.");
        int result = ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(_manual, ManualSetMax))(_manual, (int)target);
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
            if (TempOnce() is double temperature) return temperature;
            if (_system == 0) return null;
            Close(unload: false); // sem isto, depois de um TDR o sensor ficaria sem resposta até fechar o app
            return TempOnce();
        }
    }

    private double? TempOnce()
    {
        if (!Open() || _performance == 0) return null;
        nint metrics = 0;
        if (!Ok(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(_performance, PerfGetCurrentGpuMetrics))(_performance, _gpu, &metrics)) || metrics == 0)
        {
            Release(ref metrics);
            return null;
        }
        try
        {
            double temperature;
            return Ok(((delegate* unmanaged[Stdcall]<nint, double*, int>)Slot(metrics, MetricsTemperature))(metrics, &temperature)) ? temperature : null;
        }
        finally { Release(ref metrics); }
    }

    private void Close(bool unload)
    {
        Release(ref _manual);
        Release(ref _gpu);
        Release(ref _performance);
        Release(ref _tuningServices);
        if (_system != 0 && _terminate != null)
        {
            int result = _terminate(); // o sistema do ADLX não tem Release
            if (!Ok(result)) Infrastructure.AppLog.Error("oc", $"ADLXTerminate devolveu {result}.");
        }
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
    public override void Reconnect() => _io.Reconnect();
    protected override int? ReadValue() => _io.TryGetOffsetMhz(out int mhz) ? mhz : null;
    protected override string? WriteValue(int value) => _io.TrySetOffsetMhz(value) ? null : _io.LastError ?? "O driver recusou o offset.";
}

/// <summary>Como terminou uma volta ao original. Restored só com a confirmação do provedor.</summary>
public sealed record OcRollbackResult(bool Restored, string Reason, int Attempts);

/// <summary>
/// Vigia do ajuste. Volta ao valor original quando surge um erro de hardware (WHEA, TDR), se a temperatura ficar no limite por 3 leituras
/// seguidas ou se o usuário não confirmar a estabilidade a tempo. O evento de hardware só marca e enfileira, e a restauração roda numa tarefa
/// própria, uma vez, com limite de tentativas, sem trava presa durante a espera pelo driver. Nada no app o inicia nesta versão.
/// </summary>
[Experimental(OcGate.Diagnostic)]
public sealed class OcRollbackWatchdog : IDisposable
{
    public const int StrikesToRollback = 3, RestoreAttempts = 3;
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);
    private readonly IOverclockProvider _provider;
    private readonly Func<int> _hardwareErrors;
    private readonly HardwareErrorMonitor? _monitor;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private DateTime _deadline;
    private int _errorsAtArm, _strikes;
    private PeriodicTimer? _timer;
    private Task _monitorLoop = Task.CompletedTask, _rollback = Task.CompletedTask;
    private bool _disposed;

    public OcRollbackWatchdog(IOverclockProvider provider, Func<int> hardwareErrors)
    {
        _provider = provider;
        _hardwareErrors = hardwareErrors;
    }

    /// <summary>Com o monitor, WHEA e TDR desfazem o ajuste assim que o evento chega; a contagem fica como conferência em cada leitura.</summary>
    public OcRollbackWatchdog(IOverclockProvider provider, HardwareErrorMonitor errors) : this(provider, () => errors.Count)
    {
        _monitor = errors;
        errors.ErrorDetected += ReportHardwareError;
    }

    public bool Armed { get; private set; }

    /// <summary>Intervalo das leituras do monitor de fundo. Vale a partir do próximo StartBackgroundMonitor.</summary>
    public TimeSpan MonitorInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Espera entre as tentativas de desfazer: logo depois de um TDR o driver ainda está voltando.</summary>
    public TimeSpan RestoreRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A volta ao original terminou, sem Tick direto. Chega fora da thread da janela, com o motivo. Se falhou, o texto diz, e LastRollback também.</summary>
    public event Action<string>? RolledBack;

    /// <summary>Resultado da última volta ao original. Null = nenhuma aconteceu.</summary>
    public OcRollbackResult? LastRollback { get; private set; }

    public void Arm(DateTime now, TimeSpan confirmWithin)
    {
        int errors = _hardwareErrors(); // pode ler o registro de eventos: fora da trava
        lock (_gate)
        {
            _deadline = now + confirmWithin;
            _errorsAtArm = errors;
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
        lock (_gate)
        {
            _timer = timer;
            _monitorLoop = Task.Run(() => MonitorLoop(timer));
        }
    }

    /// <summary>Para as leituras e desarma, mantendo o ajuste. Termina quando a leitura em andamento e uma restauração já iniciada terminarem.</summary>
    public Task StopBackgroundMonitorAsync()
    {
        PeriodicTimer? timer;
        Task pending;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
            Armed = false;
            pending = Task.WhenAll(_monitorLoop, _rollback);
        }
        timer?.Dispose(); // WaitForNextTickAsync devolve false e o laço termina
        return pending;
    }

    public void StopBackgroundMonitor()
    {
        var pending = StopBackgroundMonitorAsync();
        if (!pending.Wait(ShutdownWait)) Infrastructure.AppLog.Error("oc", "O vigia não terminou no tempo de encerramento.");
    }

    /// <summary>Espera a restauração enfileirada por um erro de hardware. false = não terminou no tempo pedido.</summary>
    public bool WaitForRollback(TimeSpan timeout)
    {
        Task pending;
        lock (_gate) pending = _rollback;
        return pending.Wait(timeout);
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
                if (!TryClaimRollback()) return;
                why = "Falha ao vigiar o ajuste: " + ex.Message + await RestoreWithRetryAsync("Falha ao vigiar o ajuste.").ConfigureAwait(false);
            }
            if (why == null) continue;
            Finish(why);
            return;
        }
    }

    /// <summary>Só uma origem ganha o direito de desfazer: leitura, erro de hardware ou falha do vigia.</summary>
    private bool TryClaimRollback()
    {
        lock (_gate)
        {
            if (!Armed) return false;
            Armed = false;
            return true;
        }
    }

    /// <summary>
    /// Erro de hardware (WHEA, TDR): marca e enfileira, sem esperar a próxima leitura. Sem efeito se o vigia não estiver armado. Volta na hora:
    /// esta chamada vem do callback do registro de eventos, que não pode esperar o driver voltar de um TDR. Pode ser chamada de qualquer thread.
    /// </summary>
    public void ReportHardwareError(string detail)
    {
        string why = $"Erro de hardware depois do ajuste ({detail}).";
        // Na mesma trava que desarma: quem chama StopBackgroundMonitorAsync vê Armed ainda ligado ou já vê a tarefa de restauração, nunca o meio-termo.
        lock (_gate)
        {
            if (!Armed || _disposed) return;
            Armed = false;
            _rollback = Task.Run(async () => Finish(why + await RestoreWithRetryAsync(why).ConfigureAwait(false)));
        }
    }

    /// <summary>
    /// Tenta RestoreAttempts vezes, reabrindo a conexão entre elas, sem nenhuma trava presa. No encerramento, as esperas são puladas,
    /// mas as tentativas continuam: sair sem tentar deixaria o ajuste valendo. Devolve o aviso que acompanha o motivo, vazio se deu certo.
    /// </summary>
    private async Task<string> RestoreWithRetryAsync(string reason)
    {
        string error = "";
        for (int i = 0; i < RestoreAttempts; i++)
        {
            if (i > 0)
            {
                try { await Task.Delay(RestoreRetryDelay, _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
            try
            {
                if (i > 0) _provider.Reconnect();
                var restored = _provider.Restore();
                if (restored.Ok)
                {
                    LastRollback = new OcRollbackResult(true, reason, i + 1);
                    Infrastructure.AppLog.Info("oc", "Ajuste desfeito: " + reason);
                    return "";
                }
                error = restored.Message;
            }
            catch (Exception ex) { error = ex.Message; }
        }
        LastRollback = new OcRollbackResult(false, reason, RestoreAttempts);
        Infrastructure.AppLog.Error("oc", $"Não foi possível desfazer o ajuste ({reason}): {error}");
        return " Não foi possível desfazer o ajuste: reinicie o PC para voltar ao padrão.";
    }

    private void Finish(string why)
    {
        PeriodicTimer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        try { RolledBack?.Invoke(why); } catch (Exception ex) { Infrastructure.AppLog.Error("oc", ex); }
    }

    /// <summary>Motivo da volta ao original, ou null se nada aconteceu nesta leitura. As leituras e a restauração ficam fora da trava.</summary>
    public string? Tick(DateTime now)
    {
        DateTime deadline;
        lock (_gate)
        {
            if (!Armed) return null;
            deadline = _deadline;
        }
        string? why = null;
        bool hot = false;
        if (_hardwareErrors() > Volatile.Read(ref _errorsAtArm)) why = "Erro de hardware depois do ajuste.";
        else if (now >= deadline) why = "O ajuste não foi confirmado a tempo.";
        else hot = OcLimits.CheckTelemetry(_provider.Vendor, _provider.ReadTelemetry()) != null;

        lock (_gate)
        {
            if (!Armed) return null; // alguém desarmou ou já desfez durante a leitura
            if (why == null)
            {
                _strikes = hot ? _strikes + 1 : 0;
                if (_strikes < StrikesToRollback) return null;
                why = "Temperatura ou tensão no limite, ou sensor sem resposta.";
            }
            Armed = false;
        }
        return why + RestoreWithRetryAsync(why).GetAwaiter().GetResult();
    }

    /// <summary>Solta o evento, para de ler e espera o que está em andamento, com limite: o encerramento do app não fica preso no driver.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (_monitor != null) _monitor.ErrorDetected -= ReportHardwareError;
        _lifetime.Cancel();
        StopBackgroundMonitor();
        _lifetime.Dispose();
    }
}
