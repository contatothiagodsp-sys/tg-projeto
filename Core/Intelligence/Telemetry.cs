namespace ThgOptimization.Core.Intelligence;

/// <summary>Situação de uma leitura. Campo ausente nunca vira zero: o motor precisa saber por que o dado não existe.</summary>
public enum ReadingState
{
    /// <summary>O hardware/API fornece e há leitura.</summary>
    Supported,
    /// <summary>Este hardware ou este Windows não oferece a medida.</summary>
    Unsupported,
    /// <summary>Poderia existir, mas não foi lida agora (sem permissão, driver ausente, sensor mudo).</summary>
    Unavailable,
    /// <summary>Há valor, mas velho demais para decidir com ele.</summary>
    Stale,
}

/// <summary>De onde veio um número mostrado ao usuário. Estimativa e medida nunca aparecem com o mesmo nome.</summary>
public enum MetricOrigin { Captured, Estimated, Unavailable }

/// <summary>As grandezas que o motor conhece. A cobertura de cada uma é medida por sessão.</summary>
public enum Metric
{
    Fps, FrameTime, CpuLoad, CpuMaxCoreLoad, CpuClock, CpuTemp, CpuPower,
    GpuLoad, GpuClock, GpuTemp, GpuPower, Vram, Ram, Commit, NetLatency,
}

/// <summary>Um segundo de sessão, independente da fonte (LibreHardwareMonitor, NVML, ADLX, ETW). Null = sem leitura naquele segundo.</summary>
public sealed record TelemetryPoint(double AtSeconds)
{
    public double? Fps { get; init; }
    /// <summary>Pior frame do segundo (ms), quando houve gravação de frames.</summary>
    public double? WorstFrameMs { get; init; }
    public int Stutters { get; init; }
    public double? CpuLoad { get; init; }
    /// <summary>Carga do núcleo mais ocupado: é o que revela a thread principal presa mesmo com a CPU "em 40%".</summary>
    public double? CpuMaxCoreLoad { get; init; }
    public double? CpuClockMhz { get; init; }
    public double? CpuTemp { get; init; }
    public double? CpuPowerW { get; init; }
    public double? GpuLoad { get; init; }
    public double? GpuClockMhz { get; init; }
    public double? GpuTemp { get; init; }
    public double? GpuPowerW { get; init; }
    public double? VramPct { get; init; }
    public double? RamPct { get; init; }
    /// <summary>Memória comprometida ÷ limite de commit, em %.</summary>
    public double? CommitPct { get; init; }
    /// <summary>Latência até a sonda de rede (ms). Null = sem sonda neste segundo.</summary>
    public double? NetLatencyMs { get; init; }
    /// <summary>true = a sonda foi enviada e não voltou. Null = sem sonda neste segundo.</summary>
    public bool? NetLost { get; init; }
    /// <summary>Evento WHEA/TDR registrado pelo Windows neste segundo.</summary>
    public bool HardwareError { get; init; }

    public double? Get(Metric m) => m switch
    {
        Metric.Fps => Fps, Metric.FrameTime => WorstFrameMs, Metric.CpuLoad => CpuLoad, Metric.CpuMaxCoreLoad => CpuMaxCoreLoad,
        Metric.CpuClock => CpuClockMhz, Metric.CpuTemp => CpuTemp, Metric.CpuPower => CpuPowerW, Metric.GpuLoad => GpuLoad,
        Metric.GpuClock => GpuClockMhz, Metric.GpuTemp => GpuTemp, Metric.GpuPower => GpuPowerW, Metric.Vram => VramPct,
        Metric.Ram => RamPct, Metric.Commit => CommitPct, Metric.NetLatency => NetLatencyMs, _ => null,
    };
}

/// <summary>
/// Em que condições a sessão foi medida. Duas sessões só são comparáveis (e só alimentam o mesmo baseline) com a mesma assinatura:
/// trocar driver, resolução ou hardware muda o comportamento esperado.
/// </summary>
public sealed record SessionContext(string Game, string GameKey, DateTime Start)
{
    public string? HardwareId { get; init; }
    public string? GpuDriver { get; init; }
    public string? Resolution { get; init; }
    public string? PowerPlan { get; init; }
    public bool? OnBattery { get; init; }
    /// <summary>Perfil de uso escolhido para o jogo (competitivo, equilibrado…): metas diferentes, baselines separados.</summary>
    public string Preset { get; init; } = "";

    /// <summary>Assinatura das condições. Campo desconhecido entra como "?": não impede comparar, mas reduz a confiança (ver ContextMatch).</summary>
    public string Signature => string.Join("|", HardwareId ?? "?", GpuDriver ?? "?", Resolution ?? "?", OnBattery == true ? "bat" : "ac");

    /// <summary>O que difere entre dois contextos, em palavras. Vazio = comparáveis.</summary>
    public IReadOnlyList<string> DifferencesFrom(SessionContext other)
    {
        var d = new List<string>();
        if (Differs(HardwareId, other.HardwareId)) d.Add("hardware");
        if (Differs(GpuDriver, other.GpuDriver)) d.Add("driver de vídeo");
        if (Differs(Resolution, other.Resolution)) d.Add("resolução");
        if (OnBattery != null && other.OnBattery != null && OnBattery != other.OnBattery) d.Add("alimentação (tomada/bateria)");
        return d;
    }

    private static bool Differs(string? a, string? b) => a != null && b != null && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Programa de fundo que segurou a CPU durante a partida (medido pelo balanceador do Modo Jogo).</summary>
public sealed record BackgroundHog(string Name, int Strikes);

/// <summary>Evidência medida fora da linha do tempo da sessão. Cada uma traz a própria data: medida antiga é tratada como velha.</summary>
public sealed record ExternalEvidence
{
    /// <summary>Maior DPC medido no teste de engasgos (µs) e quando.</summary>
    public double? DpcMaxMicroseconds { get; init; }
    public DateTime? DpcMeasuredAt { get; init; }
    public string? DpcWorstDriver { get; init; }
    /// <summary>O jogo está instalado em disco mecânico. Null = não foi possível saber.</summary>
    public bool? GameOnHdd { get; init; }
    public IReadOnlyList<BackgroundHog> BackgroundHogs { get; init; } = Array.Empty<BackgroundHog>();
    /// <summary>Taxa de atualização do monitor (Hz), para reconhecer trava de FPS/V-Sync.</summary>
    public int? RefreshHz { get; init; }
    /// <summary>Limite térmico informado pelo fabricante, quando o sensor expõe (TjMax, limite da GPU).</summary>
    public double? CpuTempLimit { get; init; }
    public double? GpuTempLimit { get; init; }
}

/// <summary>Uma sessão pronta para o diagnóstico.</summary>
public sealed class SessionTelemetry
{
    public SessionContext Context { get; }
    public IReadOnlyList<TelemetryPoint> Points { get; }
    /// <summary>Duração de cada frame (ms). Vazio = não houve gravação de frames; os percentis de frametime ficam indisponíveis.</summary>
    public IReadOnlyList<float> FrameTimesMs { get; }
    public ExternalEvidence External { get; }

    public SessionTelemetry(SessionContext context, IReadOnlyList<TelemetryPoint> points, IReadOnlyList<float>? frameTimesMs = null, ExternalEvidence? external = null)
    {
        Context = context;
        Points = points;
        FrameTimesMs = frameTimesMs ?? Array.Empty<float>();
        External = external ?? new ExternalEvidence();
    }

    public double DurationSeconds => Points.Count == 0 ? 0 : Points[^1].AtSeconds - Points[0].AtSeconds + 1;
}

/// <summary>Quanto dá para confiar nos dados de uma sessão, antes de qualquer diagnóstico.</summary>
public sealed record TelemetryQuality(double Seconds, int Frames, IReadOnlyDictionary<Metric, double> Coverage)
{
    /// <summary>Menos que isto não sustenta diagnóstico nenhum: é carregamento, menu ou jogo aberto por engano.</summary>
    public const double MinSeconds = 120;
    /// <summary>A partir daqui a duração deixa de limitar a confiança.</summary>
    public const double FullSeconds = 600;
    /// <summary>1% low precisa de pelo menos 10 frames na cauda (1000 frames); 0,1% low, de 10 mil.</summary>
    public const int MinFramesForLow1 = 1000, MinFramesForLow01 = 10_000;
    /// <summary>Uma grandeza conta como "medida na sessão" com pelo menos metade dos segundos cobertos.</summary>
    public const double MinCoverage = 0.5;

    public bool HasFrameCapture => Frames > 0;
    public bool SupportsLow1 => Frames >= MinFramesForLow1;
    public bool SupportsLow01 => Frames >= MinFramesForLow01;
    public bool EnoughTime => Seconds >= MinSeconds;

    public double CoverageOf(Metric m) => Coverage.GetValueOrDefault(m);
    public bool Has(Metric m) => CoverageOf(m) >= MinCoverage;

    /// <summary>Fator 0..1 pela duração: 2 min vale pouco, 10 min ou mais vale tudo.</summary>
    public double DurationFactor => Math.Clamp((Seconds - MinSeconds / 2) / (FullSeconds - MinSeconds / 2), 0, 1);

    public static TelemetryQuality Of(SessionTelemetry s)
    {
        var coverage = new Dictionary<Metric, double>();
        int n = s.Points.Count;
        foreach (Metric m in Enum.GetValues<Metric>())
            coverage[m] = n == 0 ? 0 : s.Points.Count(p => p.Get(m) != null) / (double)n;
        return new TelemetryQuality(s.DurationSeconds, s.FrameTimesMs.Count, coverage);
    }

    /// <summary>As grandezas sem leitura suficiente, em palavras, para o relatório declarar a limitação.</summary>
    public IReadOnlyList<string> Missing()
    {
        var names = new List<string>();
        void Check(Metric m, string name) { if (!Has(m)) names.Add(name); }
        Check(Metric.Fps, "FPS");
        if (!HasFrameCapture) names.Add("frametime por frame");
        Check(Metric.GpuLoad, "uso da GPU");
        Check(Metric.CpuLoad, "uso da CPU");
        Check(Metric.CpuMaxCoreLoad, "uso por núcleo");
        Check(Metric.CpuTemp, "temperatura da CPU");
        Check(Metric.GpuTemp, "temperatura da GPU");
        Check(Metric.CpuClock, "clock da CPU");
        Check(Metric.GpuClock, "clock da GPU");
        Check(Metric.Vram, "memória de vídeo");
        Check(Metric.Ram, "memória RAM");
        return names;
    }
}
