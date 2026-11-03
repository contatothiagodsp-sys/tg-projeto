namespace ThgOptimization.Tests;

public class PresentRingTests
{
    [Fact]
    public void Copies_in_order_before_wrapping()
    {
        var ring = new FrameMonitor.PresentRing();
        for (int i = 0; i < 100; i++) ring.Add(i, 0);
        var dst = new double[FrameMonitor.PresentRing.Capacity];
        Assert.Equal(100, ring.CopyLatest(dst));
        Assert.Equal(0, dst[0]);
        Assert.Equal(99, dst[99]);
    }

    [Fact]
    public void Keeps_the_latest_frames_after_wrapping()
    {
        var ring = new FrameMonitor.PresentRing();
        int total = FrameMonitor.PresentRing.Capacity * 3 + 17;
        for (int i = 0; i < total; i++) ring.Add(i, 0);
        var dst = new double[FrameMonitor.PresentRing.Capacity];
        int n = ring.CopyLatest(dst);
        Assert.Equal(FrameMonitor.PresentRing.Capacity - 1, n);
        Assert.Equal(total - 1, dst[n - 1]);
        for (int i = 1; i < n; i++) Assert.Equal(dst[i - 1] + 1, dst[i]);
    }

    /// <summary>O escritor grava valores crescentes sem parar: uma cópia misturada apareceria como valor fora de ordem.</summary>
    [Fact]
    public async Task Concurrent_reads_only_see_consecutive_frames()
    {
        var ring = new FrameMonitor.PresentRing();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var writer = Task.Run(() =>
        {
            double t = 0;
            while (!stop.IsCancellationRequested) ring.Add(t++, 0);
        });
        var dst = new double[FrameMonitor.PresentRing.Capacity];
        int reads = 0;
        while (!stop.IsCancellationRequested)
        {
            int n = ring.CopyLatest(dst);
            for (int i = 1; i < n; i++)
                if (dst[i] != dst[i - 1] + 1) Assert.Fail($"Quebra na posição {i}: {dst[i - 1]} seguido de {dst[i]}");
            reads++;
        }
        await writer;
        Assert.True(reads > 0);
    }
}

public class FrameMonitorPathTests
{
    private const int Pid = 1234;

    private static void Feed(FrameMonitor m, int frames, double ms, int pid = Pid, double start = 0)
    {
        for (int i = 0; i < frames; i++) m.OnPresent(pid, start + i * ms);
    }

    [Fact]
    public void Present_path_does_not_allocate()
    {
        using var m = new FrameMonitor();
        int id = m.StartCapture(Pid, TimeSpan.FromSeconds(1));
        Feed(m, 10, 1); // o buffer do processo nasce no primeiro frame
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) m.OnPresent(Pid, 10 + i);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        m.StopCapture(id);
    }

    [Fact]
    public void Stats_cover_every_metric()
    {
        using var m = new FrameMonitor();
        Feed(m, 1000, 10);
        var s = m.GetStats(Pid);
        Assert.NotNull(s);
        Assert.Equal(100, s!.Fps, 0);
        Assert.Equal(100, s.Low1Fps, 0);
        Assert.Equal(100, s.Detail!.InstantFps, 0);
        Assert.Equal(10, s.Detail.P50Ms, 3);
        Assert.Equal(10, s.Detail.P999Ms, 3);
        Assert.Equal(120, s.RecentFrameTimes.Length);
        Assert.Equal(10, s.RecentFrameTimes.Span[0], 3);
    }

    [Fact]
    public void Recent_frame_times_reuse_the_buffer_instead_of_boxing()
    {
        using var m = new FrameMonitor();
        Feed(m, 1000, 10);
        var first = m.GetStats(Pid)!.RecentFrameTimes;
        var second = m.GetStats(Pid)!.RecentFrameTimes;
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(first, out var a));
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(second, out var b));
        Assert.Same(a.Array, b.Array);
        var kept = second.ToArray(); // quem guarda copia e não muda com a próxima leitura
        Feed(m, 200, 20, Pid, 10_000);
        m.GetStats(Pid);
        Assert.Equal(10, kept[^1], 3);
    }

    [Fact]
    public void Stats_read_allocates_only_its_two_result_objects_and_the_name()
    {
        using var m = new FrameMonitor();
        Feed(m, 1000, 10);
        m.GetStats(Pid); // a primeira leitura guarda o nome e cria o buffer da thread
        long before = GC.GetAllocatedBytesForCurrentThread();
        const int reads = 100;
        for (int i = 0; i < reads; i++) m.GetStats(Pid);
        long perRead = (GC.GetAllocatedBytesForCurrentThread() - before) / reads;
        Assert.True(perRead < StatsReadBudgetBytes, $"GetStats alocou {perRead} bytes por leitura.");
    }

    /// <summary>FrameStats e FrameStatistics são classes: os dois objetos do resultado. O boxing do ArraySegment deixou de contar.</summary>
    private const long StatsReadBudgetBytes = 400;

    [Fact]
    public void Window_of_10_seconds_is_applied_when_reading()
    {
        using var m = new FrameMonitor();
        Feed(m, 100, 100); // 10 s a 10 FPS...
        Feed(m, 2000, 10, start: 30_000); // ...e, bem depois, 20 s a 100 FPS
        var s = m.GetStats(Pid)!;
        Assert.Equal(100, s.Low1Fps, 0); // o trecho lento saiu da janela
    }

    [Fact]
    public void Process_that_stopped_drawing_reads_as_null()
    {
        using var m = new FrameMonitor();
        Assert.Null(m.GetStats(Pid));
        Feed(m, 2, 10);
        Assert.Null(m.GetStats(Pid)); // menos de 3 frames
    }

    [Fact]
    public void Prune_releases_idle_processes_and_a_returning_process_is_measured_again()
    {
        using var m = new FrameMonitor();
        Feed(m, 10, 10);
        Assert.Equal(1, m.TrackedProcesses);
        m.Prune(TimeSpan.Zero);
        Assert.Equal(0, m.TrackedProcesses);
        Feed(m, 10, 10, start: 1000); // o escritor tinha o buffer antigo guardado
        Assert.Equal(1, m.TrackedProcesses);
        Assert.NotNull(m.GetStats(Pid));
    }

    [Fact]
    public void Prune_keeps_a_process_being_captured()
    {
        using var m = new FrameMonitor();
        int id = m.StartCapture(Pid, TimeSpan.FromSeconds(1));
        Feed(m, 10, 10);
        m.Prune(TimeSpan.Zero);
        Feed(m, 10, 10, start: 1000);
        Assert.Equal(20, m.StopCapture(id).Count);
    }

    [Fact]
    public void Capture_keeps_every_frame_in_order_and_only_of_its_process()
    {
        using var m = new FrameMonitor();
        int id = m.StartCapture(Pid, TimeSpan.FromSeconds(1));
        Feed(m, 500, 2);
        Feed(m, 500, 2, pid: Pid + 4);
        var times = m.StopCapture(id);
        Assert.Equal(500, times.Count);
        Assert.Equal(998, times[^1]);
    }

    [Fact]
    public void Two_captures_of_the_same_process_are_independent()
    {
        using var m = new FrameMonitor();
        int a = m.StartCapture(Pid, TimeSpan.FromSeconds(1));
        Feed(m, 10, 1);
        int b = m.StartCapture(Pid, TimeSpan.FromSeconds(1));
        Feed(m, 10, 1, start: 100);
        Assert.Equal(20, m.StopCapture(a).Count);
        Feed(m, 10, 1, start: 200);
        Assert.Equal(20, m.StopCapture(b).Count);
    }

    [Fact]
    public void Full_capture_never_grows_and_reports_what_was_left_out()
    {
        using var m = new FrameMonitor();
        int id = m.StartCapture(Pid, TimeSpan.FromSeconds(1));
        int capacity = m.CaptureCapacity(id);
        Feed(m, capacity + 25, 1);
        Assert.Equal(capacity, m.CaptureCapacity(id));
        var times = m.StopCapture(id, out long dropped);
        Assert.Equal(capacity, times.Count);
        Assert.Equal(25, dropped);
    }

    [Fact]
    public void Capture_uses_the_measured_fps_when_it_is_higher_than_expected()
    {
        using var m = new FrameMonitor();
        Feed(m, 5000, 1); // 1000 FPS
        int id = m.StartCapture(Pid, TimeSpan.FromSeconds(60), 240);
        Assert.True(m.CaptureCapacity(id) >= 60 * 1000);
        m.StopCapture(id);
    }

    [Fact]
    public void Stopping_an_unknown_capture_returns_empty()
    {
        using var m = new FrameMonitor();
        Assert.Empty(m.StopCapture(999));
    }

    [Fact]
    public void Stop_of_a_capture_releases_it_from_the_process()
    {
        using var m = new FrameMonitor();
        int id = m.StartCapture(Pid, TimeSpan.FromSeconds(1));
        m.StopCapture(id);
        Assert.Equal(0, m.CaptureCapacity(id));
        Feed(m, 5, 1); // nada grava num buffer solto nem lança
    }

    [Fact]
    public async Task Stats_reader_does_not_block_the_writer()
    {
        using var m = new FrameMonitor();
        Feed(m, 1000, 1);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        // O escritor daqui gira sem pausa, dezenas de milhões de quadros por segundo. Com o leitor parado pelo escalonador, ele dá a volta inteira
        // no buffer, e o leitor descarta a cópia e devolve null, como deve. Isso não existe em jogo: conta só que as leituras terminam e trazem dados.
        int read = 0;
        var reader = Task.Run(() => { while (!stop.IsCancellationRequested) if (m.GetStats(Pid) != null) read++; });
        double t = 1000;
        while (!stop.IsCancellationRequested) m.OnPresent(Pid, t += 1);
        await reader;
        Assert.True(read > 0);
    }

    [Fact]
    public void Monitor_never_started_reports_off_and_stops_cleanly()
    {
        var m = new FrameMonitor();
        Assert.Equal(FrameCollectionState.Off, m.State);
        m.Stop();
        m.Dispose();
        m.Dispose();
    }
}

public class FrameStatisticsTests
{
    private static double[] Range(int n) => Enumerable.Range(1, n).Select(i => (double)i).ToArray();

    [Theory]
    [InlineData(0.50, 50)]
    [InlineData(0.90, 90)]
    [InlineData(0.95, 95)]
    [InlineData(0.99, 99)]
    [InlineData(1.00, 100)]
    public void Nearest_rank_on_100_values(double p, double expected)
    {
        Assert.Equal(expected, FrameStatistics.Percentile(Range(100), p));
    }

    [Fact]
    public void Percentile_of_a_single_value_and_of_nothing()
    {
        Assert.Equal(7, FrameStatistics.Percentile(new double[] { 7 }, 0.999));
        Assert.Equal(0, FrameStatistics.Percentile(ReadOnlySpan<double>.Empty, 0.5));
    }

    [Fact]
    public void Average_low_and_percentile_low_are_different_things()
    {
        // 1000 frames: 990 de 10 ms e 10 de 100 ms. O P99 é 10 ms (100 FPS); a média do 1% mais lento é 100 ms (10 FPS).
        var ft = Enumerable.Repeat(10.0, 990).Concat(Enumerable.Repeat(100.0, 10)).ToArray();
        var s = FrameStatistics.FromFrameTimes(ft);
        Assert.Equal(100, s.Low1PercentileFps, 3);
        Assert.Equal(10, s.Low1AvgFps, 3);
        Assert.Equal(10, s.Stutters); // acima de 25 ms e de 2,5 × a mediana
    }

    [Fact]
    public void Average_fps_is_frames_over_time_and_not_the_mean_of_fps()
    {
        var s = FrameStatistics.FromFrameTimes(new double[] { 10, 30 }); // média de FPS daria 66,7
        Assert.Equal(50, s.AvgFps, 3);
        Assert.Equal(20, s.AvgFrameTimeMs, 3);
    }

    [Fact]
    public void Histogram_accounts_for_every_frame()
    {
        var s = FrameStatistics.FromFrameTimes(new double[] { 3, 8, 16, 30, 45, 90, 500 });
        Assert.Equal(new[] { 1, 1, 1, 1, 1, 1, 1 }, s.Histogram);
        Assert.Equal(7, s.Histogram.Sum());
    }

    [Fact]
    public void Empty_input_gives_zeros()
    {
        var s = FrameStatistics.FromFrameTimes(Span<double>.Empty);
        Assert.Equal(0, s.Frames);
        Assert.Equal(0, s.StuttersPerMinute);
    }

    [Fact]
    public void Analyze_fills_the_statistics_and_the_diagnosis()
    {
        var times = Enumerable.Range(0, 1000).Select(i => i * 10.0).ToList();
        var r = FrameAnalysis.Analyze("jogo", DateTime.Now, times, droppedFrames: 3);
        Assert.Equal(10, r.Statistics!.P50Ms, 3);
        Assert.Equal(StutterVerdict.None, r.Diagnosis!.Verdict);
        Assert.Contains("3 frames", r.DetailText);
    }

    [Fact]
    public void Old_saved_capture_still_loads()
    {
        const string Json = """{"Game":"x","Start":"2026-01-01T00:00:00","Seconds":1,"Frames":2,"AvgFps":3,"Low1Fps":4,"Low01Fps":5,"P99Ms":6,"MaxMs":7,"Stutters":8}""";
        var c = System.Text.Json.JsonSerializer.Deserialize<CaptureResult>(Json)!;
        Assert.Equal(8, c.Stutters);
        Assert.Null(c.Diagnosis);
        Assert.Equal("", c.DetailText);
    }
}

public class StutterClassifierTests
{
    private static List<double> Times(IEnumerable<double> frameTimes)
    {
        var list = new List<double> { 0 };
        foreach (double ft in frameTimes) list.Add(list[^1] + ft);
        return list;
    }

    private static IEnumerable<double> Steady(int n, double ms = 10) => Enumerable.Repeat(ms, n);

    [Fact]
    public void Steady_session_has_nothing_to_report()
    {
        var r = StutterClassifier.Classify(Times(Steady(5000)));
        Assert.Equal(StutterVerdict.None, r.Verdict);
    }

    [Fact]
    public void One_spike_is_isolated_and_the_cause_stays_unknown()
    {
        var r = StutterClassifier.Classify(Times(Steady(2000).Append(200).Concat(Steady(2000))));
        Assert.Equal(1, r.IsolatedSpikes);
        Assert.Equal(0, r.RecurrentEpisodes);
        Assert.Equal(StutterVerdict.DetectedCauseUnknown, r.Verdict);
        Assert.Contains("CAUSA NÃO IDENTIFICADA", r.Summary);
    }

    [Fact]
    public void Spikes_close_in_time_are_recurrent_and_not_isolated()
    {
        var frames = new List<double>();
        for (int i = 0; i < 5; i++) { frames.AddRange(Steady(100)); frames.Add(100); }
        var r = StutterClassifier.Classify(Times(frames));
        Assert.Equal(1, r.RecurrentEpisodes);
        Assert.Equal(0, r.IsolatedSpikes);
    }

    [Fact]
    public void Spikes_far_apart_do_not_count_as_recurrent()
    {
        var frames = new List<double>();
        for (int i = 0; i < 4; i++) { frames.AddRange(Steady(3000)); frames.Add(100); } // um a cada 30 s
        var r = StutterClassifier.Classify(Times(frames));
        Assert.Equal(0, r.RecurrentEpisodes);
        Assert.Equal(4, r.IsolatedSpikes);
    }

    [Fact]
    public void Alternating_frametimes_are_persistent_oscillation()
    {
        var frames = Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? 5.0 : 20.0);
        Assert.True(StutterClassifier.Classify(Times(frames)).OscillationEpisodes >= 1);
    }

    [Fact]
    public void A_slow_second_counts_as_a_drop()
    {
        var r = StutterClassifier.Classify(Times(Steady(1000).Concat(Steady(50, 22)).Concat(Steady(1000))));
        Assert.True(r.FpsDropSeconds >= 1);
    }

    [Fact]
    public void High_frametime_counts_each_frame_above_the_limit()
    {
        var r = StutterClassifier.Classify(Times(Steady(100, 40)));
        Assert.Equal(100, r.HighFrametimeFrames);
    }

    [Fact]
    public void Low_memory_near_a_spike_is_only_a_possible_cause()
    {
        var times = Times(Steady(2000).Append(200).Concat(Steady(2000)));
        var r = StutterClassifier.Classify(times, memory: new[] { new MemorySample(times[2000], 5) });
        Assert.Equal(StutterVerdict.DetectedPossibleCause, r.Verdict);
        Assert.True(r.PossibleMemoryPressure);
        Assert.Contains("POSSÍVEL CAUSA", r.Summary);
    }

    [Fact]
    public void Low_memory_far_from_any_spike_implies_nothing()
    {
        var times = Times(Steady(2000).Append(200).Concat(Steady(8000)));
        var r = StutterClassifier.Classify(times, memory: new[] { new MemorySample(times[^1], 5) });
        Assert.Equal(StutterVerdict.DetectedCauseUnknown, r.Verdict);
    }

    [Fact]
    public void Low_memory_without_stutter_implies_nothing()
    {
        var times = Times(Steady(2000));
        Assert.Equal(StutterVerdict.None, StutterClassifier.Classify(times, memory: new[] { new MemorySample(0, 1) }).Verdict);
    }

    [Fact]
    public void Criteria_are_configurable()
    {
        var times = Times(Steady(1000).Append(30).Concat(Steady(1000)));
        Assert.Equal(StutterVerdict.DetectedCauseUnknown, StutterClassifier.Classify(times).Verdict);
        Assert.Equal(0, StutterClassifier.Classify(times, new StutterCriteria(SpikeMinMs: 50)).Spikes);
    }

    [Fact]
    public void Too_few_frames_do_not_throw()
    {
        Assert.Equal(StutterVerdict.None, StutterClassifier.Classify(new List<double> { 0, 1 }).Verdict);
    }
}
