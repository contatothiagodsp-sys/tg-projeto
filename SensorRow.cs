using System.ComponentModel;
using LibreHardwareMonitor.Hardware;

namespace ThgOptimization;

/// <summary>Linha do painel de sensores, com notificação de mudança para a UI.</summary>
public class SensorRow : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Hardware { get; init; } = "";
    public string Category { get; init; } = "";
    public string Name { get; init; } = "";
    public SensorType Type { get; init; }
    public HardwareType HardwareType { get; init; }

    private float? _value, _min, _max;
    public float? RawValue => _value;

    /// <summary>Ordem de exibição por tipo (clocks e temperaturas primeiro, como no GPU-Z).</summary>
    public int Order => Type switch
    {
        SensorType.Clock => 0, SensorType.Temperature => 1, SensorType.Load => 2, SensorType.Fan => 3,
        SensorType.Control => 4, SensorType.Power => 5, SensorType.Voltage => 6, SensorType.Current => 7,
        SensorType.SmallData => 8, SensorType.Data => 9, SensorType.Throughput => 10, _ => 11
    };
    public const string UnavailableText = "Indisponível";
    private bool _unavailable;
    /// <summary>
    /// Na LibreHardwareMonitor 0.9.6, Level de disco e bateria já chega em 0 a 100 (SSD: Life 91, Percentage Used 2). Multiplicar faria 1 % de desgaste
    /// virar 100 %. Noutro hardware a escala não é garantida: valor acima de 1,5 prova que já é porcentagem; até lá, vale a razão 0 a 1.
    /// </summary>
    private bool _levelIsPercent;
    public string Value => _unavailable ? UnavailableText : Format(_value);
    public string Min => Format(_min);
    public string Max => Format(_max);

    private static readonly PropertyChangedEventArgs ValueArgs = new(nameof(Value)), MinArgs = new(nameof(Min)), MaxArgs = new(nameof(Max));

    /// <summary>Só avisa a tela do que mudou: centenas de sensores por segundo, a maioria parada entre leituras.</summary>
    /// <param name="unavailable">O sensor existe mas não entregou leitura válida: a linha diz isso em vez de sumir ou mostrar um valor velho.</param>
    public void Update(float? value, float? min, float? max, bool unavailable = false)
    {
        bool v = _value != value || _unavailable != unavailable, lo = _min != min, hi = _max != max;
        _value = value; _min = min; _max = max; _unavailable = unavailable;
        if (Type == SensorType.Level && !_levelIsPercent)
            _levelIsPercent = HardwareType is HardwareType.Storage or HardwareType.Battery || value > 1.5f || max > 1.5f;
        if (PropertyChanged is not { } changed) return;
        if (v) changed(this, ValueArgs);
        if (lo) changed(this, MinArgs);
        if (hi) changed(this, MaxArgs);
    }

    private string Format(float? v)
    {
        if (v == null) return "—";
        return Type switch
        {
            SensorType.Temperature => $"{v:0.#} °C",
            SensorType.Load or SensorType.Control or SensorType.Humidity => $"{v:0.#} %",
            SensorType.Level => $"{(_levelIsPercent ? v : v * 100f):0.#} %",
            SensorType.Clock => $"{v:0} MHz",
            SensorType.Power => $"{v:0.#} W",
            SensorType.Voltage => $"{v:0.###} V",
            SensorType.Current => $"{v:0.###} A",
            SensorType.Fan => $"{v:0} RPM",
            SensorType.Flow => $"{v:0.#} L/h",
            SensorType.Frequency => $"{v:0.#} Hz",
            SensorType.Data => $"{v:0.##} GB",
            SensorType.SmallData => $"{v:0.#} MB",
            SensorType.Throughput => FormatThroughput(v.Value),
            SensorType.Energy => $"{v:0.#} mWh",
            SensorType.Timing => $"{v:0.###} ns",
            SensorType.Factor => $"{v:0.##} ×",
            SensorType.TimeSpan => $"{v:0} s",
            SensorType.Noise => $"{v:0.#} dBA",
            SensorType.Conductivity => $"{v:0.#} µS/cm",
            _ => $"{v:0.##}"
        };
    }

    /// <summary>Rede em Mbit/s, como o provedor anuncia; disco em bytes. Os sensores de rede se chamam "Upload Speed" e "Download Speed": quem diz que é rede é o hardware.</summary>
    private string FormatThroughput(float bytesPerSec)
    {
        if (HardwareType == HardwareType.Network || Name.Contains("Network", StringComparison.OrdinalIgnoreCase))
            return $"{bytesPerSec * 8 / 1e6:0.##} Mbit/s";
        return bytesPerSec >= 1 << 20 ? $"{bytesPerSec / 1048576f:0.##} MB/s"
             : bytesPerSec >= 1 << 10 ? $"{bytesPerSec / 1024f:0.#} KB/s"
             : $"{bytesPerSec:0} B/s";
    }

    public static string CategoryName(SensorType t) => t switch
    {
        SensorType.Temperature => "Temperatura",
        SensorType.Load => "Carga",
        SensorType.Clock => "Clock",
        SensorType.Power => "Consumo",
        SensorType.Voltage => "Tensão",
        SensorType.Current => "Corrente",
        SensorType.Fan => "Ventoinhas",
        SensorType.Control => "Controle",
        SensorType.Level => "Nível",
        SensorType.Data => "Dados (GB)",
        SensorType.SmallData => "Dados (MB)",
        SensorType.Timing => "Temporização",
        SensorType.TimeSpan => "Tempo",
        SensorType.Noise => "Ruído",
        SensorType.Conductivity => "Condutividade",
        SensorType.Throughput => "Taxa",
        SensorType.Frequency => "Frequência",
        SensorType.Energy => "Energia",
        SensorType.Flow => "Fluxo",
        SensorType.Humidity => "Umidade",
        SensorType.Factor => "Fator",
        _ => t.ToString()
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>O que a página de sensores diz sobre a leitura em si: detectando, vazia ou atualizada.</summary>
public static class SensorStatus
{
    public const string Detecting = "Detectando o hardware… (pode levar 5 s)";
    public const string Empty = "Nenhum sensor encontrado. Clique em RODAR DIAGNÓSTICO.";

    public static string Describe(bool isOpen, int rows, DateTime now) =>
        rows > 0 ? $"{rows} sensores · atualizado {now:HH:mm:ss}" : isOpen ? Empty : Detecting;

    public static string DiagnosticText(string pawnInstalled, string pawnActive, string diagnosis, string libraryVersion) =>
        $"PawnIO instalado:  {pawnInstalled}\nPawnIO ativo:  {pawnActive}\nDiagnóstico:  {diagnosis}\nLibreHardwareMonitor:  {libraryVersion}";
}
