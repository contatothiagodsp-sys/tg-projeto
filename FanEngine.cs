using System.Diagnostics;
using ThgOptimization.Infrastructure;

namespace ThgOptimization;

/// <summary>
/// Lê as temperaturas para as curvas. Uma leitura que falta por um instante mantém a última válida, para a fan não saltar por causa de um sensor
/// que piscou. Passado HoldLastValid sem leitura, a falta chega à curva, e ali vale a proteção de sempre (100%). Teste de GPU em andamento
/// não consulta o driver: vale a última leitura, sem prazo.
/// </summary>
public sealed class FanSensorReader
{
    private readonly FanControlEngine.InputReader _read;
    private readonly Func<bool> _gpuPaused;
    private float? _lastCpu, _lastGpu;
    private long _cpuAt, _gpuAt;

    public FanSensorReader(FanControlEngine.InputReader read, Func<bool> gpuPaused)
    {
        _read = read;
        _gpuPaused = gpuPaused;
    }

    public TimeSpan HoldLastValid { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>false = sensores fechados: a rodada é pulada, sem mexer nas fans.</summary>
    public bool TryRead(bool needCpu, bool needGpu, bool board, out float? cpu, out float? gpu)
    {
        bool paused = needGpu && _gpuPaused();
        bool readGpu = needGpu && !paused;
        cpu = gpu = null;
        FanInputs inputs = default;
        if (needCpu || readGpu || board)
        {
            if (_read(needCpu, readGpu, board) is not FanInputs read) return false;
            inputs = read;
        }
        long now = Stopwatch.GetTimestamp();
        if (inputs.CpuC is float c) { _lastCpu = c; _cpuAt = now; }
        if (readGpu && inputs.GpuC is float g) { _lastGpu = g; _gpuAt = now; }
        cpu = needCpu ? Held(_lastCpu, _cpuAt, now) : null;
        gpu = paused ? _lastGpu : Held(_lastGpu, _gpuAt, now);
        return true;
    }

    private float? Held(float? value, long at, long now) => value != null && Stopwatch.GetElapsedTime(at, now) <= HoldLastValid ? value : null;
}

/// <summary>Aplica as curvas nas saídas. Depois de cancelado, nenhuma saída recebe comando: quem encerrou já pode devolver a fan à BIOS.</summary>
public sealed class FanOutputController
{
    public void Apply(FanChannelView[] channels, FanSettings settings, float? cpu, float? gpu, double dt, CancellationToken cancel)
    {
        foreach (var v in channels)
        {
            if (cancel.IsCancellationRequested) return;
            try { v.EngineLive = FanCurves.Step(v, settings, cpu, gpu, dt); }
            catch (Exception ex) { AppLog.Error("fans", ex); } // uma saída com erro não impede as outras
        }
    }
}

/// <summary>
/// Curvas de fan fora da janela: um PeriodicTimer numa tarefa de fundo lê as temperaturas, calcula o passo e aplica a rotação, sem Dispatcher e sem
/// nenhum tipo do WPF. Com a janela travada, na bandeja ou durante um jogo as fans continuam obedecendo. A janela só escolhe os canais e lê
/// FanChannelView.EngineLive. Saídas da BIOS não custam leitura: com todas nela, o laço não toca no barramento.
/// </summary>
public sealed class FanControlEngine : IDisposable
{
    /// <summary>Lê (cpu, gpu, placa-mãe). Null = sensores fechados: a rodada é pulada, sem mandar a fan a 100%.</summary>
    public delegate FanInputs? InputReader(bool cpu, bool gpu, bool motherboard);

    private readonly FanSensorReader _sensors;
    private readonly FanOutputController _outputs = new();
    private readonly Func<FanSettings> _settings;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();
    private volatile FanChannelView[] _channels = Array.Empty<FanChannelView>();
    private volatile bool _liveWanted;
    private CancellationTokenSource? _cts;
    private Task _loop = Task.CompletedTask;
    private long _lastStep;
    private bool _disposed;

    /// <param name="gpuPaused">Teste de GPU em andamento: consultar o driver aparece como engasgo no resultado. Vale a última leitura.</param>
    public FanControlEngine(InputReader read, Func<FanSettings> settings, Func<bool> gpuPaused, TimeSpan? interval = null)
    {
        _sensors = new FanSensorReader(read, gpuPaused);
        _settings = settings;
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    public FanSensorReader Sensors => _sensors;

    /// <summary>
    /// Alguém está vendo a rotação: lê também a placa-mãe. A curva depende só da temperatura da CPU e da GPU, e ler o Super I/O durante
    /// o jogo é causa conhecida de engasgo, por isso, com a página fechada, esse barramento fica de fora.
    /// </summary>
    public bool LiveWanted { get => _liveWanted; set => _liveWanted = value; }

    /// <summary>Há um laço vivo. Depois de um Stop que estourou o tempo, continua true até ele terminar de fato.</summary>
    public bool Running { get { lock (_gate) return !_loop.IsCompleted; } }

    /// <summary>Troca a lista de saídas. A cópia é do motor: a lista original continua sendo da janela.</summary>
    public void SetChannels(IEnumerable<FanChannelView> channels) => _channels = channels.ToArray();

    /// <summary>
    /// Inicia o laço; se já está rodando, não faz nada. false = o laço anterior ainda não terminou, ou o motor foi descartado:
    /// dois laços nunca escrevem nas mesmas fans.
    /// </summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (!_loop.IsCompleted)
            {
                if (_cts is { IsCancellationRequested: false }) return true;
                AppLog.Error("fans", "O laço anterior das curvas ainda não terminou: as curvas não foram reiniciadas.");
                return false;
            }
            _cts?.Dispose();
            var cts = new CancellationTokenSource();
            _cts = cts;
            _lastStep = Stopwatch.GetTimestamp();
            _loop = Task.Run(() => Loop(cts.Token));
            return true;
        }
    }

    /// <summary>
    /// Pede o fim e espera o passo em andamento: depois de true, nada retoma a fan que voltou para a BIOS. false = o passo não terminou no
    /// tempo pedido. O cancelamento segue valendo, então ele não comanda mais nenhuma saída depois da que estiver em andamento.
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        Task loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            loop = _loop;
            cts = _cts;
            cts?.Cancel();
        }
        try { await loop.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            AppLog.Error("fans", "O passo das curvas não terminou no tempo de encerramento.");
            return false;
        }
        lock (_gate)
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                cts?.Dispose(); // só agora: o laço não usa mais o token
            }
        }
        return true;
    }

    /// <inheritdoc cref="StopAsync"/>
    public bool Stop(TimeSpan wait) => StopAsync(wait).GetAwaiter().GetResult();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop(TimeSpan.FromSeconds(2));
    }

    private async Task Loop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { Step(ct); }
                catch (Exception ex) { AppLog.Error("fans", ex); } // uma rodada ruim não pode parar as curvas
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Uma rodada: chamada só pelo laço (e pelos testes).</summary>
    internal void Step(CancellationToken ct = default)
    {
        var channels = _channels;
        bool needCpu = false, needGpu = false;
        foreach (var v in channels)
        {
            if (v.Config.Preset == "bios") continue;
            needCpu = true; // a proteção de 90 °C vale em toda curva, mesmo as guiadas pela GPU
            if (v.Config.Source is "gpu" or "max") needGpu = true;
        }
        double dt = Math.Clamp(Stopwatch.GetElapsedTime(_lastStep).TotalSeconds, 0.2, 5);
        _lastStep = Stopwatch.GetTimestamp();
        if (ct.IsCancellationRequested) return;
        if (!_sensors.TryRead(needCpu, needGpu, _liveWanted, out float? cpu, out float? gpu)) return;
        _outputs.Apply(channels, _settings(), cpu, gpu, dt, ct);
    }
}

/// <summary>O que a página de fans mostra. A tela copia o texto do motor no ritmo dela, e só com a página aberta: o laço de controle nunca toca na interface.</summary>
public sealed class FanPageViewModel
{
    public List<FanChannelView> Channels { get; } = new();

    /// <summary>Copia a última leitura do motor para o texto ligado à tela. Página fechada: nada a fazer, e a placa-mãe nem é lida.</summary>
    public void Publish(FanControlEngine? engine, bool visible)
    {
        if (engine != null) engine.LiveWanted = visible;
        if (!visible) return;
        foreach (var v in Channels) v.Live = v.EngineLive;
    }
}
