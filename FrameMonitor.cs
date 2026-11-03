using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ThgOptimization.Infrastructure;

namespace ThgOptimization;

/// <param name="Low1Fps">FPS equivalente ao percentil 99 do frametime nos últimos 10 s. Detail traz as demais métricas da mesma janela.</param>
/// <param name="RecentFrameTimes">Aponta para um buffer reaproveitado por thread, válido até a próxima leitura nela. Quem for guardar faz ToArray.</param>
public sealed record FrameStats(int ProcessId, string ProcessName, double Fps, double FrameTimeMs, double Low1Fps,
                                ReadOnlyMemory<double> RecentFrameTimes)
{
    public FrameStatistics? Detail { get; init; }
}

public enum FrameCollectionState { Off, Running, Failed }

/// <summary>
/// Mede o FPS de qualquer processo sem injetar código, como o PresentMon: escuta via ETW os eventos
/// "Present" que o Windows emite a cada frame entregue por DXGI (DirectX 10/11/12) e Direct3D 9.
/// Jogos Vulkan/OpenGL só aparecem se apresentarem via DXGI (comum em drivers recentes, não garantido).
///
/// O evento de cada frame não trava nada: uma única thread escreve (a do ETW) e os leitores copiam o que precisam em buffers próprios.
/// Ordenar, buscar nome de processo e limpar buffers antigos ficam fora do caminho do evento.
/// </summary>
public sealed class FrameMonitor : IDisposable
{
    private const string SessionName = "tHgOptimization-Frames";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    // Fonte dos ids e das keywords: PresentData/ETW do PresentMon. MultiplaneOverlay também é um Present de DXGI.
    private const int DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1;
    /// <summary>Analytic | Events: só os eventos de Present dos dois provedores, em vez de todos (ulong.MaxValue).</summary>
    private const ulong PresentKeywords = 0x8000000000000002;
    private const double HistoryMs = 10_000; // janela para o 1% low
    private const int MaxCaptureFrames = 3_000_000; // ~2,7 h a 300 FPS (24 MB)
    private static readonly TimeSpan PruneEvery = TimeSpan.FromSeconds(30), ThreadStopWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Instantes dos últimos frames de um processo num buffer circular de tamanho fixo. Um escritor (a thread do ETW) e leitores
    /// sem trava: o escritor grava a posição e só depois publica o índice; o leitor copia e confere o índice de novo, descartando o que
    /// pode ter sido sobrescrito. 16.384 frames cobrem os 10 s do 1% low até 1.600 FPS; acima disso a janela fica um pouco mais curta.
    /// </summary>
    internal sealed class PresentRing
    {
        internal const int Capacity = 1 << 14;
        private const int Mask = Capacity - 1;
        private readonly double[] _buf = new double[Capacity];
        private long _head;      // total de frames gravados; publicado com Volatile
        private long _lastSeen;  // Stopwatch do último evento
        private double _first = double.NaN;
        /// <summary>Tirado do mapa pela limpeza: o escritor procura de novo se o processo voltar a desenhar.</summary>
        public volatile bool Detached;
        /// <summary>Gravações deste processo. Troca o vetor inteiro, nunca altera: o escritor lê uma referência por evento.</summary>
        public volatile CaptureBuffer[] Captures = Array.Empty<CaptureBuffer>();

        public long Total => Volatile.Read(ref _head);
        public long LastSeen => Volatile.Read(ref _lastSeen);
        public double FirstTimestamp => Volatile.Read(ref _first);

        /// <summary>Só a thread do ETW chama.</summary>
        public void Add(double t, long now)
        {
            long h = _head;
            if (h == 0) Volatile.Write(ref _first, t);
            _buf[h & Mask] = t;
            Volatile.Write(ref _head, h + 1);
            Volatile.Write(ref _lastSeen, now);
        }

        /// <summary>Copia os frames disponíveis, do mais antigo para o mais novo, e devolve quantos são válidos.</summary>
        public int CopyLatest(double[] dst)
        {
            long head = Volatile.Read(ref _head);
            int n = (int)Math.Min(head, Capacity - 1);
            long start = head - n;
            int from = (int)(start & Mask);
            int first = Math.Min(n, Capacity - from);
            Array.Copy(_buf, from, dst, 0, first);
            Array.Copy(_buf, 0, dst, first, n - first);
            Thread.MemoryBarrier(); // as cópias acima terminam antes de reler o índice
            long firstValid = Volatile.Read(ref _head) - Capacity + 1;
            if (firstValid <= start) return n;
            int skip = (int)Math.Min(n, firstValid - start);
            Array.Copy(dst, skip, dst, 0, n - skip);
            return n - skip;
        }
    }

    /// <summary>Gravação de tamanho fixo, reservada antes de começar. O evento só grava e soma: cheia, conta o que ficou de fora.</summary>
    internal sealed class CaptureBuffer
    {
        public readonly int Id;
        public readonly double[] Data;
        private int _count;
        private long _dropped;
        public CaptureBuffer(int id, int capacity) { Id = id; Data = new double[capacity]; }
        public int Count => Volatile.Read(ref _count);
        public long Dropped => Volatile.Read(ref _dropped);

        public void Add(double t)
        {
            int c = _count;
            if (c >= Data.Length) { Volatile.Write(ref _dropped, _dropped + 1); return; }
            Data[c] = t;
            Volatile.Write(ref _count, c + 1);
        }
    }

    private readonly object _lifecycle = new();
    private readonly object _captureGate = new();
    private readonly ConcurrentDictionary<int, PresentRing> _rings = new();
    private readonly Dictionary<int, (PresentRing Ring, CaptureBuffer Buffer)> _captures = new();
    private int _nextCapture;
    private TraceEventSession? _session;
    private Thread? _thread;
    private Timer? _maintenance;
    private volatile string? _error;
    private long _events;
    // Só a thread do ETW usa: um jogo manda quase todos os eventos, então o mapa quase nunca é consultado.
    private int _lastPid = -1;
    private PresentRing? _lastRing;

    public bool Running => _thread?.IsAlive == true;
    public string? Error => _error;
    public FrameCollectionState State => _error != null ? FrameCollectionState.Failed : Running ? FrameCollectionState.Running : FrameCollectionState.Off;
    /// <summary>Frames recebidos desde a abertura. A diferença entre duas leituras dá os eventos por segundo.</summary>
    public long EventsReceived => Volatile.Read(ref _events);
    /// <summary>Eventos que o Windows descartou nesta sessão: se subir, as métricas têm buracos.</summary>
    public long EventsLost { get { try { return _session?.EventsLost ?? 0; } catch (ObjectDisposedException) { return 0; } } }

    public void Start()
    {
        lock (_lifecycle)
        {
            if (Running) return;
            TraceEventSession? session = null;
            try
            {
                // Opção Create derruba uma sessão antiga com o mesmo nome (ex.: app fechado à força).
                session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create) { StopOnDispose = true };
                session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, PresentKeywords, null);
                session.EnableProvider(D3D9Provider, TraceEventLevel.Informational, PresentKeywords, null);
                session.Source.AllEvents += OnEvent;
                _error = null;
                var active = session;
                _thread = new Thread(() =>
                {
                    try { active.Source.Process(); }
                    catch (Exception ex) { _error = ex.Message; AppLog.Error("frames", ex); }
                }) { IsBackground = true, Name = "ETW frames" };
                _session = session;
                _thread.Start();
                _maintenance = new Timer(_ => Prune(PruneEvery), null, PruneEvery, PruneEvery);
            }
            catch (Exception ex)
            {
                // Sem administrador ou sem sessões livres: o resto do app continua, e a falha aparece na tela.
                _error = ex.Message;
                AppLog.Error("frames", ex);
                session?.Dispose();
                _session = null;
                _thread = null;
            }
        }
    }

    private void OnEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        if (id != DxgiPresentStart && id != DxgiPresentMpoStart && id != D3D9PresentStart) return;
        var provider = e.ProviderGuid;
        if (id == D3D9PresentStart ? provider != D3D9Provider : provider != DxgiProvider) return;
        int pid = e.ProcessID;
        if (IsIgnoredPresenter(pid)) return;
        OnPresent(pid, e.TimeStampRelativeMSec);
    }

    /// <summary>
    /// Caminho de cada frame: localiza o buffer, grava o instante e o entrega às gravações ativas. Sem trava e sem alocar, exceto o
    /// buffer do primeiro frame de cada processo. Só a thread do ETW chama (e os testes, no lugar dela).
    /// </summary>
    internal void OnPresent(int pid, double t)
    {
        var ring = _lastRing;
        if (ring == null || pid != _lastPid || ring.Detached)
        {
            ring = _rings.GetOrAdd(pid, static _ => new PresentRing());
            _lastRing = ring;
            _lastPid = pid;
        }
        ring.Add(t, Stopwatch.GetTimestamp());
        var captures = ring.Captures;
        for (int i = 0; i < captures.Length; i++) captures[i].Add(t);
        Volatile.Write(ref _events, _events + 1);
    }

    /// <summary>Solta os buffers de quem parou de desenhar (programa fechado ou minimizado). Fora do caminho do evento.</summary>
    internal void Prune(TimeSpan idle)
    {
        long now = Stopwatch.GetTimestamp();
        foreach (var (pid, ring) in _rings)
        {
            if (ring.Captures.Length > 0 || Stopwatch.GetElapsedTime(ring.LastSeen, now) <= idle) continue;
            ring.Detached = true;
            _rings.TryRemove(pid, out _);
        }
    }

    internal int TrackedProcesses => _rings.Count;

    /// <summary>Sistema e o próprio tHg (ninguém mede o FPS deles): nem chegam aos buffers.</summary>
    internal static bool IsIgnoredPresenter(int pid) => pid <= 4 || pid == Environment.ProcessId;

    [ThreadStatic] private static double[]? _recentBuffer;

    /// <summary>
    /// Estatísticas do processo, ou null se ele não apresentou frames nos últimos 2 s. Chamado pelo overlay várias vezes por segundo
    /// durante o jogo: usa buffers do ArrayPool, devolvidos antes de sair, e não disputa nada com o evento de frame.
    /// </summary>
    public FrameStats? GetStats(int pid)
    {
        if (!_rings.TryGetValue(pid, out var ring) || ring.Total < 3) return null;
        if (Stopwatch.GetElapsedTime(ring.LastSeen).TotalSeconds > 2) return null;
        var pool = ArrayPool<double>.Shared;
        var ts = pool.Rent(PresentRing.Capacity);
        var ft = pool.Rent(PresentRing.Capacity);
        try
        {
            int total = ring.CopyLatest(ts);
            if (total < 3) return null;
            double last = ts[total - 1];
            int oldest = 0; // a janela de 10 s é aplicada aqui, e não a cada evento
            while (oldest < total - 3 && ts[oldest] < last - HistoryMs) oldest++;
            int n = total - oldest;
            int m = n - 1;
            for (int i = 0; i < m; i++) ft[i] = ts[oldest + i + 1] - ts[oldest + i];

            // FPS do último segundo (pelo relógio dos eventos, imune ao atraso de entrega do ETW).
            int first = oldest;
            while (first < total - 1 && ts[first] < last - 1000) first++;
            double span = last - ts[first];
            int frames = total - 1 - first;
            double fps = span > 0 ? frames * 1000 / span : 0;

            // Últimos 120 frametimes (gráfico), copiados antes de ordenar.
            // O buffer é reaproveitado por thread: RecentFrameTimes vale até a próxima leitura nela (o overlay desenha na hora).
            int rc = Math.Min(120, m);
            var buffer = _recentBuffer ??= new double[120];
            Array.Copy(ft, m - rc, buffer, 0, rc);
            ReadOnlyMemory<double> recent = buffer.AsMemory(0, rc);
            double instant = ft[m - 1] > 0 ? 1000 / ft[m - 1] : 0;

            var detail = FrameStatistics.FromFrameTimes(ft.AsSpan(0, m), instant); // ordena ft no lugar
            return new FrameStats(pid, ProcessName(pid), fps, fps > 0 ? 1000 / fps : 0, detail.Low1PercentileFps, recent) { Detail = detail };
        }
        finally
        {
            pool.Return(ts);
            pool.Return(ft);
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

    // Com o overlay ligado isso é pedido várias vezes por segundo. O nome do processo não muda, então fica guardado.
    private readonly Dictionary<int, string> _names = new();

    private string ProcessName(int pid)
    {
        lock (_names)
        {
            if (_names.TryGetValue(pid, out var n)) return n;
            if (_names.Count > 256) _names.Clear();
        }
        // Sem abrir o processo: GetProcessById abre, e o jogo pode ser o Valorant (Vanguard-safe).
        string name = ProcessUtil.NameOf(pid) ?? $"PID {pid}";
        lock (_names) _names[pid] = name;
        return name;
    }

    /// <summary>
    /// Começa a guardar todos os frames do processo (para 0,1% low, engasgos e CSV). Devolve o id da gravação. O buffer nasce do tamanho
    /// esperado, com 50% de folga, e não cresce no meio do jogo. O FPS medido agora conta, se for maior que o esperado.
    /// </summary>
    public int StartCapture(int pid, TimeSpan expectedDuration, int expectedFps = 144)
    {
        double fps = Math.Max(expectedFps, MeasuredFps(pid));
        int capacity = (int)Math.Clamp(expectedDuration.TotalSeconds * fps * 1.5, 16_384, MaxCaptureFrames);
        lock (_captureGate)
        {
            var buffer = new CaptureBuffer(++_nextCapture, capacity); // reservado antes de o evento enxergar
            var ring = _rings.GetOrAdd(pid, static _ => new PresentRing());
            ring.Captures = ring.Captures.Append(buffer).ToArray();
            _captures[buffer.Id] = (ring, buffer);
            return buffer.Id;
        }
    }

    private double MeasuredFps(int pid)
    {
        if (!_rings.TryGetValue(pid, out var ring) || ring.Total < 2) return 0;
        var ts = ArrayPool<double>.Shared.Rent(PresentRing.Capacity);
        try
        {
            int n = ring.CopyLatest(ts);
            if (n < 2) return 0;
            double span = ts[n - 1] - ts[0];
            return span > 0 ? (n - 1) * 1000 / span : 0;
        }
        finally { ArrayPool<double>.Shared.Return(ts); }
    }

    /// <summary>Capacidade reservada da gravação (0 = não existe). Para os testes.</summary>
    internal int CaptureCapacity(int id)
    {
        lock (_captureGate) return _captures.TryGetValue(id, out var c) ? c.Buffer.Data.Length : 0;
    }

    /// <summary>Frames que não couberam na gravação. Quem mostra o resultado avisa, porque as métricas ficam sem o fim da sessão.</summary>
    internal long CaptureDropped(int id)
    {
        lock (_captureGate) return _captures.TryGetValue(id, out var c) ? c.Buffer.Dropped : 0;
    }

    /// <summary>Termina a gravação e devolve os instantes de cada frame (ms). O buffer reservado é solto aqui.</summary>
    public List<double> StopCapture(int id) => StopCapture(id, out _);

    public List<double> StopCapture(int id, out long dropped)
    {
        dropped = 0;
        CaptureBuffer buffer;
        lock (_captureGate)
        {
            if (!_captures.Remove(id, out var c)) return new List<double>();
            c.Ring.Captures = c.Ring.Captures.Where(b => b.Id != id).ToArray();
            buffer = c.Buffer;
        }
        // O evento pode estar somando um último frame neste instante: Count é publicado depois do dado, então o que se lê aqui é válido.
        dropped = buffer.Dropped;
        int count = buffer.Count;
        var times = new List<double>(count);
        times.AddRange(new ArraySegment<double>(buffer.Data, 0, count));
        return times;
    }

    /// <summary>Desliga a medição (a sessão ETW) até o próximo Start. Gravações em andamento continuam quando a medição voltar.</summary>
    public void Stop()
    {
        lock (_lifecycle) StopSession();
        foreach (var (pid, ring) in _rings)
        {
            if (ring.Captures.Length > 0) continue;
            ring.Detached = true;
            _rings.TryRemove(pid, out _);
        }
        lock (_names) _names.Clear();
    }

    private void StopSession()
    {
        _maintenance?.Dispose();
        _maintenance = null;
        var session = _session;
        var thread = _thread;
        _session = null;
        _thread = null;
        if (session != null)
        {
            try { session.Source.StopProcessing(); }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { AppLog.Error("frames", ex); }
            try { session.Dispose(); }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.Runtime.InteropServices.COMException) { AppLog.Error("frames", ex); }
        }
        if (thread != null && thread != Thread.CurrentThread && !thread.Join(ThreadStopWait))
            AppLog.Error("frames", "A thread do ETW não terminou no tempo.");
        _lastRing = null;
        _lastPid = -1;
    }

    public void Dispose()
    {
        lock (_lifecycle) StopSession();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
