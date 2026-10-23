using System.Collections.ObjectModel;
using LibreHardwareMonitor.Hardware;

namespace ThgOptimization;

/// <summary>Encapsula o LibreHardwareMonitorLib e mantém a lista de sensores atualizada.</summary>
public sealed class SensorMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly Dictionary<string, SensorRow> _index = new();
    private readonly Dictionary<string, FanOutput> _controls = new();

    /// <summary>Saídas de fan controláveis (placa-mãe), descobertas na primeira leitura completa.</summary>
    public IReadOnlyCollection<FanOutput> FanOutputs => _controls.Values;

    /// <summary>Temperatura principal da placa de vídeo (núcleo/edge), em °C.</summary>
    public float? GpuTemperature => Rows.FirstOrDefault(r => r.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel
                                                          && r.Type == SensorType.Temperature && r.RawValue is > 0)?.RawValue;

    /// <summary>Lê só a placa-mãe (rotação das fans) sem passar pelos discos: usado pela página de fans.</summary>
    public void RefreshMotherboard()
    {
        if (!_open) return;
        foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard)) Walk(hw);
    }
    private volatile bool _open;

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
        foreach (var hw in _computer.Hardware)
        {
            if (light && hw.HardwareType is not (HardwareType.Cpu or HardwareType.GpuNvidia or HardwareType.GpuAmd
                                                  or HardwareType.GpuIntel or HardwareType.Memory)) continue;
            Walk(hw);
        }
    }

    private void Walk(IHardware hw)
    {
        hw.Update();
        foreach (var s in hw.Sensors)
        {
            string id = s.Identifier.ToString();
            if (!_index.TryGetValue(id, out var row))
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
            row.Update(s.Value, s.Min, s.Max);
            // Saídas de fan da placa-mãe (Super I/O) que aceitam controle por software: guardadas para as curvas.
            if (s.SensorType == SensorType.Control && s.Control != null && hw.HardwareType == HardwareType.SuperIO && !_controls.ContainsKey(id))
                _controls[id] = new FanOutput(id, hw.Name, s.Name, s.Control, s, hw.Sensors.FirstOrDefault(f => f.SensorType == SensorType.Fan && f.Index == s.Index));
        }
        foreach (var sub in hw.SubHardware)
            Walk(sub);
    }

    /// <summary>Temperatura principal da CPU: Package (Intel) / Tctl-Tdie (AMD), senão a maior entre os núcleos.</summary>
    public float? CpuTemperature
    {
        get
        {
            var temps = Rows.Where(r => r.HardwareType == HardwareType.Cpu
                                     && r.Type == SensorType.Temperature
                                     && r.RawValue is > 0).ToList();
            var main = temps.FirstOrDefault(r => r.Name.Contains("Package") || r.Name.Contains("Tctl") || r.Name.Contains("Tdie"));
            return main?.RawValue ?? temps.Max(r => r.RawValue);
        }
    }

    /// <summary>Carga total da CPU em %.</summary>
    public float? CpuLoad => Rows.FirstOrDefault(r => r.HardwareType == HardwareType.Cpu
                                                   && r.Type == SensorType.Load
                                                   && r.Name == "CPU Total")?.RawValue;

    /// <summary>Consumo do pacote da CPU em W.</summary>
    public float? CpuPower => Rows.FirstOrDefault(r => r.HardwareType == HardwareType.Cpu
                                                    && r.Type == SensorType.Power
                                                    && r.Name.Contains("Package"))?.RawValue;

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

    public void Dispose()
    {
        // Devolve o controle das fans à BIOS antes de fechar (nunca deixa uma fan presa numa rotação baixa).
        foreach (var c in _controls.Values) try { c.SetDefault(); } catch { }
        if (_open) _computer.Close();
    }
}
