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
    private EventLogWatcher? _watcher;
    private int _count;

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
            _watcher?.Dispose();
            _watcher = null;
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
            if (Subscribed) return Volatile.Read(ref _count);
            var read = OcCheck.ErrorsSince(_since);
            return read.Error != null ? int.MaxValue : read.Total;
        }
    }

    private void OnEvent(object? sender, EventRecordWrittenEventArgs e)
    {
        using var record = e.EventRecord;
        if (e.EventException != null) { Report("Falha ao ler o registro de eventos: " + e.EventException.Message); return; }
        if (record != null) Report($"{record.ProviderName} {record.Id}");
    }

    /// <summary>Também é a entrada dos testes.</summary>
    internal void Report(string detail)
    {
        Interlocked.Increment(ref _count);
        ErrorDetected?.Invoke(detail);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
