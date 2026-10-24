using System.Collections.ObjectModel;
using LibreHardwareMonitor.Hardware;

namespace ThgOptimization;

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

    /// <summary>Lê só a placa-mãe (rotação das fans) sem passar pelos discos: usado pela página de fans.</summary>
    public void RefreshMotherboard()
    {
        if (!_open) return;
        Apply(Poll(h => h.HardwareType == HardwareType.Motherboard));
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
    public Task OpenAsync() => Task.Run(() => { _computer.Open(); _open = true; });

    public void Refresh() => Refresh(light: false);

    /// <summary>
    /// light = só CPU, GPU e memória. Ler discos (S.M.A.R.T.), placa-mãe (Super I/O, controlador embarcado) e controladoras
    /// segura o barramento por um instante e é uma causa conhecida de microengasgos em jogo; no modo leve isso fica de fora.
    /// </summary>
    public void Refresh(bool light)
    {
        if (!_open) return;
        Apply(Poll(h => !light || IsLight(h)));
    }

    /// <summary>
    /// Mesmo que Refresh, mas a conversa com o hardware (a parte lenta: 20 a 200 ms com discos e placa-mãe) roda numa
    /// tarefa de fundo; só a atualização das linhas volta para a janela. Chamado sempre da janela. Com uma leitura já em
    /// andamento, espera por ela (e, se ela era a leve e esta pede tudo, lê de novo completa).
    /// </summary>
    public async Task RefreshAsync(bool light)
    {
        if (!_open) return;
        if (!_inflight.IsCompleted)
        {
            bool covered = light || !_inflightLight;
            try { await _inflight; } catch { }
            if (covered) return;
        }
        _inflightLight = light;
        var poll = Task.Run(() => Poll(h => !light || IsLight(h)));
        _inflight = poll;
        Apply(await poll);
    }

    private Task _inflight = Task.CompletedTask;
    private bool _inflightLight;

    private static bool IsLight(IHardware h) =>
        h.HardwareType is HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.Memory;

    private readonly record struct Reading(IHardware Hw, ISensor Sensor, float? Value, float? Min, float? Max);

    private List<Reading> Poll(Func<IHardware, bool> which)
    {
        var list = new List<Reading>(256);
        lock (_hw)
        {
            if (!_open) return list;
            foreach (var hw in _computer.Hardware)
                if (which(hw)) Collect(hw, list);
        }
        return list;
    }

    private static void Collect(IHardware hw, List<Reading> list)
    {
        hw.Update();
        foreach (var s in hw.Sensors) list.Add(new Reading(hw, s, s.Value, s.Min, s.Max));
        foreach (var sub in hw.SubHardware) Collect(sub, list);
    }

    private void Apply(List<Reading> readings)
    {
        foreach (var (hw, s, value, min, max) in readings)
        {
            // Saídas de fan (Control) seguem o caminho completo: lá elas entram na lista das curvas.
            if (s.SensorType != SensorType.Control && _bySensor.TryGetValue(s, out var row))
            {
                row.Update(value, min, max);
                continue;
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
            row.Update(value, min, max);
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
        UpdateSummary();
    }

    /// <summary>Temperatura principal da CPU: Package (Intel) / Tctl-Tdie (AMD), senão a maior entre os núcleos.</summary>
    public float? CpuTemperature { get; private set; }

    /// <summary>Carga total da CPU em %.</summary>
    public float? CpuLoad { get; private set; }

    /// <summary>Consumo do pacote da CPU em W.</summary>
    public float? CpuPower { get; private set; }

    /// <summary>
    /// Valores principais calculados uma vez por leitura. O overlay, as telas LCD, o RGB (30 vezes por segundo) e o painel
    /// os consultam o tempo todo; antes cada consulta varria as centenas de linhas.
    /// </summary>
    private void UpdateSummary()
    {
        float? cpuTemp = null, cpuTempMax = null, cpuLoad = null, cpuPower = null, gpuTemp = null, gpuCore = null, gpuLoad = null;
        foreach (var r in Rows)
        {
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
        CpuLoad = cpuLoad;
        CpuPower = cpuPower;
        GpuTemperature = gpuTemp;
        GpuCoreTemperature = gpuCore ?? gpuTemp;
        GpuCoreLoad = gpuLoad;
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
        foreach (var c in _controls.Values) try { c.SetDefault(); } catch { }
        lock (_hw)
        {
            if (_open) _computer.Close();
            _open = false;
        }
    }
}
