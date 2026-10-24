using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace ThgOptimization;

public sealed record FrameStats(int ProcessId, string ProcessName, double Fps, double FrameTimeMs, double Low1Fps,
                                IReadOnlyList<double> RecentFrameTimes);

/// <summary>
/// Mede o FPS de qualquer processo sem injetar código, como o PresentMon: escuta via ETW os eventos
/// "Present" que o Windows emite a cada frame entregue por DXGI (DirectX 10/11/12) e Direct3D 9.
/// Jogos Vulkan/OpenGL só aparecem se apresentarem via DXGI (comum em drivers recentes, não garantido).
/// </summary>
public sealed class FrameMonitor : IDisposable
{
    private const string SessionName = "tHgOptimization-Frames";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42, D3D9PresentStart = 1;
    private const double HistoryMs = 10_000; // janela para o 1% low

    private TraceEventSession? _session;
    private Thread? _thread;
    private readonly object _lock = new();
    private readonly Dictionary<int, PresentRing> _presents = new(); // pid → timestamps (ms, relógio do ETW)
    private long _lastPrune = Stopwatch.GetTimestamp();

    /// <summary>
    /// Instantes dos últimos frames de um processo num buffer circular de tamanho fixo: o evento de cada frame (até
    /// milhares por segundo) só grava um double, sem alocar nem crescer como a Queue fazia. 16.384 frames cobrem os
    /// 10 s do 1% low até 1.600 FPS; acima disso a janela fica um pouco mais curta.
    /// </summary>
    private sealed class PresentRing
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

        /// <summary>Copia em ordem (mais antigo primeiro) para <paramref name="dst"/>.</summary>
        public void CopyTo(double[] dst)
        {
            int first = Math.Min(Count, Capacity - _start);
            Array.Copy(_buf, _start, dst, 0, first);
            Array.Copy(_buf, 0, dst, first, Count - first);
        }
    }
    private readonly Dictionary<int, long> _lastSeen = new();           // pid → Stopwatch do último evento recebido
    // Gravações completas (todos os frames de um processo): id → (pid, timestamps em ms)
    private readonly Dictionary<int, (int Pid, List<double> Times)> _captures = new();
    private int _nextCapture;
    private const int MaxCaptureFrames = 3_000_000; // ~2,7 h a 300 FPS (24 MB)

    public bool Running => _thread?.IsAlive == true;
    public string? Error { get; private set; }

    public void Start()
    {
        if (Running) return;
        try
        {
            // Opção Create derruba uma sessão antiga com o mesmo nome (ex.: app fechado à força).
            _session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create) { StopOnDispose = true };
            _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue, null);
            _session.EnableProvider(D3D9Provider, TraceEventLevel.Informational, ulong.MaxValue, null);
            _session.Source.AllEvents += OnEvent;
            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception ex) { Error = ex.Message; }
            }) { IsBackground = true, Name = "ETW frames" };
            _thread.Start();
            Error = null;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _session?.Dispose();
            _session = null;
        }
    }

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        bool present = (e.ProviderGuid == DxgiProvider && id == DxgiPresentStart) ||
                       (e.ProviderGuid == D3D9Provider && id == D3D9PresentStart);
        if (!present) return;

        double t = e.TimeStampRelativeMSec;
        lock (_lock)
        {
            if (!_presents.TryGetValue(e.ProcessID, out var q)) _presents[e.ProcessID] = q = new PresentRing();
            q.Add(t);
            while (q.Count > 0 && q.Oldest < t - HistoryMs) q.DropOldest();
            long now = Stopwatch.GetTimestamp();
            _lastSeen[e.ProcessID] = now;
            // A cada 30 s, solta os buffers de quem parou de desenhar há mais de 30 s (programa fechado ou minimizado).
            if (Stopwatch.GetElapsedTime(_lastPrune, now).TotalSeconds > 30)
            {
                _lastPrune = now;
                foreach (var pid in _lastSeen.Where(kv => Stopwatch.GetElapsedTime(kv.Value, now).TotalSeconds > 30).Select(kv => kv.Key).ToList())
                {
                    _lastSeen.Remove(pid);
                    _presents.Remove(pid);
                }
            }
            foreach (var c in _captures.Values)
                if (c.Pid == e.ProcessID && c.Times.Count < MaxCaptureFrames) c.Times.Add(t);
        }
    }

    /// <summary>
    /// Estatísticas do processo, ou null se ele não apresentou frames nos últimos 2 s. Chamado pelo overlay várias vezes por
    /// segundo durante o jogo: usa buffers reaproveitados (ArrayPool) e ordena no lugar, sem LINQ, para não gerar lixo
    /// a cada leitura e não acordar o coletor de lixo no meio da partida.
    /// </summary>
    public FrameStats? GetStats(int pid)
    {
        double[] ts; int n;
        lock (_lock)
        {
            if (!_presents.TryGetValue(pid, out var q) || q.Count < 3) return null;
            if (Stopwatch.GetElapsedTime(_lastSeen[pid]).TotalSeconds > 2) return null;
            n = q.Count;
            ts = System.Buffers.ArrayPool<double>.Shared.Rent(n);
            q.CopyTo(ts);
        }
        var ft = System.Buffers.ArrayPool<double>.Shared.Rent(n);
        try
        {
            double last = ts[n - 1];
            int m = n - 1;
            for (int i = 1; i < n; i++) ft[i - 1] = ts[i] - ts[i - 1];

            // FPS do último segundo (pelo relógio dos eventos, imune ao atraso de entrega do ETW).
            int first = 0;
            while (first < n - 1 && ts[first] < last - 1000) first++;
            double span = last - ts[first];
            int frames = n - 1 - first;
            double fps = span > 0 ? frames * 1000 / span : 0;

            // Últimos 120 frametimes (gráfico), copiados antes de ordenar.
            int rc = Math.Min(120, m);
            var recent = new double[rc];
            Array.Copy(ft, m - rc, recent, 0, rc);

            // 1% low: FPS equivalente ao percentil 99 do frametime nos últimos 10 s.
            Array.Sort(ft, 0, m);
            double p99 = ft[(int)Math.Min(m - 1, Math.Floor(m * 0.99))];
            double low1 = p99 > 0 ? 1000 / p99 : 0;
            return new FrameStats(pid, ProcessName(pid), fps, fps > 0 ? 1000 / fps : 0, low1, recent);
        }
        finally
        {
            System.Buffers.ArrayPool<double>.Shared.Return(ts);
            System.Buffers.ArrayPool<double>.Shared.Return(ft);
        }
    }

    /// <summary>Estatísticas do programa em primeiro plano (o jogo que está na tela).</summary>
    public FrameStats? GetForegroundStats()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == 0 || pid == Environment.ProcessId ? null : GetStats((int)pid);
    }

    // Process.GetProcessById percorre a lista de todos os processos do Windows: com o overlay ligado isso acontecia várias
    // vezes por segundo. O nome do processo não muda, então fica guardado.
    private readonly Dictionary<int, string> _names = new();

    private string ProcessName(int pid)
    {
        lock (_names)
        {
            if (_names.TryGetValue(pid, out var n)) return n;
            if (_names.Count > 256) _names.Clear();
        }
        string name;
        // Pela lista do Windows: GetProcessById abre o processo, e o jogo pode ser o Valorant (Vanguard-safe).
        name = $"PID {pid}";
        foreach (var p in Process.GetProcesses())
            using (p)
                if (p.Id == pid) name = p.ProcessName;
        lock (_names) _names[pid] = name;
        return name;
    }

    /// <summary>Começa a guardar todos os frames do processo (para 0,1% low, engasgos e CSV). Devolve o id da gravação.</summary>
    public int StartCapture(int pid)
    {
        lock (_lock) { int id = ++_nextCapture; _captures[id] = (pid, new List<double>(4096)); return id; }
    }

    /// <summary>Termina a gravação e devolve os instantes de cada frame (ms).</summary>
    public List<double> StopCapture(int id)
    {
        lock (_lock)
        {
            if (!_captures.Remove(id, out var c)) return new List<double>();
            return c.Times;
        }
    }

    /// <summary>Desliga a medição (a sessão ETW) até o próximo Start.</summary>
    public void Stop()
    {
        try { _session?.Source.StopProcessing(); } catch { }
        try { _session?.Dispose(); } catch { }
        _session = null;
        _thread = null;
        lock (_lock) { _presents.Clear(); _lastSeen.Clear(); }
        lock (_names) _names.Clear();
    }

    public void Dispose()
    {
        try { _session?.Source.StopProcessing(); } catch { }
        _session?.Dispose();
        _session = null;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
