using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LibreHardwareMonitor.Hardware;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.UI.ViewModels;

/// <summary>Cor do valor. O pincel fica no modelo da linha: assim a cor acompanha a troca de tema sem reler nada aqui.</summary>
public enum SensorTone { Normal, Good, Warn, Bad, Faint }

/// <summary>Uma linha da página: só avisa a tela quando o valor bruto muda, e monta o texto só nesse caso.</summary>
public sealed class SensorItemViewModel : ObservableObject
{
    public const int HistorySize = 60;

    public string Id { get; init; } = "";
    public string Hardware { get; init; } = "";
    public string HardwareKind { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public SensorType Type { get; init; }
    public string CategoryGlyph { get; init; } = "";
    /// <summary>Texto já sem acentos e em minúsculas, para a busca não refazer isso a cada letra digitada.</summary>
    internal string SearchText { get; init; } = "";

    public string Value { get; private set; } = "—";
    public string Min { get; private set; } = "—";
    public string Max { get; private set; } = "—";
    public double FillPercent { get; private set; }
    public Visibility BarVisibility { get; private set; } = Visibility.Collapsed;
    public SensorTone Tone { get; private set; } = SensorTone.Normal;
    public PointCollection? SparklinePoints { get; private set; }
    public bool IsPinned { get; private set; }
    public bool IsUnavailable { get; private set; }
    public string PinGlyph => IsPinned ? "" : "";
    public string PinLabel => IsPinned ? $"Desafixar {Name}" : $"Fixar {Name}";

    private readonly double[] _history = new double[HistorySize];
    private int _historyHead, _historyCount;
    private float? _rawValue, _rawMin, _rawMax;
    private bool _everUpdated;

    /// <summary>Devolve true se a disponibilidade mudou: quem filtra por "indisponíveis" precisa reavaliar a lista.</summary>
    public bool Update(SensorRow row)
    {
        bool availabilityChanged = IsUnavailable != row.IsUnavailable;
        if (IsPinned && row.RawValue is float raw)
        {
            AddHistory(raw);
            RebuildSparkline();
        }
        if (_everUpdated && !availabilityChanged && _rawValue == row.RawValue && _rawMin == row.RawMin && _rawMax == row.RawMax) return false;

        bool valueChanged = !_everUpdated || availabilityChanged || _rawValue != row.RawValue;
        bool minChanged = !_everUpdated || _rawMin != row.RawMin, maxChanged = !_everUpdated || _rawMax != row.RawMax;
        _everUpdated = true;
        _rawValue = row.RawValue; _rawMin = row.RawMin; _rawMax = row.RawMax;
        IsUnavailable = row.IsUnavailable;

        if (minChanged) { Min = row.Min; OnPropertyChanged(nameof(Min)); }
        if (maxChanged) { Max = row.Max; OnPropertyChanged(nameof(Max)); }
        if (!valueChanged) return availabilityChanged;

        Value = row.Value;
        OnPropertyChanged(nameof(Value));
        UpdateTone(row);
        UpdateBar(row);
        return availabilityChanged;
    }

    private void UpdateTone(SensorRow row)
    {
        var tone = row.IsUnavailable ? SensorTone.Faint
                 : Type == SensorType.Temperature && row.RawValue is float t ? (t < 60 ? SensorTone.Good : t < 80 ? SensorTone.Warn : SensorTone.Bad)
                 : SensorTone.Normal;
        if (tone == Tone) return;
        Tone = tone;
        OnPropertyChanged(nameof(Tone));
    }

    private void UpdateBar(SensorRow row)
    {
        bool hasBar = row.TryGetFillPercent(out double percent);
        var visibility = hasBar ? Visibility.Visible : Visibility.Collapsed;
        if (visibility != BarVisibility)
        {
            BarVisibility = visibility;
            OnPropertyChanged(nameof(BarVisibility));
        }
        if (percent == FillPercent) return;
        FillPercent = percent;
        OnPropertyChanged(nameof(FillPercent));
    }

    public void SetPinned(bool pinned)
    {
        if (IsPinned == pinned) return;
        IsPinned = pinned;
        if (!pinned)
        {
            _historyHead = _historyCount = 0;
            SparklinePoints = null;
            OnPropertyChanged(nameof(SparklinePoints));
        }
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinGlyph));
        OnPropertyChanged(nameof(PinLabel));
    }

    private void AddHistory(double value)
    {
        _history[_historyHead] = value;
        _historyHead = (_historyHead + 1) % HistorySize;
        if (_historyCount < HistorySize) _historyCount++;
    }

    /// <summary>
    /// De 0 a 100 entre o menor e o maior valor da janela; o Polyline estica para caber no cartão. Congelada: a tela não precisa acompanhar
    /// uma coleção que muda. Quatro cartões × 60 pontos × uma vez por segundo é um custo aceitável.
    /// </summary>
    private void RebuildSparkline()
    {
        if (_historyCount < 2) return;
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = 0; i < _historyCount; i++)
        {
            double v = _history[(_historyHead - _historyCount + i + HistorySize) % HistorySize];
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        double range = hi - lo;
        var points = new PointCollection(_historyCount);
        for (int i = 0; i < _historyCount; i++)
        {
            double v = _history[(_historyHead - _historyCount + i + HistorySize) % HistorySize];
            points.Add(new Point(i, range > 0 ? 100 - (v - lo) / range * 100 : 50));
        }
        points.Freeze();
        SparklinePoints = points;
        OnPropertyChanged(nameof(SparklinePoints));
    }
}

public sealed partial class SensorGroupViewModel : ObservableObject
{
    public string Hardware { get; init; } = "";
    public string HardwareKind { get; init; } = "";
    public string HardwareGlyph { get; init; } = "";
    /// <summary>Todas as linhas deste hardware. <see cref="Sensors"/> tem só as que passam pelos filtros.</summary>
    internal List<SensorItemViewModel> All { get; } = new();
    public ObservableCollection<SensorItemViewModel> Sensors { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _countLabel = "";
    [ObservableProperty] private Visibility _groupVisibility = Visibility.Visible;
}

/// <summary>
/// Meu PC → Sensores. Grupos e linhas nascem em <see cref="Rebuild"/> e vivem até a lista do monitor mudar; o tique só atualiza
/// valores, em <see cref="Refresh"/>. Roda na thread da janela, como as linhas do monitor.
/// </summary>
public sealed partial class SensorPageViewModel : ObservableObject
{
    public const string AllFilters = "Todos";

    private static readonly string[] KindOrder = { "CPU", "GPU", "Memória", "Placa-mãe", "Discos", "Rede", "Bateria", "Outros" };

    private readonly Dictionary<string, SensorItemViewModel> _byId = new();
    private readonly Dictionary<string, bool> _expandedBeforeSearch = new();
    private AppSettings? _settings;
    private bool _monitorOpen;
    private string? _failure;

    public ObservableCollection<SensorItemViewModel> Pinned { get; } = new();
    public ObservableCollection<SensorGroupViewModel> Groups { get; } = new();
    public IReadOnlyList<string> HardwareFilters { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> CategoryFilters { get; private set; } = Array.Empty<string>();

    public int TotalCount { get; private set; }
    public int VisibleCount { get; private set; }
    public DateTime LastUpdate { get; private set; }

    public string StatusLine => _failure ?? (TotalCount == 0
        ? (_monitorOpen ? SensorStatus.Empty : SensorStatus.Detecting)
        : $"{VisibleCount} de {TotalCount} sensores · atualizado {LastUpdate:HH:mm:ss}");

    public string AccessibilitySummary => $"{VisibleCount} sensores visíveis de {TotalCount}.";

    /// <summary>Vazio de verdade: o botão de diagnóstico só faz sentido aqui e quando a detecção falhou.</summary>
    public bool NeedsDiagnostic => _failure != null || (_monitorOpen && TotalCount == 0);

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string? _hardwareFilter;
    [ObservableProperty] private string? _categoryFilter;
    [ObservableProperty] private bool _onlyPinned;
    [ObservableProperty] private bool _onlyUnavailable;

    partial void OnSearchChanged(string? oldValue, string newValue)
    {
        bool wasSearching = !string.IsNullOrEmpty(oldValue), searching = !string.IsNullOrEmpty(newValue);
        if (searching && !wasSearching)
        {
            _expandedBeforeSearch.Clear();
            foreach (var g in Groups) _expandedBeforeSearch[g.Hardware] = g.IsExpanded;
        }
        ApplyFilters();
        // Procurando, o resultado aparece aberto; apagar a busca devolve o que o usuário tinha deixado aberto.
        foreach (var g in Groups)
        {
            if (searching) g.IsExpanded = true;
            else if (wasSearching && _expandedBeforeSearch.TryGetValue(g.Hardware, out bool was)) g.IsExpanded = was;
        }
    }

    partial void OnHardwareFilterChanged(string? value) => ApplyFilters();
    partial void OnCategoryFilterChanged(string? value) => ApplyFilters();
    partial void OnOnlyPinnedChanged(bool value) => ApplyFilters();
    partial void OnOnlyUnavailableChanged(bool value) => ApplyFilters();

    public void SetMonitorState(bool open, string? failure = null)
    {
        if (_monitorOpen == open && _failure == failure) return;
        _monitorOpen = open;
        _failure = failure;
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(NeedsDiagnostic));
    }

    /// <summary>Chamar na primeira leitura e quando o número de linhas do monitor muda. Mantém filtros, grupos abertos e favoritos.</summary>
    public void Rebuild(SensorMonitor monitor, AppSettings settings)
    {
        _settings = settings;
        var pinned = new HashSet<string>(settings.CopyList(s => s.PinnedSensors));
        var expanded = Groups.ToDictionary(g => g.Hardware, g => g.IsExpanded);
        var previous = new Dictionary<string, SensorItemViewModel>(_byId);

        _byId.Clear();
        Groups.Clear();
        Pinned.Clear();

        var groups = new Dictionary<string, SensorGroupViewModel>();
        var rows = monitor.Rows.OrderBy(r => r.Order).ToList(); // ordem estável: dentro do mesmo tipo fica a ordem da biblioteca
        foreach (var row in rows)
        {
            if (_byId.ContainsKey(row.Id)) continue;
            string kind = KindOf(row.HardwareType);
            if (!groups.TryGetValue(row.Hardware, out var group))
            {
                // Sem nada salvo, abre só CPU e GPU: com tudo aberto, a página nasceria com centenas de linhas.
                bool open = expanded.TryGetValue(row.Hardware, out bool was) ? was : kind is "CPU" or "GPU";
                groups[row.Hardware] = group = new SensorGroupViewModel { Hardware = row.Hardware, HardwareKind = kind, HardwareGlyph = KindGlyph(kind), IsExpanded = open };
            }
            // Uma linha que já existia mantém o histórico do gráfico.
            var item = previous.TryGetValue(row.Id, out var old) ? old : new SensorItemViewModel
            {
                Id = row.Id, Hardware = row.Hardware, HardwareKind = kind, Name = row.Name, Category = row.Category, Type = row.Type,
                CategoryGlyph = GlyphOf(row.Type),
                SearchText = SearchIndex.Fold($"{row.Name} {row.Hardware} {row.Category}"),
            };
            item.SetPinned(pinned.Contains(row.Id));
            item.Update(row);
            _byId[row.Id] = item;
            group.All.Add(item);
        }
        foreach (var g in groups.Values.OrderBy(g => Array.IndexOf(KindOrder, g.HardwareKind)).ThenBy(g => g.Hardware, StringComparer.CurrentCulture))
            Groups.Add(g);

        HardwareFilters = groups.Values.Select(g => g.HardwareKind).Distinct().OrderBy(k => Array.IndexOf(KindOrder, k)).ToList();
        CategoryFilters = _byId.Values.Select(i => i.Category).Distinct().OrderBy(c => c, StringComparer.CurrentCulture).ToList();
        OnPropertyChanged(nameof(HardwareFilters));
        OnPropertyChanged(nameof(CategoryFilters));

        TotalCount = monitor.Rows.Count;
        LastUpdate = DateTime.Now;
        RebuildPinned();
        ApplyFilters();
    }

    /// <summary>Chamar no tique. O(n): percorre as linhas do monitor e atualiza cada uma no lugar, sem recriar grupos.</summary>
    public void Refresh(SensorMonitor monitor)
    {
        bool reapply = OnlyUnavailable;
        foreach (var row in monitor.Rows)
        {
            if (!_byId.TryGetValue(row.Id, out var item))
            {
                // Mesma contagem com outra linha (uma leitura reserva da CPU saiu e outra entrou).
                if (_settings != null) Rebuild(monitor, _settings);
                return;
            }
            reapply |= item.Update(row);
        }
        LastUpdate = DateTime.Now;
        if (reapply) ApplyFilters();
        else OnPropertyChanged(nameof(StatusLine));
    }

    /// <summary>Fixa ou solta o sensor nas configurações. Quem chama grava o arquivo.</summary>
    public void TogglePin(string id, AppSettings settings)
    {
        _settings = settings;
        if (!_byId.TryGetValue(id, out var item)) return;
        bool pin = !item.IsPinned;
        settings.Mutate(() =>
        {
            if (pin) { if (!settings.PinnedSensors.Contains(id)) settings.PinnedSensors.Add(id); }
            else settings.PinnedSensors.Remove(id);
        });
        item.SetPinned(pin);
        RebuildPinned();
        if (OnlyPinned) ApplyFilters();
    }

    private void RebuildPinned()
    {
        Pinned.Clear();
        foreach (var item in _byId.Values.Where(i => i.IsPinned).OrderBy(i => i.Id, StringComparer.Ordinal)) Pinned.Add(item);
    }

    private bool Passes(SensorItemViewModel item, string foldedSearch) =>
        (HardwareFilter == null || item.HardwareKind == HardwareFilter)
        && (CategoryFilter == null || item.Category == CategoryFilter)
        && (!OnlyPinned || item.IsPinned)
        && (!OnlyUnavailable || item.IsUnavailable)
        && (foldedSearch.Length == 0 || item.SearchText.Contains(foldedSearch, StringComparison.Ordinal));

    /// <summary>Mexe na lista de um grupo só se o resultado mudou: recriar linhas é o que custa, e o filtro de indisponíveis reavalia a cada tique.</summary>
    private void ApplyFilters()
    {
        string folded = SearchIndex.Fold((Search ?? "").Trim());
        int visible = 0;
        foreach (var group in Groups)
        {
            var wanted = group.All.Where(i => Passes(i, folded)).ToList();
            if (!wanted.SequenceEqual(group.Sensors))
            {
                group.Sensors.Clear();
                foreach (var item in wanted) group.Sensors.Add(item);
            }
            visible += wanted.Count;
            group.CountLabel = wanted.Count == group.All.Count ? Count(group.All.Count) : $"{wanted.Count} de {group.All.Count}";
            group.GroupVisibility = wanted.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        VisibleCount = visible;
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(StatusLine));
        OnPropertyChanged(nameof(AccessibilitySummary));
        OnPropertyChanged(nameof(NeedsDiagnostic));
    }

    private static string Count(int n) => n == 1 ? "1 sensor" : $"{n} sensores";

    /// <summary>Discos e adaptadores de rede são vários num PC só: o filtro é por tipo de peça, senão viraria uma dúzia de chips.</summary>
    internal static string KindOf(HardwareType type) => type switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
        HardwareType.Memory => "Memória",
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "Placa-mãe",
        HardwareType.Storage => "Discos",
        HardwareType.Network => "Rede",
        HardwareType.Battery => "Bateria",
        _ => "Outros",
    };

    private static string KindGlyph(string kind) => kind switch
    {
        "CPU" => "",
        "GPU" => "",
        "Memória" => "",
        "Placa-mãe" => "",
        "Discos" => "",
        "Rede" => "",
        "Bateria" => "",
        _ => "",
    };

    internal static string GlyphOf(SensorType type) => type switch
    {
        SensorType.Temperature => "",
        SensorType.Load => "",
        SensorType.Clock => "",
        SensorType.Fan => "",
        SensorType.Power or SensorType.Current => "",
        SensorType.Voltage => "",
        SensorType.Level => "",
        SensorType.Control => "",
        SensorType.Data or SensorType.SmallData => "",
        SensorType.Throughput => "",
        _ => "",
    };
}
