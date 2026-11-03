using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ThgOptimization.Tests;

/// <summary>
/// Compara o caminho do evento de frame, do antigo (1.6.4, copiado abaixo) com o atual. Só roda com THG_BENCH com o caminho do arquivo de saída:
/// <c>$env:THG_BENCH="saida.md"; dotnet test -c Release --filter FramePathBenchmark</c>. Mede o código em memória, sem ETW de verdade.
/// </summary>
public class FramePathBenchmark
{
    private const int Pid = 1000, Events = 3_000_000, Runs = 3;

    /// <summary>Caminho do evento e leitura da 1.6.4 (FrameMonitor em ad6127c), sem mudanças de lógica.</summary>
    private sealed class Legacy
    {
        private sealed class Ring
        {
            private const int Capacity = 1 << 14, Mask = Capacity - 1;
            private readonly double[] _buf = new double[Capacity];
            private int _start;
            public int Count { get; private set; }
            public double Oldest => _buf[_start];
            public void Add(double t)
            {
                if (Count == Capacity) { _start = (_start + 1) & Mask; Count--; }
                _buf[(_start + Count) & Mask] = t;
                Count++;
            }
            public void DropOldest() { _start = (_start + 1) & Mask; Count--; }
            public void CopyTo(double[] dst)
            {
                int first = Math.Min(Count, Capacity - _start);
                Array.Copy(_buf, _start, dst, 0, first);
                Array.Copy(_buf, 0, dst, first, Count - first);
            }
        }

        private readonly object _lock = new();
        private readonly Dictionary<int, Ring> _presents = new();
        private readonly Dictionary<int, long> _lastSeen = new();
        private readonly Dictionary<int, (int Pid, List<double> Times)> _captures = new();
        private long _lastPrune = Stopwatch.GetTimestamp();

        public void StartCapture(int pid, int cap) { lock (_lock) _captures[1] = (pid, new List<double>(cap)); }

        public void OnEvent(int pid, double t)
        {
            lock (_lock)
            {
                if (!_presents.TryGetValue(pid, out var q)) _presents[pid] = q = new Ring();
                q.Add(t);
                while (q.Count > 0 && q.Oldest < t - 10_000) q.DropOldest();
                long now = Stopwatch.GetTimestamp();
                _lastSeen[pid] = now;
                if (Stopwatch.GetElapsedTime(_lastPrune, now).TotalSeconds > 30)
                {
                    _lastPrune = now;
                    foreach (var id in _lastSeen.Where(kv => Stopwatch.GetElapsedTime(kv.Value, now).TotalSeconds > 30).Select(kv => kv.Key).ToList())
                    {
                        _lastSeen.Remove(id);
                        _presents.Remove(id);
                    }
                }
                foreach (var c in _captures.Values)
                    if (c.Pid == pid && c.Times.Count < 3_000_000) c.Times.Add(t);
            }
        }

        public double Read(int pid)
        {
            double[] ts; int n;
            lock (_lock)
            {
                if (!_presents.TryGetValue(pid, out var q) || q.Count < 3) return 0;
                n = q.Count;
                ts = ArrayPool<double>.Shared.Rent(n);
                q.CopyTo(ts);
            }
            var ft = ArrayPool<double>.Shared.Rent(n);
            try
            {
                int m = n - 1;
                for (int i = 1; i < n; i++) ft[i - 1] = ts[i] - ts[i - 1];
                Array.Sort(ft, 0, m);
                return ft[(int)Math.Min(m - 1, Math.Floor(m * 0.99))];
            }
            finally { ArrayPool<double>.Shared.Return(ts); ArrayPool<double>.Shared.Return(ft); }
        }
    }

    private sealed record Result(double NsPerEvent, long AllocatedBytes, double P999Ns, double MaxMicros, int Reads);

    /// <summary>Uma thread lendo a 20 Hz (o overlay faz 2 a 4), para a disputa aparecer numa execução curta.</summary>
    private static Result Measure(Action<double> onEvent, Action read)
    {
        for (int i = 0; i < 20_000; i++) onEvent(i * 0.1); // aquecimento
        using var stop = new CancellationTokenSource();
        int reads = 0;
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                read();
                reads++;
                try { await Task.Delay(50, stop.Token); } catch (OperationCanceledException) { }
            }
        });
        // Tempo de cada evento em faixas de 100 ns: sem guardar milhões de amostras nem alocar durante a medida.
        var buckets = new long[4096];
        long maxTicks = 0, allocated = GC.GetAllocatedBytesForCurrentThread();
        double tickNs = 1e9 / Stopwatch.Frequency;
        long total = 0;
        double t = 2000;
        for (int i = 0; i < Events; i++)
        {
            long a = Stopwatch.GetTimestamp();
            onEvent(t += 0.1);
            long dt = Stopwatch.GetTimestamp() - a;
            total += dt;
            if (dt > maxTicks) maxTicks = dt;
            buckets[Math.Min((long)(dt * tickNs / 100), buckets.Length - 1)]++;
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        stop.Cancel();
        reader.GetAwaiter().GetResult();
        long rank = (long)(Events * 0.999), seen = 0;
        int bucket = 0;
        for (; bucket < buckets.Length; bucket++) if ((seen += buckets[bucket]) >= rank) break;
        return new Result(total * tickNs / Events, allocated, (bucket + 1) * 100.0, maxTicks * tickNs / 1000, reads);
    }

    private static Result LegacyRun()
    {
        var m = new Legacy();
        m.StartCapture(Pid, 3_100_000);
        return Measure(t => m.OnEvent(Pid, t), () => m.Read(Pid));
    }

    private static Result CurrentRun()
    {
        using var m = new FrameMonitor();
        int id = m.StartCapture(Pid, TimeSpan.FromSeconds(10), 310_000);
        var result = Measure(t => m.OnPresent(Pid, t), () => m.GetStats(Pid));
        m.StopCapture(id);
        return result;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted[sorted.Length / 2];
    }

    private static string Row(string name, IReadOnlyList<Result> r)
    {
        static string F(double v) => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        string Range(Func<Result, double> f) => $"{F(Median(r.Select(f)))} ({F(r.Min(f))} a {F(r.Max(f))})";
        return $"| {name} | {Range(x => x.NsPerEvent)} | {Range(x => x.P999Ns)} | {Range(x => x.MaxMicros)} | {r.Max(x => x.AllocatedBytes)} | {r.Sum(x => x.Reads)} |";
    }

    [Fact]
    public void Run_when_requested()
    {
        string? output = Environment.GetEnvironmentVariable("THG_BENCH");
        if (string.IsNullOrEmpty(output)) return;
        var legacy = new List<Result>();
        var current = new List<Result>();
        for (int i = 0; i < Runs; i++) { legacy.Add(LegacyRun()); current.Add(CurrentRun()); } // alternados, para o ruído da máquina cair nos dois
        var sb = new StringBuilder();
        sb.AppendLine($"{Events:N0} eventos por execução, {Runs} execuções, 1 gravação ativa e 1 leitor a 20 Hz. Mediana (mínimo a máximo). {RuntimeInfo()}");
        sb.AppendLine();
        sb.AppendLine("| Caminho | ns por evento | P99,9 (ns, faixa de 100) | Pior evento (µs) | Bytes alocados no evento | Leituras |");
        sb.AppendLine("|---|---|---|---|---|---|");
        sb.AppendLine(Row("1.6.4 (trava única)", legacy));
        sb.AppendLine(Row("Atual (sem trava)", current));
        File.WriteAllText(output, sb.ToString());
    }

    private static string RuntimeInfo()
    {
#if DEBUG
        return "Build Debug.";
#else
        return "Build Release.";
#endif
    }
}
