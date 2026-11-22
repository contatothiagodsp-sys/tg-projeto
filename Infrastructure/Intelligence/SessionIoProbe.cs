using System.Diagnostics;

namespace ThgOptimization.Infrastructure.Intelligence;

/// <summary>
/// Mede atividade de disco (MB/s) e leituras de página em disco (hard page faults/s) durante a sessão de jogo,
/// usando os contadores nativos do Windows sem instalar serviços externos. Nunca lança se o contador estiver indisponível.
/// </summary>
public sealed class SessionIoProbe : IDisposable
{
    private const double BytesPerMb = 1024.0 * 1024.0;
    private readonly object _gate = new();
    private PerformanceCounter? _diskBytes;
    private PerformanceCounter? _pageReads;
    private bool _initStarted, _warmedUp, _disposed;
    private long _lastTicks;

    public double? LastDiskMbPerSec { get; private set; }
    public double? LastPageFaultsPerSec { get; private set; }

    public SessionIoProbe()
    {
        StartInit();
    }

    private void StartInit()
    {
        lock (_gate)
        {
            if (_initStarted || _disposed) return;
            _initStarted = true;
        }
        _ = Task.Run(() =>
        {
            PerformanceCounter? disk = null;
            PerformanceCounter? page = null;
            try
            {
                disk = new PerformanceCounter("PhysicalDisk", "Disk Bytes/sec", "_Total", readOnly: true);
                _ = disk.NextValue();
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                disk?.Dispose();
                disk = null;
                AppLog.Debug("io-probe", ex);
            }
            try
            {
                page = new PerformanceCounter("Memory", "Page Reads/sec", readOnly: true);
                _ = page.NextValue();
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                page?.Dispose();
                page = null;
                AppLog.Debug("io-probe", ex);
            }
            lock (_gate)
            {
                if (_disposed)
                {
                    disk?.Dispose();
                    page?.Dispose();
                    return;
                }
                _diskBytes = disk;
                _pageReads = page;
                _lastTicks = Stopwatch.GetTimestamp();
            }
        });
    }

    /// <summary>Lê a amostra atual (limitada a no máximo 1 leitura a cada 800 ms). A primeira leitura útil descarta o aquecimento do contador.</summary>
    public (double? DiskMbPerSec, double? PageFaultsPerSec) Sample()
    {
        lock (_gate)
        {
            if (_disposed || (_diskBytes == null && _pageReads == null)) return (null, null);
            long now = Stopwatch.GetTimestamp();
            if (_lastTicks != 0 && Stopwatch.GetElapsedTime(_lastTicks, now).TotalMilliseconds < 800)
                return (LastDiskMbPerSec, LastPageFaultsPerSec);
            _lastTicks = now;

            double? diskMb = Read(_diskBytes, scale: 1.0 / BytesPerMb);
            double? faults = Read(_pageReads, scale: 1.0);
            if (!_warmedUp)
            {
                _warmedUp = true;
                return (null, null);
            }
            LastDiskMbPerSec = diskMb;
            LastPageFaultsPerSec = faults;
            return (diskMb, faults);
        }
    }

    private static double? Read(PerformanceCounter? counter, double scale)
    {
        if (counter == null) return null;
        try
        {
            double v = counter.NextValue() * scale;
            return double.IsFinite(v) && v >= 0 ? Math.Round(v, 2) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _diskBytes?.Dispose();
            _pageReads?.Dispose();
            _diskBytes = null;
            _pageReads = null;
        }
    }
}
