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
}
