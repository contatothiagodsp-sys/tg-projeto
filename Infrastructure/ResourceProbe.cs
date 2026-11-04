using System.Diagnostics;

namespace ThgOptimization.Infrastructure;

/// <summary>O que o próprio app gastou durante uma medição. CpuPercent é sobre todos os núcleos juntos, como no Gerenciador de Tarefas.</summary>
public readonly record struct ResourceMeasurement(long PeakWorkingSetMb, long AverageWorkingSetMb, double CpuPercent, TimeSpan Duration)
{
    public string Summary =>
        $"Medido em {Duration.TotalSeconds:0} s: pico {PeakWorkingSetMb} MB · média {AverageWorkingSetMb} MB · CPU média {CpuPercent:0.0} %. Metas: repouso até 80 MB, jogo até 150 MB, pico até 400 MB.";
}

/// <summary>Medição sob demanda, pelo botão em Configurações. Fora dela, nada aqui roda.</summary>
public static class ResourceProbe
{
    /// <summary>Uso de CPU sobre todos os núcleos, de 0 a 100. Intervalo vazio ou sem núcleos (leitura inválida) vale 0.</summary>
    internal static double CpuPercent(TimeSpan cpuUsed, TimeSpan elapsed, int logicalProcessors)
    {
        if (elapsed <= TimeSpan.Zero || logicalProcessors <= 0 || cpuUsed < TimeSpan.Zero) return 0;
        return Math.Clamp(cpuUsed.TotalMilliseconds / (elapsed.TotalMilliseconds * logicalProcessors) * 100, 0, 100);
    }

    public static async Task<ResourceMeasurement> MeasureAsync(TimeSpan duration, TimeSpan interval, CancellationToken ct = default)
    {
        const long Mb = 1024 * 1024;
        using var process = Process.GetCurrentProcess();
        var clock = Stopwatch.StartNew();
        TimeSpan cpuStart = process.TotalProcessorTime;
        long peak = 0, sum = 0;
        int samples = 0;
        do
        {
            process.Refresh();
            long now = process.WorkingSet64;
            peak = Math.Max(peak, now);
            sum += now;
            samples++;
            await Task.Delay(interval, ct);
        }
        while (clock.Elapsed < duration);
        process.Refresh();
        double cpu = CpuPercent(process.TotalProcessorTime - cpuStart, clock.Elapsed, Environment.ProcessorCount);
        return new ResourceMeasurement(peak / Mb, sum / samples / Mb, cpu, clock.Elapsed);
    }
}
