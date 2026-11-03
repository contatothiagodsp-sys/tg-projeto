using System.Diagnostics.Eventing.Reader;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Core.Oc;

/// <summary>
/// Avisa no instante em que o Windows registra um erro WHEA ou um reinício do driver de vídeo (TDR), sem varrer o registro de eventos.
/// Se a assinatura falhar, Count passa a ler o registro a cada consulta; se a leitura também falhar, devolve int.MaxValue, porque
/// quem não consegue vigiar não pode manter o ajuste.
/// </summary>
public sealed class HardwareErrorMonitor : IDisposable
{
    public static readonly string Query = $"*[System[{OcCheck.WheaSelector} or {OcCheck.GpuResetSelector}]]";

    private readonly DateTime _since = DateTime.Now;
    private readonly object _gate = new();
    private EventLogWatcher? _watcher;
    private int _count;
    private bool _disposed;

    /// <param name="subscribe">False só nos testes: nada de assinar o registro do PC que roda o teste.</param>
    public HardwareErrorMonitor(bool subscribe = true)
    {
        if (!subscribe) return;
        try
        {
            _watcher = new EventLogWatcher(new EventLogQuery("System", PathType.LogName, Query));
            _watcher.EventRecordWritten += OnEvent;
            _watcher.Enabled = true;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error("oc", ex);
            ReleaseWatcher();
        }
    }

    /// <summary>A assinatura está valendo: os erros chegam na hora, por ErrorDetected.</summary>
    public bool Subscribed => _watcher != null;

    /// <summary>Chega fora da thread da janela, com a origem e o id do evento.</summary>
    public event Action<string>? ErrorDetected;

    /// <summary>Erros desde a criação do monitor. É a segunda linha de defesa do vigia, conferida a cada leitura.</summary>
    public int Count
    {
        get
        {
            if (Subscribed || Volatile.Read(ref _disposed)) return Volatile.Read(ref _count);
            var read = OcCheck.ErrorsSince(_since);
            return read.Error != null ? int.MaxValue : read.Total;
        }
    }

    /// <summary>Roda na thread do registro de eventos: só lê a origem e avisa. Quem assina não pode fazer trabalho demorado aqui.</summary>
    private void OnEvent(object? sender, EventRecordWrittenEventArgs e)
    {
        using var record = e.EventRecord;
        if (e.EventException != null) { Report("Falha ao ler o registro de eventos: " + e.EventException.Message); return; }
        if (record != null) Report($"{record.ProviderName} {record.Id}");
    }

    /// <summary>Também é a entrada dos testes.</summary>
    internal void Report(string detail)
    {
        if (Volatile.Read(ref _disposed)) return;
        Interlocked.Increment(ref _count);
        // Um assinante com erro não pode impedir o outro de desfazer o ajuste, nem subir para a thread do registro de eventos.
        foreach (var handler in ErrorDetected?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { ((Action<string>)handler)(detail); }
            catch (Exception ex) { AppLog.Error("oc", ex); }
    }

    private void ReleaseWatcher()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher == null) return;
        try { watcher.Enabled = false; }
        catch (Exception ex) when (ex is EventLogException or InvalidOperationException or UnauthorizedAccessException) { AppLog.Error("oc", ex); }
        watcher.EventRecordWritten -= OnEvent;
        watcher.Dispose(); // espera o callback em andamento
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Volatile.Write(ref _disposed, true);
            ReleaseWatcher();
            ErrorDetected = null;
        }
    }
}
