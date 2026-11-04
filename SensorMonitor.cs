using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;

namespace ThgOptimization;

/// <summary>Temperaturas lidas pelo motor de fans. Null = sem leitura, ou não foi pedida.</summary>
public readonly record struct FanInputs(float? CpuC, float? GpuC);

/// <summary>Clock e carga da CPU pelo próprio Windows. Null = sem valor nesta rodada, sem ser falha (ou não foi pedido).</summary>
internal readonly record struct NativeCpuReading(float? ClockAvgMhz, float? ClockMaxMhz, bool ClockFailed, float? LoadPercent, bool LoadFailed);

/// <summary>
/// Reserva em modo usuário para quando a biblioteca de sensores não entrega clock ou carga da CPU: não usa driver nenhum.
/// O clock vem de CallNtPowerInformation, que em muitos PCs informa o clock-base e não o turbo real: serve de referência, não de medição.
/// </summary>
internal sealed class NativeCpuSensors
{
    private const int ProcessorInformation = 11;
    private const ushort AllProcessorGroups = 0xFFFF;
    private (long Idle, long Kernel, long User)? _lastTimes;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessorPowerInformation { public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState; }

    public NativeCpuReading Read(bool wantClock, bool wantLoad)
    {
        float? avg = null, max = null, load = null;
        bool clockFailed = false, loadFailed = false;
        if (wantClock)
        {
            (avg, max) = ReadClock();
            clockFailed = avg == null;
        }
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            // Lê sempre, mesmo sem precisar: quando a biblioteca falhar, o primeiro delta já sai de um intervalo curto.
            if (wantLoad && _lastTimes is { } last) load = LoadPercent(last, (idle, kernel, user));
            _lastTimes = (idle, kernel, user);
        }
        else if (wantLoad) loadFailed = true;
        return new NativeCpuReading(avg, max, clockFailed, load, loadFailed);
    }

    /// <summary>Uso entre duas leituras de GetSystemTimes. O tempo de kernel já inclui o ocioso. Null = intervalo vazio.</summary>
    internal static float? LoadPercent((long Idle, long Kernel, long User) before, (long Idle, long Kernel, long User) now)
    {
        long idle = now.Idle - before.Idle, total = now.Kernel - before.Kernel + now.User - before.User;
        if (total <= 0 || idle < 0) return null;
        return Math.Clamp((float)(100.0 * (total - idle) / total), 0f, 100f);
    }

    /// <summary>Clock médio atual e o maior máximo entre os processadores lógicos, em MHz. Null = o Windows não deu valor utilizável.</summary>
    internal static (float? Avg, float? Max) Summarize(ReadOnlySpan<ProcessorPowerInformation> cpus)
    {
        long sum = 0;
        uint highest = 0;
        foreach (var c in cpus)
        {
            sum += c.CurrentMhz;
            highest = Math.Max(highest, c.MaxMhz);
        }
        if (cpus.Length == 0 || sum <= 0) return (null, null);
        return ((float)sum / cpus.Length, highest > 0 ? highest : null);
    }

    private static (float? Avg, float? Max) ReadClock()
    {
        try
        {
            uint count = GetActiveProcessorCount(AllProcessorGroups);
            if (count == 0) return (null, null);
            var cpus = new ProcessorPowerInformation[count];
            int size = cpus.Length * Marshal.SizeOf<ProcessorPowerInformation>();
            return CallNtPowerInformation(ProcessorInformation, IntPtr.Zero, 0, cpus, size) == 0 ? Summarize(cpus) : (null, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Infrastructure.AppLog.Debug("sensores", ex);
            return (null, null);
        }
    }

    /// <summary>O dispositivo existe? Acesso negado também responde sim: quem nega é o driver carregado.</summary>
    internal static bool DeviceResponds(string path)
    {
        const uint GenericRead = 0x80000000, ShareReadWrite = 3, OpenExisting = 3;
        const int AccessDenied = 5;
        using SafeFileHandle h = CreateFile(path, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        return !h.IsInvalid || Marshal.GetLastWin32Error() == AccessDenied;
    }

    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CallNtPowerInformation(int level, IntPtr input, int inputLength, [Out] ProcessorPowerInformation[] output, int outputLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")]
    private static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}

/// <summary>
/// Uma leitura por vez, e cada uma termina só depois de aplicada. Quem chega com outra em andamento espera por ela, sem ler de novo, a não ser
/// que ela fosse a leve e este pedido queira tudo: aí lê completa em seguida. Assim duas leituras nunca aplicam fora de ordem na lista.
/// </summary>
internal sealed class SensorRefreshGate
{
    private readonly object _sync = new();
    private Task _inflight = Task.CompletedTask;
    private bool _inflightLight;

    public async Task RunAsync<T>(bool light, Func<Task<T>> poll, Action<T> apply)
    {
        while (true)
        {
            Task current;
            bool mine = false, covered = false;
            lock (_sync)
            {
                if (_inflight.IsCompleted)
                {
                    _inflightLight = light;
                    current = _inflight = PollAndApply(poll, apply);
                    mine = true;
                }
                else
                {
                    current = _inflight;
                    covered = light || !_inflightLight;
                }
            }
            if (mine)
            {
                await current;
                return;
            }
            // A falha é de quem pediu aquela leitura. Aqui só importa que ela acabou.
            try { await current; }
            catch (Exception ex) { Infrastructure.AppLog.Debug("sensores", ex); }
            if (covered) return;
        }
    }

    private static async Task PollAndApply<T>(Func<Task<T>> poll, Action<T> apply) => apply(await poll());
}

/// <summary>Encapsula o LibreHardwareMonitorLib e mantém a lista de sensores atualizada.</summary>
public sealed class SensorMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly Dictionary<string, SensorRow> _index = new();
    /// <summary>Mesmo índice pela instância do sensor: evita montar o Identifier em texto para cada sensor a cada leitura.</summary>
    private readonly Dictionary<ISensor, SensorRow> _bySensor = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, FanOutput> _controls = new();

    /// <summary>Saídas de fan controláveis (placa-mãe), descobertas na primeira leitura completa.</summary>
    public IReadOnlyCollection<FanOutput> FanOutputs => _controls.Values;

    /// <summary>Temperatura principal da placa de vídeo (núcleo/edge), em °C.</summary>
    public float? GpuTemperature { get; private set; }

    /// <summary>Temperatura do núcleo da GPU (o sensor "Core", senão a primeira com leitura): RGB por temperatura.</summary>
    public float? GpuCoreTemperature { get; private set; }

    /// <summary>Carga do núcleo da GPU em % (telas LCD).</summary>
    public float? GpuCoreLoad { get; private set; }

    /// <summary>Lê só a placa-mãe sem passar pelos discos: o barramento fora da janela, a lista de sensores na janela. Chamado da janela.</summary>
    public async Task RefreshMotherboardAsync()
    {
        if (!_open) return;
        Apply(await Task.Run(() => Poll(h => h.HardwareType == HardwareType.Motherboard, includesCpu: false)));
    }

    /// <summary>
    /// Leitura do motor de fans, de qualquer thread e sem tocar na lista de sensores (ela é da janela). Atualiza só o hardware pedido
    /// e pega as temperaturas direto dos sensores. Null = sensores fechados, nada a fazer nesta rodada.
    /// </summary>
    public FanInputs? ReadFanInputs(bool cpu, bool gpu, bool motherboard)
    {
        float? cpuTemp = null, gpuTemp = null;
        lock (_hw)
        {
            if (!_open) return null;
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    if (!cpu) continue;
                    UpdateTree(hw);
                    cpuTemp ??= CpuTemperatureOf(hw);
                }
                else if (hw.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)
                {
                    if (!gpu) continue;
                    UpdateTree(hw);
                    gpuTemp ??= FirstTemperature(hw);
                }
                else if (motherboard && hw.HardwareType == HardwareType.Motherboard) UpdateTree(hw);
            }
        }
        return new FanInputs(cpuTemp, gpuTemp);
    }

    private static void UpdateTree(IHardware hw)
    {
        hw.Update();
        foreach (var sub in hw.SubHardware) UpdateTree(sub);
    }

    /// <summary>Mesma regra de CpuTemperature: Package/Tctl/Tdie, senão a maior entre os núcleos.</summary>
    private static float? CpuTemperatureOf(IHardware hw)
    {
        float? highest = null;
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Temperature || s.Value is not float t || t <= 0) continue;
            if (s.Name.Contains("Package") || s.Name.Contains("Tctl") || s.Name.Contains("Tdie")) return t;
            if (highest == null || t > highest) highest = t;
        }
        return highest;
    }

    private static float? FirstTemperature(IHardware hw)
    {
        foreach (var s in hw.Sensors)
            if (s.SensorType == SensorType.Temperature && s.Value is > 0 and float t) return t;
        return null;
    }

    private volatile bool _open;

    /// <summary>
    /// O LibreHardwareMonitor não é seguro entre threads: leitura dos sensores (em segundo plano) e comando das fans
    /// (na janela) passam por esta trava. Sem ela, duas conversas simultâneas com o Super I/O podiam trocar o banco do chip.
    /// </summary>
    private readonly object _hw = new();

    public ObservableCollection<SensorRow> Rows { get; } = new();
    public bool IsOpen => _open;

    public SensorMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsControllerEnabled = true
        };
    }

    /// <summary>Detecta o hardware (leva de 0,5 a alguns segundos) fora da thread da janela. Até terminar, Refresh não faz nada.</summary>
    public Task OpenAsync() => Task.Run(() =>
    {
        _computer.Open();
        CheckPawnIoDriver(); // antes de liberar as leituras: a primeira já decide o aviso com o estado real do driver
        _open = true;
    });

    /// <summary>O driver PawnIO está carregado? Instalado não basta: é preciso o serviço rodando ou o dispositivo respondendo.</summary>
    public bool IsPawnIoAvailable { get; private set; }

    /// <summary>Texto do aviso no topo da aba de Sensores. Null = nada a avisar.</summary>
    public string? DiagnosticBanner { get; private set; }

    /// <summary>Confere o driver agora. Rápido, mas fala com o Gerenciador de Serviços: a leitura periódica repete só de vez em quando.</summary>
    public void CheckPawnIoDriver()
    {
        IsPawnIoAvailable = Infrastructure.ServiceStatus.IsRunning("PawnIO") == true || NativeCpuSensors.DeviceResponds(@"\\.\PawnIO");
        Volatile.Write(ref _lastPawnIoCheck, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private long _lastPawnIoCheck;
    private static readonly TimeSpan PawnIoCheckInterval = TimeSpan.FromSeconds(30);
    /// <summary>Exceções de leitura na última rodada que tocou a CPU. Escrito na leitura e lido depois dela.</summary>
    private int _readFailures;
    private string _cpuName = "", _gpuName = "";
    private readonly NativeCpuSensors _native = new();
    private float? _nativeCpuLoad;
    private readonly Dictionary<string, SensorRow> _nativeRows = new();

    /// <summary>
    /// O aviso da aba. Sem o driver, é a causa de quase tudo. Com ele, só avisa quando houve falha de fato, e cita as causas conhecidas que se aplicam a este PC.
    /// </summary>
    internal static string? BuildBanner(bool pawnIoAvailable, int readFailures, bool cpuSensorsSilent, string cpuName, string gpuName, bool hvciOn)
    {
        if (!pawnIoAvailable)
            return "O driver PawnIO não está ativo: temperatura, consumo e clock da CPU podem ficar indisponíveis. Reinstale pelo instalador oficial do PawnIO e reabra o tHg.";
        if (readFailures == 0 && !cpuSensorsSilent) return null;
        var why = new List<string>();
        if (System.Text.RegularExpressions.Regex.IsMatch(cpuName, @"Ryzen\b.*\b9\d{3}", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            why.Add("Ryzen 9000 ainda pode não ter suporte completo na biblioteca de sensores");
        if (System.Text.RegularExpressions.Regex.IsMatch(gpuName, @"\bRX\s*9\d{3}", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            why.Add("Radeon RX 9000 ainda pode não ter suporte completo na biblioteca de sensores");
        if (hvciOn) why.Add("a Integridade de Memória (HVCI) está ligada e pode bloquear o acesso aos registradores");
        return "Alguns registradores de hardware não responderam: parte dos sensores aparece como Indisponível." + (why.Count > 0 ? " Possível causa: " + string.Join("; ", why) + "." : "");
    }

    private static readonly Lazy<bool> HvciOn = new(() =>
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            return key?.GetValue("Enabled") is int on && on != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    });

    /// <summary>
    /// light = só CPU, GPU e memória. Ler discos (S.M.A.R.T.), placa-mãe (Super I/O, controlador embarcado) e controladoras
    /// segura o barramento por um instante e é uma causa conhecida de microengasgos em jogo; no modo leve isso fica de fora.
    /// A conversa com o hardware (a parte lenta: 20 a 200 ms com discos e placa-mãe) roda numa tarefa de fundo; só a atualização
    /// das linhas volta para a janela. Chamado sempre da janela. Ver <see cref="SensorRefreshGate"/> para leituras simultâneas.
    /// </summary>
    public Task RefreshAsync(bool light) => !_open ? Task.CompletedTask : _refreshGate.RunAsync(light,
        () => Task.Run(() => Poll(h => !light || IsLight(h), includesCpu: true)),
        result => { if (_open) Apply(result); }); // os sensores podem ter fechado durante a leitura

    private readonly SensorRefreshGate _refreshGate = new();

    private static bool IsLight(IHardware h) =>
        h.HardwareType is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.Memory;

    private readonly record struct Reading(IHardware Hw, ISensor Sensor, SensorType Type, float? Value, float? Min, float? Max, bool Failed);

    /// <summary>O que uma rodada leu. Null em Native = esta rodada não tocou a CPU, e as linhas nativas ficam como estão.</summary>
    private readonly record struct PollResult(List<Reading> Readings, NativeCpuReading? Native, int Failures);

    /// <summary>Clock e Carga sem leitura válida viram linha "Indisponível". 0 % de carga é leitura válida; 0 MHz não é.</summary>
    internal static bool IsUnavailable(SensorType type, float? value, bool failed)
    {
        if (type is not (SensorType.Clock or SensorType.Load)) return false;
        return failed || value is not float v || !float.IsFinite(v) || (type == SensorType.Clock && v <= 0);
    }

    private PollResult Poll(Func<IHardware, bool> which, bool includesCpu)
    {
        var list = new List<Reading>(256);
        int failures = 0;
        NativeCpuReading? native = null;
        lock (_hw)
        {
            if (!_open) return new PollResult(list, null, 0);
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu && _cpuName.Length == 0) _cpuName = hw.Name;
                else if (hw.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel && _gpuName.Length == 0) _gpuName = hw.Name;
                if (which(hw)) Collect(hw, list, ref failures);
            }
        }
        if (includesCpu)
        {
            bool clock = false, load = false;
            foreach (var r in list)
            {
                if (r.Hw.HardwareType != HardwareType.Cpu || IsUnavailable(r.Type, r.Value, r.Failed)) continue;
                // "Bus Speed" não diz o clock dos núcleos: sozinho, não dispensa a leitura nativa.
                if (r.Type == SensorType.Clock && !r.Sensor.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase)) clock = true;
                else if (r.Type == SensorType.Load) load = true;
            }
            native = _native.Read(!clock, !load);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastPawnIoCheck)) >= PawnIoCheckInterval) CheckPawnIoDriver();
        }
        return new PollResult(list, native, failures);
    }

    /// <summary>Cada chamada isolada: um chip que falha no Update ou um sensor que lança não derrubam o resto da árvore.</summary>
    private static void Collect(IHardware hw, List<Reading> list, ref int failures)
    {
        try { hw.Update(); }
        catch (Exception ex) { failures++; Infrastructure.AppLog.Debug("sensores", ex); }

        try
        {
            foreach (var s in hw.Sensors) list.Add(Read(hw, s, ref failures));
            foreach (var sub in hw.SubHardware) Collect(sub, list, ref failures);
        }
        catch (Exception ex) { failures++; Infrastructure.AppLog.Debug("sensores", ex); }
    }

    private static Reading Read(IHardware hw, ISensor s, ref int failures)
    {
        SensorType type = SensorType.Load;
        try
        {
            type = s.SensorType;
            return new Reading(hw, s, type, s.Value, s.Min, s.Max, false);
        }
        catch (Exception ex)
        {
            failures++;
            Infrastructure.AppLog.Debug("sensores", ex);
            return new Reading(hw, s, type, null, null, null, true);
        }
    }

    private void Apply(PollResult result)
    {
        foreach (var reading in result.Readings)
        {
            try { ApplyReading(reading); }
            catch (Exception ex) { Infrastructure.AppLog.Debug("sensores", ex); } // uma linha ruim não apaga as outras
        }
        if (result.Native is { } native) ApplyNative(native);
        // A leitura só da placa-mãe não diz nada sobre a CPU: não apaga o que a última rodada completa contou.
        if (result.Native != null) _readFailures = result.Failures;
        UpdateSummary();
    }

    private void ApplyNative(NativeCpuReading n)
    {
        ApplyNativeRow("native/cpu/clock-avg", "CPU · Clock Médio", SensorType.Clock, n.ClockFailed ? null : n.ClockAvgMhz, false);
        ApplyNativeRow("native/cpu/clock-max", "CPU · Clock Máximo", SensorType.Clock, n.ClockFailed ? null : n.ClockMaxMhz, false);
        ApplyNativeRow("native/cpu/clock", "CPU · Clock dos Núcleos", SensorType.Clock, null, n.ClockFailed);
        ApplyNativeRow("native/cpu/load", "CPU · Carga Total", SensorType.Load, n.LoadPercent, n.LoadFailed);
        _nativeCpuLoad = n.LoadFailed ? null : n.LoadPercent;
    }

    /// <summary>Linha só enquanto a biblioteca não entrega aquele valor: quando ela volta a ler, a reserva sai da lista.</summary>
    private void ApplyNativeRow(string id, string name, SensorType type, float? value, bool unavailable)
    {
        if (value == null && !unavailable)
        {
            if (_nativeRows.Remove(id, out var gone)) Rows.Remove(gone);
            return;
        }
        if (!_nativeRows.TryGetValue(id, out var row))
        {
            row = new SensorRow
            {
                Id = id, Hardware = _cpuName.Length > 0 ? _cpuName : "CPU", Category = SensorRow.CategoryName(type),
                Name = name, Type = type, HardwareType = HardwareType.Cpu,
            };
            _nativeRows[id] = row;
            Rows.Add(row);
        }
        row.Update(value, null, null, unavailable);
    }

    private void ApplyReading(Reading reading)
    {
        var (hw, s, type, rawValue, min, max, failed) = reading;
        bool unavailable = IsUnavailable(type, rawValue, failed);
        float? value = unavailable ? null : rawValue;
        {
            // Saídas de fan (Control) seguem o caminho completo: lá elas entram na lista das curvas.
            if (type != SensorType.Control && _bySensor.TryGetValue(s, out var row))
            {
                row.Update(value, min, max, unavailable);
                return;
            }
            string id = s.Identifier.ToString();
            if (!_index.TryGetValue(id, out row))
            {
                row = new SensorRow
                {
                    Id = id,
                    Hardware = hw.Name,
                    Category = SensorRow.CategoryName(s.SensorType),
                    Name = s.Name,
                    Type = s.SensorType,
                    HardwareType = hw.HardwareType
                };
                _index[id] = row;
                Rows.Add(row);
            }
            _bySensor[s] = row;
            row.Update(value, min, max, unavailable);
            // Saídas de fan da placa-mãe (Super I/O) que aceitam controle por software: guardadas para as curvas.
            if (s.SensorType == SensorType.Control && s.Control is { } control && hw.HardwareType == HardwareType.SuperIO && !_controls.ContainsKey(id))
            {
                var rpm = hw.Sensors.FirstOrDefault(f => f.SensorType == SensorType.Fan && f.Index == s.Index);
                _controls[id] = new FanOutput(id, hw.Name, s.Name,
                    v => { lock (_hw) control.SetSoftware(v); }, () => { lock (_hw) control.SetDefault(); },
                    () => rpm?.Value is float r && r > 0 ? $"{r:0} RPM" : "sem leitura de RPM",
                    control.MinSoftwareValue, control.MaxSoftwareValue);
            }
        }
    }

    /// <summary>Temperatura principal da CPU: Package (Intel) / Tctl-Tdie (AMD), senão a maior entre os núcleos.</summary>
    public float? CpuTemperature { get; private set; }

    /// <summary>Carga total da CPU em %.</summary>
    public float? CpuLoad { get; private set; }

    /// <summary>Consumo do pacote da CPU em W.</summary>
    public float? CpuPower { get; private set; }

    /// <summary>Tensão dos núcleos da CPU em V. Null se nenhum sensor informa.</summary>
    public float? CpuVcore { get; private set; }

    /// <summary>Tensão do SoC da CPU AMD (VSOC) em V. O sensor da CPU vale mais que o da placa-mãe, que mede longe dela.</summary>
    public float? CpuVsoc { get; private set; }

    /// <summary>O nome varia: "SoC (SVI2 TFN)" na CPU, "CPU VSOC", "VSOC" ou "VDDCR_SOC" no Super I/O.</summary>
    internal static bool IsVsocSensor(string name) =>
        name.Contains("VSOC", StringComparison.OrdinalIgnoreCase) || name.Contains("VDDCR_SOC", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("SoC", StringComparison.OrdinalIgnoreCase);

    internal static bool IsVcoreSensor(string name) =>
        name.Contains("VCore", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Core (SVI", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Valores principais calculados uma vez por leitura. O overlay, as telas LCD, o RGB (30 vezes por segundo) e o painel
    /// os consultam o tempo todo; antes cada consulta varria as centenas de linhas.
    /// </summary>
    private void UpdateSummary()
    {
        float? cpuTemp = null, cpuTempMax = null, cpuLoad = null, cpuPower = null, gpuTemp = null, gpuCore = null, gpuLoad = null;
        float? vcoreCpu = null, vcoreBoard = null, vsocCpu = null, vsocBoard = null;
        foreach (var r in Rows)
        {
            if (r.Type == SensorType.Voltage && r.RawValue is > 0 and var volts)
            {
                bool onCpu = r.HardwareType == HardwareType.Cpu;
                if (onCpu || r.HardwareType == HardwareType.SuperIO)
                {
                    if (IsVsocSensor(r.Name)) { if (onCpu) vsocCpu ??= volts; else vsocBoard ??= volts; }
                    else if (IsVcoreSensor(r.Name)) { if (onCpu) vcoreCpu ??= volts; else vcoreBoard ??= volts; }
                }
            }
            if (r.HardwareType == HardwareType.Cpu)
            {
                if (r.Type == SensorType.Temperature && r.RawValue is > 0 and var t)
                {
                    if (cpuTemp == null && (r.Name.Contains("Package") || r.Name.Contains("Tctl") || r.Name.Contains("Tdie"))) cpuTemp = t;
                    if (cpuTempMax == null || t > cpuTempMax) cpuTempMax = t;
                }
                else if (cpuLoad == null && r.Type == SensorType.Load && r.Name == "CPU Total") cpuLoad = r.RawValue;
                else if (cpuPower == null && r.Type == SensorType.Power && r.Name.Contains("Package")) cpuPower = r.RawValue;
            }
            else if (r.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel)
            {
                if (r.Type == SensorType.Temperature && r.RawValue is > 0 and var t)
                {
                    gpuTemp ??= t;
                    if (gpuCore == null && r.Name.Contains("Core")) gpuCore = t;
                }
                else if (gpuLoad == null && r.Type == SensorType.Load && r.Name.Contains("Core")) gpuLoad = r.RawValue;
            }
        }
        CpuTemperature = cpuTemp ?? cpuTempMax;
        CpuLoad = cpuLoad ?? _nativeCpuLoad;
        CpuPower = cpuPower;
        CpuVcore = vcoreCpu ?? vcoreBoard;
        CpuVsoc = vsocCpu ?? vsocBoard;
        GpuTemperature = gpuTemp;
        GpuCoreTemperature = gpuCore ?? gpuTemp;
        GpuCoreLoad = gpuLoad;
        DiagnosticBanner = BuildBanner(IsPawnIoAvailable, _readFailures, DiagnoseCpuSensors() == CpuSensorProblem.ReadFailed, _cpuName, _gpuName, HvciOn.Value);
        Volatile.Write(ref _lastSample, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private long _lastSample;

    /// <summary>Há quanto tempo os valores de CPU e GPU foram lidos. Nunca lidos = TimeSpan.MaxValue: o overclock recusa amostra antiga.</summary>
    public TimeSpan SampleAge
    {
        get
        {
            long at = Volatile.Read(ref _lastSample);
            return at == 0 ? TimeSpan.MaxValue : System.Diagnostics.Stopwatch.GetElapsedTime(at);
        }
    }

    /// <summary>
    /// Driver PawnIO (necessário para temperatura/consumo da CPU via MSR/SMN).
    /// A biblioteca procura exatamente esta chave de desinstalação.
    /// </summary>
    public static bool IsPawnIOInstalled
    {
        get
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                                                                     Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key != null;
        }
    }

    public static bool IsElevated =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public enum CpuSensorProblem { None, NotElevated, PawnIOMissing, ReadFailed }

    /// <summary>Por que a temperatura/consumo da CPU não aparecem (o motivo mais provável primeiro).</summary>
    public CpuSensorProblem DiagnoseCpuSensors()
    {
        if (CpuTemperature != null) return CpuSensorProblem.None;
        if (!IsElevated) return CpuSensorProblem.NotElevated;
        if (!IsPawnIOInstalled) return CpuSensorProblem.PawnIOMissing;
        return CpuSensorProblem.ReadFailed;
    }

    public static string Explain(CpuSensorProblem p) => p switch
    {
        CpuSensorProblem.NotElevated =>
            "O tHg Optimization não está rodando como administrador, e o Windows só libera os sensores internos da CPU " +
            "nesse modo. Feche e abra de novo aceitando o pedido de administrador.",
        CpuSensorProblem.PawnIOMissing =>
            "Falta o driver PawnIO, que lê a temperatura e o consumo da CPU. Ele é gratuito, assinado e usado também " +
            "pelo LibreHardwareMonitor e pelo FanControl. Depois de instalar, feche e abra o tHg Optimization.",
        CpuSensorProblem.ReadFailed =>
            "O PawnIO está instalado, mas a leitura falhou. Se acabou de instalar, reinicie o PC. Se continuar, " +
            "confira se o antivírus não bloqueou o driver ou se a CPU é muito antiga/recente para a biblioteca de sensores.",
        _ => ""
    };

    private bool _disposed;

    /// <summary>Pode ser chamado mais de uma vez (pela janela ao sair e pelo contêiner de serviços ao encerrar).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Devolve o controle das fans à BIOS antes de fechar (nunca deixa uma fan presa numa rotação baixa).
        foreach (var c in _controls.Values) try { c.SetDefault(); } catch (Exception ex) { Infrastructure.AppLog.Warn("sensores", ex); } // fan que não voltou à BIOS merece registro
        lock (_hw)
        {
            if (_open) _computer.Close();
            _open = false;
        }
    }
}
