namespace ThgOptimization;

/// <summary>Item que pode aparecer no overlay. Group: "FPS", "GPU", "VRAM", "CPU", "RAM" ou "Outros".</summary>
public sealed record OverlayMetric(string Key, string Group, string Label, bool Default);

/// <summary>Linha do overlay já pronta para mostrar (ex.: "GPU" · "98%  65 °C  2790 MHz").</summary>
public sealed record OverlayLine(string Label, string Value);

/// <summary>Catálogo do que o overlay pode mostrar, na ordem em que aparece.</summary>
public static class OverlayMetrics
{
    public static readonly OverlayMetric[] All =
    {
        new("fps", "FPS", "FPS", true),
        new("frametime", "FPS", "Frametime (ms)", true),
        new("low1", "FPS", "1% low", true),
        new("graph", "FPS", "Gráfico de frametime", true),
        new("low01", "FPS", "0,1% low", false),
        new("p99", "FPS", "Frametime P99", false),
        new("p999", "FPS", "Frametime P99,9 (Detalhado)", false),
        new("stutters_min", "FPS", "Engasgos por minuto", false),

        new("gpu_load", "GPU", "Uso", true),
        new("gpu_temp", "GPU", "Temperatura", true),
        new("gpu_hotspot", "GPU", "Hotspot", false),
        new("gpu_clock", "GPU", "Clock do núcleo", true),
        new("gpu_memclock", "GPU", "Clock da memória", false),
        new("gpu_power", "GPU", "Consumo", true),
        new("gpu_volt", "GPU", "Tensão do núcleo", false),
        new("gpu_fan", "GPU", "Ventoinha", false),
        new("gpu_perfcap", "GPU", "Limitador (NVIDIA)", false),

        new("vram_used", "VRAM", "Usada (GB)", true),
        new("vram_pct", "VRAM", "Uso (%)", false),
        new("vram_temp", "VRAM", "Temperatura", false),

        new("cpu_load", "CPU", "Uso total", true),
        new("cpu_maxcore", "CPU", "Núcleo mais ocupado", false),
        new("cpu_temp", "CPU", "Temperatura", true),
        new("cpu_clock", "CPU", "Clock médio", true),
        new("cpu_clockmax", "CPU", "Clock máximo (núcleo)", false),
        new("cpu_power", "CPU", "Consumo", true),

        new("ram_used", "RAM", "Usada (GB)", true),
        new("ram_pct", "RAM", "Uso (%)", false),

        new("game", "Outros", "Nome do jogo", true),
        new("clock", "Outros", "Hora", false),
        new("mem_pressure", "Outros", "Pressão de memória (Detalhado)", false),
        new("present_mode", "Outros", "Modo de apresentação, da última coleta (Detalhado)", false),
        new("etw_state", "Outros", "Estado do ETW (Detalhado)", false),
    };

    public static HashSet<string> Defaults() => All.Where(m => m.Default).Select(m => m.Key).ToHashSet();

    private static readonly string[] LoadKeys = { "gpu_load", "gpu_temp", "vram_pct", "cpu_load", "cpu_maxcore", "cpu_temp", "ram_pct" };

    /// <summary>Algum item escolhido precisa da amostra de carga? Ela consulta o driver de vídeo: sem uso, a consulta nem acontece.</summary>
    public static bool NeedsLoadSample(IReadOnlySet<string> show)
    {
        foreach (string key in LoadKeys) if (show.Contains(key)) return true;
        return false;
    }

    public static bool NeedsVram(IReadOnlySet<string> show) => show.Contains("vram_used");
    public static bool NeedsMemoryStatus(IReadOnlySet<string> show) => show.Contains("ram_used") || show.Contains("mem_pressure");
}

public enum MonitorProfile { Economy, Balanced, Detailed }

/// <summary>
/// Quanto o overlay trabalha. O perfil muda só o ritmo da tela e o que ela calcula: a coleta dos frames é a mesma nos três, e o evento de frame
/// nunca faz conta pesada.
/// </summary>
public static class MonitorProfiles
{
    private static readonly HashSet<string> EconomyKeys = new() { "fps", "frametime", "low1", "low01", "stutters_min", "game", "clock" };
    private static readonly HashSet<string> DetailedOnly = new() { "p999", "mem_pressure", "present_mode", "etw_state" };

    public static MonitorProfile Parse(string? text) => text switch { "economy" => MonitorProfile.Economy, "detailed" => MonitorProfile.Detailed, _ => MonitorProfile.Balanced };

    public static string Key(MonitorProfile p) => p switch { MonitorProfile.Economy => "economy", MonitorProfile.Detailed => "detailed", _ => "balanced" };

    public static TimeSpan Interval(MonitorProfile p) => p switch
    {
        MonitorProfile.Economy => TimeSpan.FromMilliseconds(1000),
        MonitorProfile.Detailed => TimeSpan.FromMilliseconds(250),
        _ => TimeSpan.FromMilliseconds(500),
    };

    /// <summary>Sobre o que foi escolhido, o que este perfil realmente mostra. Nada é adicionado sem o usuário marcar.</summary>
    public static HashSet<string> Effective(IEnumerable<string> chosen, MonitorProfile p) => p switch
    {
        MonitorProfile.Economy => chosen.Where(EconomyKeys.Contains).ToHashSet(),
        MonitorProfile.Detailed => chosen.ToHashSet(),
        _ => chosen.Where(k => !DetailedOnly.Contains(k)).ToHashSet(),
    };
}
