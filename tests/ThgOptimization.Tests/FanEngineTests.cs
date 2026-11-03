using System.Windows.Threading;

namespace ThgOptimization.Tests;

public class FanEngineTests
{
    private sealed class Reads
    {
        public int Count;
        public bool LastCpu, LastGpu, LastBoard;
        public FanInputs? Answer = new FanInputs(50, 60);
        public FanInputs? Read(bool cpu, bool gpu, bool board)
        {
            Count++;
            (LastCpu, LastGpu, LastBoard) = (cpu, gpu, board);
            return Answer;
        }
    }

    private static FanChannelView View(string preset, string source, Action<float>? set = null) =>
        new(new FanOutput("id", "chip", "fan", set ?? (_ => { }), () => { }, () => "1000 RPM"),
            new FanChannelSettings { Preset = preset, Source = source, Custom = "30:20, 70:80" });

    private static FanEngine Engine(Reads r, bool gpuPaused = false) =>
        new(r.Read, () => new FanSettings(), () => gpuPaused);

    [Fact]
    public void Bios_channels_with_the_page_closed_do_not_touch_the_hardware()
    {
        var r = new Reads();
        var e = Engine(r);
        e.SetChannels(new[] { View("bios", "cpu") });
        e.Step();
        Assert.Equal(0, r.Count);
    }

    [Fact]
    public void Curve_reads_only_what_it_needs()
    {
        var r = new Reads();
        var e = Engine(r);
        e.SetChannels(new[] { View("custom", "cpu") });
        e.Step();
        Assert.True(r.LastCpu);
        Assert.False(r.LastGpu);
        Assert.False(r.LastBoard); // a curva não usa a placa-mãe: com a página fechada, nada de Super I/O no jogo
    }

    [Fact]
    public void Open_page_also_reads_the_motherboard()
    {
        var r = new Reads();
        var e = Engine(r);
        e.SetChannels(new[] { View("bios", "cpu") });
        e.LiveWanted = true;
        e.Step();
        Assert.True(r.LastBoard);
    }

    [Fact]
    public void Gpu_curve_keeps_the_last_reading_during_a_gpu_test()
    {
        var r = new Reads();
        bool paused = false;
        var e = new FanEngine(r.Read, () => new FanSettings(), () => paused);
        var v = View("custom", "gpu");
        e.SetChannels(new[] { v });
        e.Step();
        Assert.True(r.LastGpu);
        paused = true;
        r.Answer = new FanInputs(50, null);
        e.Step();
        Assert.False(r.LastGpu);
        Assert.Contains("60 °C", v.EngineLive);
    }

    [Fact]
    public void Closed_sensors_skip_the_round_without_full_speed()
    {
        var r = new Reads { Answer = null };
        var e = Engine(r);
        float? applied = null;
        e.SetChannels(new[] { View("custom", "cpu", v => applied = v) });
        e.Step();
        Assert.Null(applied);
    }

    [Fact]
    public void Live_text_stays_in_the_engine_until_the_window_asks()
    {
        var e = Engine(new Reads());
        var v = View("custom", "cpu");
        var notified = new List<string?>();
        v.PropertyChanged += (_, a) => notified.Add(a.PropertyName);
        e.SetChannels(new[] { v });
        e.Step();
        Assert.NotEqual("", v.EngineLive);
        Assert.Equal("", v.Live);
        Assert.Empty(notified);
    }

    [Fact]
    public async Task Loop_runs_alone_outside_any_dispatcher_and_stops_for_good()
    {
        var firstStep = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var v = View("custom", "cpu", _ =>
        {
            Interlocked.Increment(ref calls);
            firstStep.TrySetResult(Dispatcher.FromThread(Thread.CurrentThread) == null);
        });
        var e = new FanEngine(new Reads().Read, () => new FanSettings(), () => false, TimeSpan.FromMilliseconds(10));
        e.SetChannels(new[] { v });
        e.Start();
        var finished = await Task.WhenAny(firstStep.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(firstStep.Task, finished);
        Assert.True(await firstStep.Task);
        e.Stop(TimeSpan.FromSeconds(5));
        int atStop = Volatile.Read(ref calls);
        await Task.Delay(50);
        Assert.Equal(atStop, Volatile.Read(ref calls));
        Assert.False(e.Running);
    }

    // ---------------- failsafe de sensor (T85) ----------------

    private sealed class Failsafe
    {
        public readonly Reads Reads = new() { Answer = null };
        public long Now = 1;
        public readonly FanEngine Engine;
        public readonly List<bool> States = new();
        public Failsafe()
        {
            Engine = new FanEngine(Reads.Read, () => new FanSettings(), () => false) { Clock = () => Now };
            Engine.SensorStateChanged += States.Add;
        }
        public void Advance(double seconds) => Now += (long)(seconds * System.Diagnostics.Stopwatch.Frequency);
    }

    private static FanChannelView Output(string name, List<float> duties, Action? setDefault = null) =>
        new(new FanOutput("id-" + name, "chip", name, duties.Add, setDefault ?? (() => { }), () => "1000 RPM"),
            new FanChannelSettings { Preset = "custom", Source = "cpu", Custom = "30:20, 70:80" });

    [Fact]
    public void Short_sensor_failure_changes_nothing()
    {
        var f = new Failsafe();
        var duties = new List<float>();
        int restores = 0;
        f.Engine.SetChannels(new[] { Output("Fan #1", duties, () => restores++) });
        f.Engine.Step();
        f.Advance(15);
        f.Engine.Step();
        Assert.Empty(duties);
        Assert.Equal(0, restores);
        Assert.False(f.Engine.SensorUnavailable);
    }

    [Fact]
    public void Long_sensor_failure_gives_control_back_to_the_bios()
    {
        var f = new Failsafe();
        var duties = new List<float>();
        int restores = 0;
        var v = Output("Fan #1", duties, () => restores++);
        f.Engine.SetChannels(new[] { v });
        f.Engine.Step();
        f.Advance(16);
        f.Engine.Step();
        Assert.Equal(1, restores);
        Assert.Empty(duties);
        Assert.True(f.Engine.SensorUnavailable);
        Assert.Equal(new[] { true }, f.States);
        Assert.Contains("BIOS", v.EngineLive);
    }

    [Theory]
    [InlineData("CPU Fan", 100f)]
    [InlineData("AIO Pump", 100f)]
    [InlineData("Fan #3", 80f)]
    public void Output_without_restore_gets_a_safe_duty(string name, float expected)
    {
        var f = new Failsafe();
        var duties = new List<float>();
        f.Engine.SetChannels(new[] { Output(name, duties, () => throw new InvalidOperationException("sem controle")) });
        f.Engine.Step();
        f.Advance(16);
        f.Engine.Step();
        Assert.Equal(new[] { expected }, duties);
    }

    [Fact]
    public void Failsafe_runs_once_while_the_failure_lasts()
    {
        var f = new Failsafe();
        int restores = 0;
        f.Engine.SetChannels(new[] { Output("Fan #1", new List<float>(), () => restores++) });
        f.Engine.Step();
        f.Advance(16);
        f.Engine.Step();
        f.Advance(60);
        f.Engine.Step();
        Assert.Equal(1, restores);
        Assert.Equal(new[] { true }, f.States);
    }

    [Fact]
    public void Curves_resume_when_the_reading_comes_back()
    {
        var f = new Failsafe();
        var duties = new List<float>();
        f.Engine.SetChannels(new[] { Output("Fan #1", duties) });
        f.Engine.Step();
        f.Advance(16);
        f.Engine.Step();
        f.Reads.Answer = new FanInputs(50, null);
        f.Engine.Step();
        Assert.False(f.Engine.SensorUnavailable);
        Assert.Single(duties);
        Assert.Equal(new[] { true, false }, f.States);
    }

    [Fact]
    public void Reader_that_throws_counts_as_a_failure()
    {
        var clock = 1L;
        int restores = 0;
        var e = new FanEngine((_, _, _) => throw new InvalidOperationException("sensor"), () => new FanSettings(), () => false) { Clock = () => clock };
        e.SetChannels(new[] { Output("Fan #1", new List<float>(), () => restores++) });
        e.Step();
        clock += 16 * System.Diagnostics.Stopwatch.Frequency;
        e.Step();
        Assert.Equal(1, restores);
    }

    [Fact]
    public void Bios_channels_are_not_touched_by_the_failsafe()
    {
        var f = new Failsafe();
        int restores = 0;
        f.Engine.LiveWanted = true;
        var bios = new FanChannelView(new FanOutput("id", "chip", "fan", _ => { }, () => restores++, () => "x"), new FanChannelSettings { Preset = "bios" });
        f.Engine.SetChannels(new[] { bios });
        f.Engine.Step();
        f.Advance(16);
        f.Engine.Step();
        Assert.Equal(0, restores);
        Assert.False(f.Engine.SensorUnavailable);
    }
}
