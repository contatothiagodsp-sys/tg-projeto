using ThgOptimization.Core.Intelligence;

namespace ThgOptimization.Tests;

/// <summary>Sessões sintéticas para a lógica do tHg Intelligence. Nenhum teste daqui depende de GPU, sensor ou jogo real.</summary>
internal static class IntelSim
{
    public static readonly DateTime T0 = new(2026, 11, 1, 20, 0, 0);

    public static SessionContext Context(string game = "Jogo", string driver = "32.0.1", string resolution = "2560x1440@144", string hardware = "CPU + GPU + 32 GB", DateTime? start = null) =>
        new(game, "Steam:1", start ?? T0) { GpuDriver = driver, Resolution = resolution, HardwareId = hardware, OnBattery = false };

    /// <summary>Um ponto por segundo. O padrão é um PC saudável: GPU e CPU com folga, temperaturas baixas, 100 FPS.</summary>
    public static TelemetryPoint Healthy(int second) => new(second)
    {
        Fps = 100, CpuLoad = 35, CpuMaxCoreLoad = 55, CpuClockMhz = 5000, CpuTemp = 65, CpuPowerW = 80,
        GpuLoad = 70, GpuClockMhz = 2000, GpuTemp = 62, GpuPowerW = 180, VramPct = 55, RamPct = 50, CommitPct = 45,
    };

    public static SessionTelemetry Session(double minutes, Func<int, TelemetryPoint, TelemetryPoint>? shape = null, IReadOnlyList<float>? frames = null,
                                           ExternalEvidence? external = null, SessionContext? context = null)
    {
        int n = (int)(minutes * 60);
        var points = new List<TelemetryPoint>(n);
        for (int i = 0; i < n; i++) points.Add(shape == null ? Healthy(i) : shape(i, Healthy(i)));
        return new SessionTelemetry(context ?? Context(), points, frames, external);
    }

    /// <summary>Frames de 10 ms (100 FPS) com picos de spikeMs nos segundos indicados; devolve os frames e marca os pontos.</summary>
    public static (float[] Frames, Func<int, TelemetryPoint, TelemetryPoint> Mark) Frames(double minutes, IEnumerable<int> spikeSeconds, float spikeMs = 80)
    {
        var spikes = spikeSeconds.ToHashSet();
        int seconds = (int)(minutes * 60);
        var frames = new List<float>(seconds * 100);
        for (int s = 0; s < seconds; s++)
        {
            int normal = spikes.Contains(s) ? 92 : 100; // o pico ocupa o tempo de 8 frames
            for (int k = 0; k < normal; k++) frames.Add(10f);
            if (spikes.Contains(s)) frames.Add(spikeMs);
        }
        return (frames.ToArray(), (i, p) => spikes.Contains(i) ? p with { Stutters = 1, WorstFrameMs = spikeMs } : p with { WorstFrameMs = 10 });
    }

    public static DiagnosisReport Diagnose(SessionTelemetry s, DateTime? now = null) => new DiagnosisEngine(now: () => now ?? T0.AddHours(3)).Diagnose(s);

    public static Finding? Find(this DiagnosisReport r, DiagnosisKind kind) => r.Findings.FirstOrDefault(f => f.Kind == kind);
}

public class IntelligenceDiagnosisTests
{
    [Fact]
    public void Short_session_is_inconclusive_and_says_why()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(1, (_, p) => p with { GpuLoad = 99 }));
        Assert.Single(r.Findings);
        Assert.Equal(DiagnosisKind.Inconclusive, r.Findings[0].Kind);
        Assert.Contains("curta", r.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gpu_at_the_limit_most_of_the_session_is_gpu_bound_and_not_a_problem()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(15, (_, p) => p with { GpuLoad = 98 }));
        var f = r.Find(DiagnosisKind.GpuBound);
        Assert.NotNull(f);
        Assert.False(f!.IsProblem);
        Assert.Equal(ConfidenceLevel.High, f.Confidence.Level);
        Assert.Null(r.Find(DiagnosisKind.CpuBound));
    }

    [Fact]
    public void Gpu_bound_confidence_grows_with_collection_time()
    {
        double Score(double minutes) => IntelSim.Diagnose(IntelSim.Session(minutes, (_, p) => p with { GpuLoad = 98 })).Find(DiagnosisKind.GpuBound)!.Confidence.Score;
        Assert.True(Score(3) < Score(6));
        Assert.True(Score(6) < Score(15));
        Assert.True(Score(15) <= 0.9); // sem GPU Busy por frame, a confiança tem teto
    }

    [Fact]
    public void Gpu_at_99_for_a_third_of_the_time_is_not_gpu_bound()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(15, (i, p) => p with { GpuLoad = i % 3 == 0 ? 99 : 60 }));
        Assert.Null(r.Find(DiagnosisKind.GpuBound));
    }

    [Fact]
    public void Whole_cpu_busy_with_gpu_idle_is_cpu_bound()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(15, (_, p) => p with { CpuLoad = 93, CpuMaxCoreLoad = 99, GpuLoad = 55 }));
        Assert.NotNull(r.Find(DiagnosisKind.CpuBound));
        Assert.Null(r.Find(DiagnosisKind.MainThreadBound));
    }

    [Fact]
    public void One_saturated_core_with_low_total_cpu_is_main_thread_bound()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(15, (_, p) => p with { CpuLoad = 40, CpuMaxCoreLoad = 97, GpuLoad = 55 }));
        var f = r.Find(DiagnosisKind.MainThreadBound);
        Assert.NotNull(f);
        Assert.Contains("40%", f!.Interpretation);
        Assert.Null(r.Find(DiagnosisKind.CpuBound));
    }

    [Fact]
    public void Without_per_core_load_the_main_thread_diagnosis_is_not_invented()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(15, (_, p) => p with { CpuLoad = 40, CpuMaxCoreLoad = null, GpuLoad = 55 }));
        Assert.Null(r.Find(DiagnosisKind.MainThreadBound));
        Assert.Contains(r.Limitations, l => l.Contains("uso por núcleo"));
    }

    [Fact]
    public void Stable_fps_at_a_common_cap_with_idle_hardware_is_a_cap_not_a_bottleneck()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(15, (_, p) => p with { Fps = 60, GpuLoad = 45, CpuLoad = 25, CpuMaxCoreLoad = 40 }));
        Assert.DoesNotContain(r.Findings, f => f.Kind is DiagnosisKind.GpuBound or DiagnosisKind.CpuBound or DiagnosisKind.MainThreadBound);
        Assert.Contains(r.RuledOut, x => x.Contains("trava", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Session_with_only_fps_does_not_crash_and_declares_what_is_missing()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(15, (i, _) => new TelemetryPoint(i) { Fps = 90 }));
        Assert.DoesNotContain(r.Findings, f => f.IsProblem && f.Kind != DiagnosisKind.Inconclusive);
        Assert.Contains(r.Limitations, l => l.Contains("uso da GPU") && l.Contains("temperatura da CPU"));
    }

    [Fact]
    public void Empty_session_does_not_crash()
    {
        var r = IntelSim.Diagnose(new SessionTelemetry(IntelSim.Context(), Array.Empty<TelemetryPoint>()));
        Assert.Equal(DiagnosisKind.Inconclusive, r.Top!.Kind);
    }

    // ---------------------------------------------------------------- térmico e energia

    [Fact]
    public void Clock_drop_when_hot_with_same_load_is_thermal_limitation()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(20, (i, p) => i < 600 ? p with { CpuTemp = 72, CpuClockMhz = 5000, CpuLoad = 80 }
                                                                     : p with { CpuTemp = 96, CpuClockMhz = 4300, CpuLoad = 80 }));
        var f = r.Find(DiagnosisKind.CpuTemperature);
        Assert.NotNull(f);
        Assert.Equal(Severity.Problem, f!.Severity);
        Assert.True(f.Confidence.Level >= ConfidenceLevel.Medium);
        Assert.Contains("clock", f.Interpretation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void High_temperature_without_clock_drop_is_not_called_throttling()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(20, (i, p) => p with { CpuTemp = i < 600 ? 72 : 95, CpuClockMhz = 5000, CpuLoad = 80 }));
        Assert.Null(r.Find(DiagnosisKind.CpuTemperature));
        Assert.Contains(r.RuledOut, x => x.Contains("sem limitação comprovada"));
    }

    [Fact]
    public void Always_hot_without_clock_reading_is_only_a_low_confidence_warning()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(20, (_, p) => p with { GpuTemp = 88, GpuClockMhz = null }));
        var f = r.Find(DiagnosisKind.GpuTemperature);
        Assert.NotNull(f);
        Assert.Equal(Severity.Attention, f!.Severity);
        Assert.True(f.Confidence.Score <= 0.55);
        Assert.Contains(f.Limitations, l => l.Contains("clock"));
    }

    [Fact]
    public void Sensor_reported_limit_replaces_the_default_one()
    {
        // 96 °C com limite informado de 105 °C: longe da faixa de limitação.
        var x = new ExternalEvidence { CpuTempLimit = 105 };
        var r = IntelSim.Diagnose(IntelSim.Session(20, (_, p) => p with { CpuTemp = 96 }, external: x));
        Assert.Null(r.Find(DiagnosisKind.CpuTemperature));
    }

    [Fact]
    public void Fps_and_clock_falling_while_temperature_rises_is_progressive_thermal_loss()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(30, (i, p) =>
        {
            double t = i / 1800.0;
            bool late = i > 1500; // depois de 25 min
            return p with { GpuTemp = 60 + 18 * t, GpuClockMhz = late ? 1820 : 2000, Fps = late ? 112 : 140, GpuLoad = 80 };
        }));
        var f = r.Find(DiagnosisKind.ThermalThrottling);
        Assert.NotNull(f);
        Assert.Contains("após aproximadamente", f!.Title);
        Assert.Contains("Não recomendamos alterar clocks", f.Interpretation);
        Assert.Contains(f.Limitations, l => l.Contains("não prova"));
    }

    [Fact]
    public void Power_pinned_at_a_ceiling_with_reduced_clock_and_no_heat_is_a_possible_power_limit()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(20, (i, p) => i % 20 == 0
            ? p with { CpuMaxCoreLoad = 30, CpuClockMhz = 5200, CpuPowerW = 40, CpuTemp = 55 }      // momentos leves mostram o clock que o chip atinge
            : p with { CpuMaxCoreLoad = 96, CpuClockMhz = 4100, CpuPowerW = 65, CpuTemp = 70 }));
        var f = r.Find(DiagnosisKind.PowerLimit);
        Assert.NotNull(f);
        Assert.True(f!.Confidence.Score <= 0.6);
        Assert.Contains("Possível", f.Title);
    }

    [Fact]
    public void Low_clock_alone_is_not_a_power_limit()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(20, (_, p) => p with { CpuMaxCoreLoad = 96, CpuClockMhz = 3600, CpuPowerW = 65 }));
        Assert.Null(r.Find(DiagnosisKind.PowerLimit)); // nunca vimos o chip mais rápido que isso: não há do que estar "abaixo"
    }

    // ---------------------------------------------------------------- memória

    [Fact]
    public void Busy_ram_without_pressure_is_explained_as_normal_use()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(15, (_, p) => p with { RamPct = 82, CommitPct = 60 }));
        Assert.Null(r.Find(DiagnosisKind.MemoryPressure));
        Assert.Contains(r.RuledOut, x => x.Contains("uso normal"));
    }

    [Fact]
    public void Ram_at_the_limit_together_with_the_stutters_is_memory_pressure()
    {
        var spikes = Enumerable.Range(0, 40).Select(k => 300 + k * 7).ToArray();
        var (frames, mark) = IntelSim.Frames(15, spikes);
        var r = IntelSim.Diagnose(IntelSim.Session(15, (i, p) => mark(i, p) with { RamPct = i is >= 295 and <= 600 ? 96 : 55 }, frames));
        var f = r.Find(DiagnosisKind.MemoryPressure);
        Assert.NotNull(f);
        Assert.Equal(Severity.Problem, f!.Severity);
        Assert.Contains(f.Evidence, e => e.Metric.Contains("Engasgos"));
        Assert.Null(r.Find(DiagnosisKind.Inconclusive)); // os engasgos têm uma condição associada
    }

    [Fact]
    public void Ram_at_the_limit_without_stutters_is_attention_not_a_confirmed_problem()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(15, (_, p) => p with { RamPct = 95 }));
        Assert.Equal(Severity.Attention, r.Find(DiagnosisKind.MemoryPressure)!.Severity);
    }

    [Fact]
    public void Full_vram_is_vram_pressure_and_roomy_vram_is_ruled_out()
    {
        Assert.NotNull(IntelSim.Diagnose(IntelSim.Session(15, (_, p) => p with { VramPct = 98 })).Find(DiagnosisKind.VramPressure));
        var ok = IntelSim.Diagnose(IntelSim.Session(15));
        Assert.Null(ok.Find(DiagnosisKind.VramPressure));
        Assert.Contains(ok.RuledOut, x => x.Contains("Memória de vídeo com folga"));
    }

    // ---------------------------------------------------------------- fluidez

    [Fact]
    public void Recurrent_spikes_are_a_frame_pacing_problem_with_unknown_cause_when_nothing_matches()
    {
        var (frames, mark) = IntelSim.Frames(15, Enumerable.Range(0, 60).Select(k => 30 + k * 14));
        var r = IntelSim.Diagnose(IntelSim.Session(15, mark, frames));
        Assert.NotNull(r.Find(DiagnosisKind.FramePacing));
        var unknown = r.Find(DiagnosisKind.Inconclusive);
        Assert.NotNull(unknown);
        Assert.Contains("não identificada", unknown!.Title);
    }

    [Fact]
    public void Smooth_session_has_no_pacing_problem_and_says_so()
    {
        var (frames, mark) = IntelSim.Frames(15, Array.Empty<int>());
        var r = IntelSim.Diagnose(IntelSim.Session(15, mark, frames));
        Assert.Null(r.Find(DiagnosisKind.FramePacing));
        Assert.Contains(r.RuledOut, x => x.Contains("Fluidez consistente"));
        Assert.DoesNotContain(r.Findings, f => f.IsProblem);
    }

    [Fact]
    public void A_single_isolated_spike_is_not_a_permanent_problem()
    {
        var (frames, mark) = IntelSim.Frames(15, new[] { 400 });
        var r = IntelSim.Diagnose(IntelSim.Session(15, mark, frames));
        Assert.Null(r.Find(DiagnosisKind.FramePacing));
    }

    [Fact]
    public void Lows_are_not_computed_from_samples_that_cannot_support_them()
    {
        var few = Enumerable.Repeat(10f, 800).ToArray();
        var r1 = IntelSim.Diagnose(IntelSim.Session(15, frames: few));
        Assert.Null(r1.Frames!.Low1Fps);
        Assert.Null(r1.Frames.Low01Fps);

        var some = Enumerable.Repeat(10f, 5000).ToArray();
        var r2 = IntelSim.Diagnose(IntelSim.Session(15, frames: some));
        Assert.NotNull(r2.Frames!.Low1Fps);
        Assert.Null(r2.Frames.Low01Fps);
        Assert.Contains(r2.Limitations, l => l.Contains("0,1% low"));
    }

    [Fact]
    public void Without_frame_capture_the_frame_summary_is_marked_as_estimated()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(15));
        Assert.Equal(MetricOrigin.Estimated, r.Frames!.Origin);
        Assert.Null(r.Frames.Low1Fps);
        Assert.Contains(r.Limitations, l => l.Contains("Sem gravação de frames"));
    }

    // ---------------------------------------------------------------- evidência externa

    [Fact]
    public void Background_programs_that_held_the_cpu_are_reported_without_closing_anything()
    {
        var x = new ExternalEvidence { BackgroundHogs = new[] { new BackgroundHog("chrome", 12), new BackgroundHog("updater", 3) } };
        var f = IntelSim.Diagnose(IntelSim.Session(15, external: x)).Find(DiagnosisKind.ConcurrentProcesses);
        Assert.NotNull(f);
        Assert.Contains("chrome", f!.Interpretation);
        Assert.Contains("não encerra", f.Interpretation);
    }

    [Fact]
    public void Fresh_high_dpc_enters_the_diagnosis_only_together_with_stutters()
    {
        var x = new ExternalEvidence { DpcMaxMicroseconds = 2400, DpcMeasuredAt = IntelSim.T0.AddDays(-2), DpcWorstDriver = "ndis.sys" };
        Assert.Null(IntelSim.Diagnose(IntelSim.Session(15, external: x)).Find(DiagnosisKind.DpcIsr));

        var (frames, mark) = IntelSim.Frames(15, Enumerable.Range(0, 60).Select(k => 30 + k * 14));
        var f = IntelSim.Diagnose(IntelSim.Session(15, mark, frames, x)).Find(DiagnosisKind.DpcIsr);
        Assert.NotNull(f);
        Assert.Contains("ndis.sys", f!.Interpretation);
        Assert.True(f.Confidence.Score <= 0.6);
    }

    [Fact]
    public void Stale_dpc_measurement_is_declared_as_a_limitation_not_used()
    {
        var x = new ExternalEvidence { DpcMaxMicroseconds = 2400, DpcMeasuredAt = IntelSim.T0.AddDays(-90) };
        var (frames, mark) = IntelSim.Frames(15, Enumerable.Range(0, 60).Select(k => 30 + k * 14));
        var r = IntelSim.Diagnose(IntelSim.Session(15, mark, frames, x));
        Assert.Null(r.Find(DiagnosisKind.DpcIsr));
        Assert.Contains(r.Limitations, l => l.Contains("velha demais"));
    }

    [Fact]
    public void Game_on_a_mechanical_disk_matters_only_when_there_are_stutters()
    {
        var x = new ExternalEvidence { GameOnHdd = true };
        Assert.Null(IntelSim.Diagnose(IntelSim.Session(15, external: x)).Find(DiagnosisKind.Storage));
        var (frames, mark) = IntelSim.Frames(15, Enumerable.Range(0, 60).Select(k => 30 + k * 14));
        Assert.NotNull(IntelSim.Diagnose(IntelSim.Session(15, mark, frames, x)).Find(DiagnosisKind.Storage));
    }

    [Fact]
    public void Hardware_error_is_reported_even_in_a_short_session()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(1, (i, p) => p with { HardwareError = i == 20 }));
        var f = r.Find(DiagnosisKind.Stability);
        Assert.NotNull(f);
        Assert.Equal(ConfidenceLevel.High, f!.Confidence.Level);
        Assert.Equal(DiagnosisKind.Stability, r.Top!.Kind);
    }

    // ---------------------------------------------------------------- rede

    [Fact]
    public void Packet_loss_is_a_network_finding_that_does_not_blame_fps()
    {
        var r = IntelSim.Diagnose(IntelSim.Session(15, (i, p) => i % 5 != 0 ? p : i % 50 == 0 ? p with { NetLost = true } : p with { NetLatencyMs = 22, NetLost = false }));
        var f = r.Find(DiagnosisKind.Network);
        Assert.NotNull(f);
        Assert.Contains("não derruba o FPS", f!.Interpretation);
        Assert.Contains(r.Limitations, l => l.Contains("não o servidor do jogo"));
    }

    [Fact]
    public void Stable_connection_is_ruled_out_and_few_probes_say_nothing()
    {
        var stable = IntelSim.Diagnose(IntelSim.Session(15, (i, p) => i % 5 == 0 ? p with { NetLatencyMs = 20 + i % 3, NetLost = false } : p));
        Assert.Null(stable.Find(DiagnosisKind.Network));
        Assert.Contains(stable.RuledOut, x => x.StartsWith("Rede estável"));

        var few = IntelSim.Diagnose(IntelSim.Session(15, (i, p) => i < 10 ? p with { NetLost = true } : p));
        Assert.Null(few.Find(DiagnosisKind.Network));
        Assert.DoesNotContain(few.RuledOut, x => x.StartsWith("Rede"));
    }

    [Fact]
    public void Jitter_alone_is_detected()
    {
        var q = NetworkQuality.Analyze(Enumerable.Range(0, 100).Select(i => new TelemetryPoint(i) { NetLatencyMs = i % 2 == 0 ? 20 : 75, NetLost = false }).ToList());
        Assert.NotNull(q);
        Assert.True(q!.JitterMs >= 50);
        Assert.True(q.IsProblem(new DiagnosisThresholds()));
        Assert.Equal(0, q.LossPct);
    }

    // ---------------------------------------------------------------- qualidade e confiança

    [Fact]
    public void Quality_measures_coverage_and_time()
    {
        var q = TelemetryQuality.Of(IntelSim.Session(10, (i, p) => p with { GpuTemp = i % 4 == 0 ? 60 : null }));
        Assert.Equal(1, q.CoverageOf(Metric.Fps), 3);
        Assert.Equal(0.25, q.CoverageOf(Metric.GpuTemp), 2);
        Assert.False(q.Has(Metric.GpuTemp));
        Assert.Equal(1, q.DurationFactor, 3);
        Assert.Contains("temperatura da GPU", q.Missing());
    }

    [Fact]
    public void Confidence_falls_with_poor_coverage_and_respects_the_cap()
    {
        var full = TelemetryQuality.Of(IntelSim.Session(15));
        var holes = TelemetryQuality.Of(IntelSim.Session(15, (i, p) => p with { GpuLoad = i % 2 == 0 ? 98 : null }));
        var a = Confidence.Build(1, full, new[] { Metric.GpuLoad });
        var b = Confidence.Build(1, holes, new[] { Metric.GpuLoad });
        Assert.True(b.Score < a.Score);
        Assert.True(Confidence.Build(1, full, new[] { Metric.GpuLoad }, cap: 0.6).Score <= 0.6);
        Assert.NotEmpty(a.Reasons);
        Assert.Equal(ConfidenceLevel.Insufficient, new Confidence(0.1, Array.Empty<string>()).Level);
    }

    [Fact]
    public void Healthy_session_yields_no_problem_and_no_recommendation_bait()
    {
        var (frames, mark) = IntelSim.Frames(15, Array.Empty<int>());
        var r = IntelSim.Diagnose(IntelSim.Session(15, mark, frames));
        Assert.Empty(r.Problems);
        Assert.Equal("Nenhum problema identificado nesta sessão.", r.Headline);
        Assert.True(r.RuledOut.Count >= 4);
    }

    // ---------------------------------------------------------------- estatística

    [Fact]
    public void Robust_statistics_ignore_an_outlier()
    {
        var v = new double[] { 100, 101, 99, 100, 102, 98, 500 };
        Assert.Equal(100, RobustStats.Median(v));
        Assert.True(RobustStats.Mad(v) < 3);
        Assert.True(double.IsNaN(RobustStats.Median(Array.Empty<double>())));
        Assert.True(double.IsNaN(RobustStats.NoisePct(new double[] { 1, 2 })));
    }

    [Fact]
    public void Theil_sen_trend_survives_a_spike()
    {
        var x = Enumerable.Range(0, 50).Select(i => (double)i).ToList();
        var y = x.Select(i => 2 * i + 10).ToList();
        y[25] = 9999;
        Assert.Equal(2, RobustStats.TheilSenSlope(x, y), 2);
    }

    [Fact]
    public void Bootstrap_is_deterministic_and_separates_a_clear_shift_from_nothing()
    {
        var a = new double[] { 100, 101, 99, 100, 102 };
        var b = new double[] { 120, 121, 119, 122, 120 };
        var shift1 = RobustStats.BootstrapMedianDeltaPct(a, b);
        var shift2 = RobustStats.BootstrapMedianDeltaPct(a, b);
        Assert.Equal(shift1, shift2);
        Assert.True(shift1!.Value.ExcludesZero);
        Assert.False(RobustStats.BootstrapMedianDeltaPct(a, a)!.Value.ExcludesZero);
        Assert.Null(RobustStats.BootstrapMedianDeltaPct(new double[] { 1 }, b));
    }

    [Fact]
    public void Spearman_detects_monotonic_relation_and_refuses_tiny_samples()
    {
        var x = Enumerable.Range(1, 30).Select(i => (double)i).ToList();
        Assert.Equal(1, RobustStats.Spearman(x, x.Select(v => v * v).ToList()), 3);
        Assert.True(double.IsNaN(RobustStats.Spearman(new double[] { 1, 2 }, new double[] { 2, 1 })));
    }

    [Fact]
    public void Machine_profile_never_turns_a_missing_field_into_zero()
    {
        Assert.Equal(ReadingState.Unavailable, Capability.Of("VRAM", "").State);
        Assert.Equal(ReadingState.Unavailable, Capability.Of("VRAM", "—").State);
        Assert.Equal(ReadingState.Supported, Capability.Of("VRAM", "16 GB").State);
        Assert.Contains("não suportado", Capability.Unsupported("GPU Busy", "exige PresentMon").Display);
        var m = new MachineProfile(IntelSim.T0, MachineProfile.MakeHardwareId("Ryzen  7", "RX 9070", 31.7), Array.Empty<CapabilityGroup>())
        {
            Sensors = new[] { Capability.Of("Temperatura da CPU", "65 °C"), Capability.Unsupported("GPU Busy", "exige PresentMon") },
        };
        Assert.Equal("Ryzen 7 + RX 9070 + 32 GB", m.HardwareId);
        Assert.Single(m.Blind());
    }
}
