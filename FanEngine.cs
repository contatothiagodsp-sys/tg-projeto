using System.Diagnostics;
using ThgOptimization.Infrastructure;

namespace ThgOptimization;

/// <summary>
/// Curvas de fan fora da janela: um PeriodicTimer numa tarefa de fundo lê as temperaturas, calcula o passo e aplica a rotação,
/// sem Dispatcher. Com a janela travada, na bandeja ou durante um jogo as fans continuam obedecendo. A janela só escolhe os canais e
/// lê FanChannelView.EngineLive. Saídas da BIOS não custam leitura: com todas nela, o laço não toca no barramento.
/// </summary>
public sealed class FanEngine
{
    /// <summary>Lê (cpu, gpu, placa-mãe). Null = sensores fechados: a rodada é pulada, sem mandar a fan a 100%.</summary>
    public delegate FanInputs? InputReader(bool cpu, bool gpu, bool motherboard);

    private readonly InputReader _read;
    private readonly Func<FanSettings> _settings;
    private readonly Func<bool> _gpuPaused;
    private readonly TimeSpan _interval;
    private volatile FanChannelView[] _channels = Array.Empty<FanChannelView>();
    private volatile bool _liveWanted;
    private CancellationTokenSource? _cts;
    private Task _loop = Task.CompletedTask;
    private float? _lastGpu;
    private long _lastStep;

    /// <param name="gpuPaused">Teste de GPU em andamento: consultar o driver aparece como engasgo no resultado. Vale a última leitura.</param>
    public FanEngine(InputReader read, Func<FanSettings> settings, Func<bool> gpuPaused, TimeSpan? interval = null)
    {
        _read = read;
        _settings = settings;
        _gpuPaused = gpuPaused;
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Alguém está vendo a rotação: lê também a placa-mãe. A curva depende só da temperatura da CPU e da GPU, e ler o Super I/O durante
    /// o jogo é causa conhecida de engasgo, por isso, com a página fechada, esse barramento fica de fora.
    /// </summary>
    public bool LiveWanted { get => _liveWanted; set => _liveWanted = value; }

    public bool Running => _cts != null;

    /// <summary>Troca a lista de saídas. A cópia é do motor: a lista original continua sendo da janela.</summary>
    public void SetChannels(IEnumerable<FanChannelView> channels) => _channels = channels.ToArray();

    public void Start()
    {
        if (_cts != null) return;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _lastStep = Stopwatch.GetTimestamp();
        _loop = Task.Run(() => Loop(cts.Token));
    }

    /// <summary>Espera o passo em andamento: depois disto, nada retoma a fan que voltou para a BIOS.</summary>
    public void Stop(TimeSpan wait)
    {
        var cts = _cts;
        if (cts == null) return;
        _cts = null;
        cts.Cancel();
        try { _loop.Wait(wait); } catch (AggregateException) { }
        cts.Dispose();
    }

    private async Task Loop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { Step(); }
                catch (Exception ex) { AppLog.Error("fans", ex); } // uma rodada ruim não pode parar as curvas
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Uma rodada: chamada só pelo laço (e pelos testes).</summary>
    internal void Step()
    {
        var channels = _channels;
        bool needCpu = false, needGpu = false;
        foreach (var v in channels)
        {
            if (v.Config.Preset == "bios") continue;
            needCpu = true; // a proteção de 90 °C vale em toda curva, mesmo as guiadas pela GPU
            if (v.Config.Source is "gpu" or "max") needGpu = true;
        }
        bool readGpu = needGpu && !_gpuPaused();
        bool live = _liveWanted;
        double dt = Math.Clamp(Stopwatch.GetElapsedTime(_lastStep).TotalSeconds, 0.2, 5);
        _lastStep = Stopwatch.GetTimestamp();

        FanInputs inputs = default;
        if (needCpu || readGpu || live)
        {
            if (_read(needCpu, readGpu, live) is not FanInputs read) return;
            inputs = read;
        }
        if (readGpu) _lastGpu = inputs.GpuC;

        var settings = _settings();
        foreach (var v in channels)
        {
            try { v.EngineLive = FanCurves.Step(v, settings, inputs.CpuC, _lastGpu, dt); }
            catch (Exception ex) { AppLog.Error("fans", ex); } // uma saída com erro não impede as outras
        }
    }
}
