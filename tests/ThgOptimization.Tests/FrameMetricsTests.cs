using System.Diagnostics;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

public class ProcessNameOfTests
{
    [Fact]
    public void Nomes_iguais_aos_de_GetProcesses()
    {
        int compared = 0;
        var different = new List<string>();
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                // Pode ter fechado depois da lista: null não é divergência, nome diferente é.
                if (ProcessUtil.NameOf(p.Id) is not { } name) continue;
                compared++;
                // Memory Compression é um processo do kernel, sem .exe: a classe 88 devolve "MemCompression".
                if (name != p.ProcessName && p.ProcessName != "Memory Compression") different.Add($"{p.Id}: {p.ProcessName} / {name}");
            }
        Assert.True(compared > 10, $"só {compared} processos comparados");
        Assert.Empty(different);
    }

    [Fact]
    public void Le_o_nome_de_processo_que_nao_pode_ser_aberto()
    {
        var csrss = Process.GetProcessesByName("csrss");
        int pid = csrss[0].Id;
        foreach (var p in csrss) p.Dispose();
        Assert.Equal("csrss", ProcessUtil.NameOf(pid));
    }

    [Fact]
    public void Processo_inexistente_devolve_null()
    {
        var alive = ProcessUtil.Snapshot().Select(s => s.Pid).ToHashSet();
        int pid = 0x7FFFFFF0; // PIDs reais são múltiplos de 4 e bem menores
        Assert.DoesNotContain(pid, alive);
        Assert.Null(ProcessUtil.NameOf(pid));
    }
}

public class FrameCaptureCapacityTests
{
    private const int Max = 3_000_000;

    [Fact]
    public void Sessao_do_Modo_Jogo_reserva_o_limite_inteiro()
    {
        using var m = new FrameMonitor();
        int id = m.StartCapture(1000, TimeSpan.FromHours(3), 300);
        Assert.True(m.CaptureCapacity(id) >= Max);
        m.StopCapture(id);
    }

    [Fact]
    public void Gravacao_de_60_s_reserva_com_folga()
    {
        using var m = new FrameMonitor();
        int id = m.StartCapture(1000, TimeSpan.FromSeconds(60), 240);
        Assert.Equal(21_600, m.CaptureCapacity(id));
    }

    [Fact]
    public void Duracao_curta_fica_no_minimo()
    {
        using var m = new FrameMonitor();
        int id = m.StartCapture(1000, TimeSpan.FromSeconds(1));
        Assert.Equal(16_384, m.CaptureCapacity(id));
    }
}

public class FramePresenterFilterTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    public void Sistema_e_ignorado(int pid) => Assert.True(FrameMonitor.IsIgnoredPresenter(pid));

    [Fact]
    public void O_proprio_tHg_e_ignorado() => Assert.True(FrameMonitor.IsIgnoredPresenter(Environment.ProcessId));

    [Fact]
    public void Um_jogo_passa() => Assert.False(FrameMonitor.IsIgnoredPresenter(Environment.ProcessId + 4));
}

public class FrameAnalysisTests
{
    /// <summary>O algoritmo de antes da troca por Array.Sort: os resultados precisam continuar iguais.</summary>
    private static CaptureResult Reference(string game, DateTime start, List<double> times)
    {
        var ft = new double[times.Count - 1];
        for (int i = 1; i < times.Count; i++) ft[i - 1] = times[i] - times[i - 1];
        double seconds = (times[^1] - times[0]) / 1000.0;
        var sorted = ft.OrderBy(x => x).ToArray();
        double Pct(double p) => sorted[Math.Clamp((int)Math.Floor(sorted.Length * p), 0, sorted.Length - 1)];
        double LowAvg(double fraction)
        {
            int n = Math.Max(1, (int)Math.Ceiling(sorted.Length * fraction));
            double mean = sorted.Skip(sorted.Length - n).Average();
            return mean > 0 ? 1000 / mean : 0;
        }
        double median = Pct(0.5);
        int stutters = ft.Count(x => x > median * 2.5 && x > 25);
        return new CaptureResult(game, start, Math.Round(seconds, 1), ft.Length, ft.Length / Math.Max(0.001, seconds),
                                 LowAvg(0.01), LowAvg(0.001), Pct(0.99), sorted[^1], stutters);
    }

    private static List<double> Times(int seed, int frames, double spikeChance)
    {
        var rng = new Random(seed);
        var times = new List<double>(frames) { 0 };
        for (int i = 1; i < frames; i++)
            times.Add(times[^1] + (rng.NextDouble() < spikeChance ? 30 + rng.NextDouble() * 200 : 4 + rng.NextDouble() * 6));
        return times;
    }

    [Theory]
    [InlineData(1, 3, 0.0)]
    [InlineData(2, 4, 0.5)]
    [InlineData(3, 1_000, 0.0)]
    [InlineData(4, 1_000, 0.02)]
    [InlineData(5, 100_000, 0.001)]
    public void Resultados_iguais_aos_do_algoritmo_anterior(int seed, int frames, double spikes)
    {
        var times = Times(seed, frames, spikes);
        var start = new DateTime(2026, 1, 1);
        var expected = Reference("jogo", start, times);
        var actual = FrameAnalysis.Analyze("jogo", start, times);
        Assert.Equal(expected.Seconds, actual.Seconds);
        Assert.Equal(expected.Frames, actual.Frames);
        Assert.Equal(expected.AvgFps, actual.AvgFps);
        Assert.Equal(expected.P99Ms, actual.P99Ms);
        Assert.Equal(expected.MaxMs, actual.MaxMs);
        Assert.Equal(expected.Stutters, actual.Stutters);
        Assert.Equal(expected.Low1Fps, actual.Low1Fps, 9);
        Assert.Equal(expected.Low01Fps, actual.Low01Fps, 9);
    }

    [Fact]
    public void Nao_altera_a_lista_de_instantes()
    {
        // A lista é usada depois para salvar o CSV frame a frame, na ordem.
        var times = Times(6, 500, 0.05);
        var copy = times.ToList();
        FrameAnalysis.Analyze("jogo", DateTime.Now, times);
        Assert.Equal(copy, times);
    }

    [Fact]
    public void Poucos_frames_nao_quebra()
    {
        var r = FrameAnalysis.Analyze("jogo", DateTime.Now, new List<double> { 0, 10 });
        Assert.Equal(2, r.Frames);
        Assert.Equal(0, r.AvgFps);
    }
}
