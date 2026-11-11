using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ThgOptimization.Core.Benchmark;

/// <summary>Uma amostra por segundo. Null = sem leitura naquele segundo, nunca zero inventado. FrametimeMs é o pior frame do segundo, quando há gravação de frames.</summary>
public sealed record SessionSample(
    long TimestampMs,
    double? FrametimeMs, double? Fps,
    double? CpuTemp, double? CpuLoad, double? CpuClock, double? CpuPower,
    double? GpuTemp, double? GpuLoad, double? GpuClock, double? GpuPower,
    double? VramPct, double? RamPct,
    int? FanCpuRpm, bool Throttled, int StuttersThisSecond, bool WheaError);

/// <summary>Primeira linha do arquivo: a lista de sessões lê só isto, sem abrir as amostras.</summary>
public sealed record SessionHeader(int Version, string Game, DateTime Start, double DurationSeconds, double AvgFps, double Low1Fps, int Stutters)
{
    public string Duration => DurationSeconds >= 3600 ? $"{DurationSeconds / 3600:0.#} h" : DurationSeconds >= 60 ? $"{DurationSeconds / 60:0} min" : $"{DurationSeconds:0} s";
    public string Line => $"{Start:dd/MM HH:mm} · {Duration} · {AvgFps:0} FPS · 1% low {Low1Fps:0} · {Stutters} engasgo(s)";
}

public sealed record SessionFrameInfo(double AtSeconds, double FrameMs, double? CpuTemp, double? GpuTemp, double? CpuClock, double? GpuClock)
{
    public string Line => $"{TimeSpan.FromSeconds(AtSeconds):hh\\:mm\\:ss} · {FrameMs:0.0} ms · CPU {Number(CpuTemp, "°C")} {Number(CpuClock, "MHz")} · GPU {Number(GpuTemp, "°C")} {Number(GpuClock, "MHz")}";
    private static string Number(double? v, string unit) => v is double d ? $"{d:0} {unit}" : "—";
}

public sealed record SessionPercentiles(double P50, double P95, double P99, double P999);

/// <summary>Uma sessão carregada. FramesMs: duração de cada frame, se a gravação existia; senão, vazio e as contas saem das amostras.</summary>
public sealed class SessionTimeline
{
    public SessionHeader Header { get; }
    public IReadOnlyList<SessionSample> Samples { get; }
    public IReadOnlyList<float> FramesMs { get; }
    public string? FilePath { get; init; }

    public SessionTimeline(SessionHeader header, IReadOnlyList<SessionSample> samples, IReadOnlyList<float>? frames = null)
    {
        Header = header;
        Samples = samples;
        FramesMs = frames ?? Array.Empty<float>();
    }

    public long DurationMs => Samples.Count > 0 ? Samples[^1].TimestampMs + 1000 : 0;

    public SessionPercentiles Percentiles()
    {
        var values = FramesMs.Count > 0 ? FramesMs.Select(f => (double)f).ToArray() : Samples.Where(s => s.FrametimeMs != null).Select(s => s.FrametimeMs!.Value).ToArray();
        Array.Sort(values);
        return new SessionPercentiles(FrameStatistics.Percentile(values, 0.5), FrameStatistics.Percentile(values, 0.95), FrameStatistics.Percentile(values, 0.99), FrameStatistics.Percentile(values, 0.999));
    }

    /// <summary>Os piores frames, com o que os sensores diziam naquele segundo: é isto que responde "o que aconteceu quando travou?".</summary>
    public IReadOnlyList<SessionFrameInfo> WorstFrames(int count = 5)
    {
        if (FramesMs.Count > 0)
        {
            double at = 0;
            var all = new (double At, float Ms)[FramesMs.Count];
            for (int i = 0; i < all.Length; i++)
            {
                all[i] = (at, FramesMs[i]);
                at += FramesMs[i] / 1000.0;
            }
            return all.OrderByDescending(f => f.Ms).Take(count).Select(f => Info(f.At, f.Ms)).ToList();
        }
        return Samples.Where(s => s.FrametimeMs != null).OrderByDescending(s => s.FrametimeMs).Take(count)
                      .Select(s => Info(s.TimestampMs / 1000.0, s.FrametimeMs!.Value)).ToList();
    }

    private SessionFrameInfo Info(double atSeconds, double ms)
    {
        var sample = SampleAt((long)(atSeconds * 1000));
        return new SessionFrameInfo(atSeconds, ms, sample?.CpuTemp, sample?.GpuTemp, sample?.CpuClock, sample?.GpuClock);
    }

    /// <summary>Amostra que cobre o instante (busca binária: é chamada a cada movimento do mouse no gráfico).</summary>
    public SessionSample? SampleAt(long ms)
    {
        if (Samples.Count == 0) return null;
        int lo = 0, hi = Samples.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Samples[mid].TimestampMs <= ms) lo = mid;
            else hi = mid - 1;
        }
        return Samples[lo];
    }

    /// <summary>
    /// Subconjunto do CSV do PresentMon: Application, TimeInSeconds, msBetweenPresents, que o CapFrameX importa. Com a gravação de frames, uma linha por frame.
    /// Sem ela, uma por segundo, com o pior frame do segundo, porque é o único dado que existe.
    /// </summary>
    public string ToPresentMonCsv()
    {
        var sb = new StringBuilder("Application,TimeInSeconds,msBetweenPresents\r\n");
        string app = CsvSafe(Header.Game);
        var culture = CultureInfo.InvariantCulture;
        if (FramesMs.Count > 0)
        {
            double at = 0;
            foreach (float frame in FramesMs)
            {
                at += frame / 1000.0;
                sb.Append(app).Append(',').Append(at.ToString("0.000", culture)).Append(',').Append(frame.ToString("0.000", culture)).Append("\r\n");
            }
            return sb.ToString();
        }
        foreach (var s in Samples.Where(s => s.FrametimeMs != null))
            sb.Append(app).Append(',').Append((s.TimestampMs / 1000.0).ToString("0.000", culture)).Append(',').Append(s.FrametimeMs!.Value.ToString("0.000", culture)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>O nome vem do jogo: sem vírgula nem quebra de linha, e sem começar com o que uma planilha executaria como fórmula.</summary>
    internal static string CsvSafe(string name)
    {
        var clean = new string(name.Where(c => c != ',' && c != '"' && !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 0 && "=+-@".Contains(clean[0]) ? "'" + clean : clean;
    }
}

/// <summary>Pasta das sessões: um .jsonl por sessão e, ao lado, os frames compactados. Guarda só as mais recentes.</summary>
public sealed class SessionStore
{
    public const int MaxSessions = 50;
    private const long MaxFileBytes = 64L * 1024 * 1024;
    private const string FramesSuffix = ".frames.gz";

    public string Folder { get; }

    public SessionStore(string folder) => Folder = folder;

    /// <summary>Na pasta protegida do usuário, como as configurações.</summary>
    public static SessionStore Default => new(Path.Combine(AppSettings.Folder, "Sessions"));

    internal static string Slug(string game)
    {
        var sb = new StringBuilder();
        foreach (char c in Infrastructure.SearchIndex.Fold(game))
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        string slug = sb.ToString().Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');
        return slug.Length > 0 ? slug : "jogo";
    }

    public string Save(SessionTimeline session, IReadOnlyList<float>? frames)
    {
        Directory.CreateDirectory(Folder);
        string path = Path.Combine(Folder, $"{session.Header.Start:yyyyMMdd-HHmmss}-{Slug(session.Header.Game)}.jsonl");
        string tmp = path + ".tmp";
        using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
        {
            writer.WriteLine(JsonSerializer.Serialize(session.Header));
            foreach (var sample in session.Samples) writer.WriteLine(JsonSerializer.Serialize(sample));
        }
        File.Move(tmp, path, overwrite: true);
        if (frames is { Count: > 0 })
        {
            using var file = File.Create(path + FramesSuffix);
            using var gz = new GZipStream(file, CompressionLevel.Fastest);
            gz.Write(MemoryMarshal.AsBytes(frames.ToArray().AsSpan()));
        }
        Prune();
        return path;
    }

    public IEnumerable<string> Files() =>
        Directory.Exists(Folder) ? Directory.EnumerateFiles(Folder, "*.jsonl").OrderByDescending(Path.GetFileName, StringComparer.Ordinal) : Enumerable.Empty<string>();

    /// <summary>Apaga as mais antigas. O nome começa com a data, então a ordem do nome é a da data.</summary>
    public void Prune(int keep = MaxSessions)
    {
        foreach (string old in Files().Skip(keep))
        {
            try
            {
                File.Delete(old);
                File.Delete(old + FramesSuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Infrastructure.AppLog.Warn("sessões", ex); }
        }
    }

    /// <summary>Cabeçalhos, da mais recente para a mais antiga. Um arquivo ruim fica de fora da lista sem esconder os outros.</summary>
    public List<(string Path, SessionHeader Header)> List()
    {
        var list = new List<(string, SessionHeader)>();
        foreach (string file in Files())
        {
            var header = ReadHeader(file);
            if (header != null) list.Add((file, header));
        }
        return list;
    }

    private static SessionHeader? ReadHeader(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            return reader.ReadLine() is { } line ? ValidHeader(JsonSerializer.Deserialize<SessionHeader>(line)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Infrastructure.AppLog.Warn("sessões", ex);
            return null;
        }
    }

    private static SessionHeader? ValidHeader(SessionHeader? h) => h is { Version: > 0, Game: not null } ? h : null;

    /// <summary>Arquivo corrompido ou grande demais devolve null: quem chama mostra "não foi possível abrir".</summary>
    public static SessionTimeline? Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            using var reader = new StreamReader(path, Encoding.UTF8);
            if (reader.ReadLine() is not { } first || ValidHeader(JsonSerializer.Deserialize<SessionHeader>(first)) is not { } header) return null;
            var samples = new List<SessionSample>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                if (JsonSerializer.Deserialize<SessionSample>(line) is not { } sample) return null;
                samples.Add(sample);
            }
            return new SessionTimeline(header, samples, LoadFrames(path)) { FilePath = path };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Infrastructure.AppLog.Warn("sessões", ex);
            return null;
        }
    }

    /// <summary>Sem o arquivo de frames, ou com ele ruim, a sessão continua abrindo pelas amostras.</summary>
    private static float[]? LoadFrames(string path)
    {
        string framesPath = path + FramesSuffix;
        if (!File.Exists(framesPath)) return null;
        try
        {
            using var file = File.OpenRead(framesPath);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            gz.CopyTo(buffer);
            return MemoryMarshal.Cast<byte, float>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)).ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Infrastructure.AppLog.Warn("sessões", ex);
            return null;
        }
    }
}

/// <summary>
/// Grava uma sessão de jogo. A amostra vai para a memória, e o disco só é tocado no fim: nada de escrita durante o jogo. O limite cobre 3 h, como a gravação de frames.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    public const int MaxSamples = 3 * 3600;

    private readonly SessionStore _store;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly List<SessionSample> _samples = new();
    private string _game = "";
    private DateTime _start;
    private bool _recording;

    public SessionRecorder(SessionStore store, Func<DateTime>? now = null)
    {
        _store = store;
        _now = now ?? (() => DateTime.Now);
    }

    public bool IsRecording { get { lock (_gate) return _recording; } }
    public SessionTimeline? LastSession { get; private set; }

    public void Begin(string game)
    {
        lock (_gate)
        {
            _samples.Clear();
            _game = game;
            _start = _now();
            _recording = true;
        }
    }

    /// <summary>Lê os resumos do monitor e as linhas de sensor: chamar da thread da janela, que é a dona das linhas.</summary>
    public void Sample(SensorMonitor monitor, FrameStats? frames, int stuttersThisSecond, bool throttled, bool wheaError = false)
    {
        var (cpuClock, gpuClock, gpuPower, vramPct, ramPct) = ReadRows(monitor);
        Add(frames?.FrameTimeMs, frames?.Fps, monitor.CpuTemperature, monitor.CpuLoad, cpuClock, monitor.CpuPower,
            monitor.GpuTemperature, monitor.GpuCoreLoad, gpuClock, gpuPower, vramPct, ramPct, stuttersThisSecond, throttled, wheaError);
    }

    internal void Add(double? frametime, double? fps, double? cpuTemp, double? cpuLoad, double? cpuClock, double? cpuPower,
                      double? gpuTemp, double? gpuLoad, double? gpuClock, double? gpuPower, double? vramPct, double? ramPct,
                      int stutters, bool throttled, bool wheaError)
    {
        lock (_gate)
        {
            if (!_recording || _samples.Count >= MaxSamples) return;
            long at = (long)(_now() - _start).TotalMilliseconds;
            // A placa-mãe fica de fora das leituras durante o jogo, para não segurar o barramento: o RPM não seria deste segundo, então não é gravado.
            _samples.Add(new SessionSample(at, frametime, fps, cpuTemp, cpuLoad, cpuClock, cpuPower, gpuTemp, gpuLoad, gpuClock, gpuPower, vramPct, ramPct,
                                           FanCpuRpm: null, throttled, stutters, wheaError));
        }
    }

    private static (double? CpuClock, double? GpuClock, double? GpuPower, double? VramPct, double? RamPct) ReadRows(SensorMonitor monitor)
    {
        double? cpuClock = null, cpuAverage = null, gpuClock = null, gpuPower = null, ram = null;
        double? vramUsed = null, vramTotal = null;
        foreach (var row in monitor.Rows)
        {
            if (row.RawValue is not float value) continue;
            switch (row.HardwareType)
            {
                case LibreHardwareMonitor.Hardware.HardwareType.Cpu when row.Type == LibreHardwareMonitor.Hardware.SensorType.Clock:
                    if (row.Name.Contains("Average", StringComparison.OrdinalIgnoreCase) || row.Name.Contains("Médio", StringComparison.OrdinalIgnoreCase)) cpuAverage ??= value;
                    else if (row.Name.StartsWith("Core #", StringComparison.Ordinal) && !row.Name.Contains("Effective", StringComparison.Ordinal)) cpuClock = Math.Max(cpuClock ?? 0, value);
                    break;
                case LibreHardwareMonitor.Hardware.HardwareType.GpuAmd or LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia or LibreHardwareMonitor.Hardware.HardwareType.GpuIntel:
                    if (row.Type == LibreHardwareMonitor.Hardware.SensorType.Clock && row.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)) gpuClock ??= value;
                    else if (row.Type == LibreHardwareMonitor.Hardware.SensorType.Power) gpuPower ??= value;
                    else if (row.Type == LibreHardwareMonitor.Hardware.SensorType.SmallData && row.Name == "GPU Memory Used") vramUsed = value;
                    else if (row.Type == LibreHardwareMonitor.Hardware.SensorType.SmallData && row.Name == "GPU Memory Total") vramTotal = value;
                    break;
                case LibreHardwareMonitor.Hardware.HardwareType.Memory when row.Type == LibreHardwareMonitor.Hardware.SensorType.Load && row.Name == "Memory":
                    ram ??= value;
                    break;
            }
        }
        return (cpuAverage ?? cpuClock, gpuClock, gpuPower, vramUsed / vramTotal * 100, ram);
    }

    /// <summary>
    /// Termina e grava. frameInstants: instante de cada frame em ms, da gravação do Modo Jogo. Sem nenhuma amostra, não grava nada e devolve null: uma sessão vazia não é história.
    /// </summary>
    public SessionTimeline? End(IReadOnlyList<double>? frameInstants = null, double avgFps = 0, double low1Fps = 0, int stutters = 0)
    {
        List<SessionSample> samples;
        string game;
        DateTime start;
        lock (_gate)
        {
            if (!_recording) return null;
            _recording = false;
            samples = new List<SessionSample>(_samples);
            _samples.Clear();
            game = _game;
            start = _start;
        }
        if (samples.Count == 0) return null;

        float[]? frames = frameInstants is { Count: > 1 } ? ToDurations(frameInstants) : null;
        if (frames != null) samples = MergeFrames(samples, frames);
        double avg = avgFps > 0 ? avgFps : samples.Where(s => s.Fps > 0).Select(s => s.Fps!.Value).DefaultIfEmpty(0).Average();
        int spikes = stutters > 0 ? stutters : samples.Sum(s => s.StuttersThisSecond);
        var header = new SessionHeader(1, game, start, samples[^1].TimestampMs / 1000.0 + 1, Math.Round(avg, 1), Math.Round(low1Fps, 1), spikes);
        var session = new SessionTimeline(header, samples, frames);
        string path = _store.Save(session, frames);
        LastSession = new SessionTimeline(header, samples, frames) { FilePath = path };
        return LastSession;
    }

    internal static float[] ToDurations(IReadOnlyList<double> instants)
    {
        var frames = new float[instants.Count - 1];
        for (int i = 0; i < frames.Length; i++) frames[i] = (float)(instants[i + 1] - instants[i]);
        return frames;
    }

    /// <summary>Pior frame e engasgos de cada segundo. Critério de engasgo: o mesmo das sessões salvas (2,5 × a mediana e mais de 25 ms).</summary>
    internal static List<SessionSample> MergeFrames(List<SessionSample> samples, float[] frames)
    {
        var sorted = (float[])frames.Clone();
        Array.Sort(sorted);
        double median = sorted[sorted.Length / 2];
        double limit = Math.Max(FrameStatistics.StutterMedianFactor * median, FrameStatistics.StutterMinMs);

        int seconds = samples.Count;
        var worst = new double[seconds];
        var stutters = new int[seconds];
        double at = 0;
        int index = 0;
        foreach (float frame in frames)
        {
            at += frame;
            while (index + 1 < seconds && at >= samples[index + 1].TimestampMs) index++;
            if (frame > worst[index]) worst[index] = frame;
            if (frame > limit) stutters[index]++;
        }
        var merged = new List<SessionSample>(seconds);
        for (int i = 0; i < seconds; i++)
            merged.Add(worst[i] > 0 ? samples[i] with { FrametimeMs = worst[i], StuttersThisSecond = stutters[i] } : samples[i]);
        return merged;
    }

    /// <summary>Solta sem gravar: o app fechando no meio do jogo não deixa arquivo pela metade.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _recording = false;
            _samples.Clear();
        }
    }
}

/// <summary>Qual trecho da sessão o gráfico mostra. Só contas: zoom e arraste sem janela, para o teste não depender de mouse.</summary>
public sealed class TimelineViewport
{
    public const long MinSpanMs = 5000;

    public long TotalMs { get; }
    public long StartMs { get; private set; }
    public long EndMs { get; private set; }
    public long SpanMs => EndMs - StartMs;

    public TimelineViewport(long totalMs)
    {
        TotalMs = Math.Max(totalMs, 1);
        Reset();
    }

    public void Reset()
    {
        StartMs = 0;
        EndMs = TotalMs;
    }

    /// <param name="anchor">Onde o mouse está, de 0 a 1 na largura: esse instante fica parado na tela.</param>
    /// <param name="factor">Menor que 1 aproxima; maior que 1 afasta.</param>
    public void Zoom(double anchor, double factor)
    {
        anchor = Math.Clamp(anchor, 0, 1);
        double at = StartMs + SpanMs * anchor;
        double span = Math.Clamp(SpanMs * factor, Math.Min(MinSpanMs, TotalMs), TotalMs);
        Place(at - span * anchor, span);
    }

    /// <param name="fraction">Quanto arrastar, em frações da largura. Positivo vai para a frente no tempo.</param>
    public void Pan(double fraction) => Place(StartMs + SpanMs * fraction, SpanMs);

    private void Place(double start, double span)
    {
        start = Math.Clamp(start, 0, TotalMs - span);
        StartMs = (long)start;
        EndMs = (long)(start + span);
    }

    public double FractionOf(long ms) => SpanMs > 0 ? (ms - StartMs) / (double)SpanMs : 0;
    public long MsAt(double fraction) => (long)(StartMs + SpanMs * Math.Clamp(fraction, 0, 1));
}
