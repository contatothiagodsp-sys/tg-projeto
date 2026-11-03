using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

public class LightOrderResolutionTests
{
    private static LightDeviceMetadata Meta(LightBackendContract contract = LightBackendContract.NormalizedRgb, string backend = "OpenRGB", string name = "Controlador",
                                            string zone = "", LightZoneKind kind = LightZoneKind.Unknown, string type = "", string location = "") =>
        new(backend, contract, name, "", zone, kind, type, location);

    private static readonly LightOrderRule GrbByVid = new(LightChannelOrder.GRB, "Documentado pelo fabricante", VendorId: 0x1234, ProductId: 0x0001);

    [Fact]
    public void Normalized_backend_stays_rgb_with_high_confidence()
    {
        var r = ColorOrderAuto.ResolveOrder(Meta(type: "keyboard"));
        Assert.Equal(LightChannelOrder.RGB, r.Order);
        Assert.Equal(DetectionConfidence.High, r.Confidence);
        Assert.Equal("OpenRGB", r.Backend);
    }

    [Theory]
    [InlineData("ASUS ROG STRIX · Aura Addressable 1")]
    [InlineData("Gigabyte RGB Fusion · D_LED1")]
    [InlineData("MSI Mystic Light · JRAINBOW1")]
    [InlineData("ASRock Polychrome · ARGB 1")]
    public void Maker_of_the_motherboard_never_implies_grb(string name)
    {
        Assert.Equal(LightChannelOrder.RGB, ColorOrderAuto.ResolveOrder(Meta(name: name, type: "motherboard", kind: LightZoneKind.Linear)).Order);
    }

    [Fact]
    public void Rule_never_permutes_over_a_normalized_backend()
    {
        var rule = new LightOrderRule(LightChannelOrder.GRB, "palpite", Backend: "OpenRGB");
        Assert.Equal(LightChannelOrder.RGB, ColorOrderAuto.ResolveOrder(Meta(), new[] { rule }).Order);
    }

    [Fact]
    public void Proven_rule_with_explicit_override_applies_on_a_normalized_backend()
    {
        var rule = new LightOrderRule(LightChannelOrder.GRB, "Medido neste controlador", Backend: "OpenRGB", OverridesNormalizedContract: true);
        var r = ColorOrderAuto.ResolveOrder(Meta(), new[] { rule });
        Assert.Equal(LightChannelOrder.GRB, r.Order);
        Assert.Equal("Medido neste controlador", r.Reason);
    }

    [Fact]
    public void Raw_backend_uses_grb_when_proven_by_vid_pid()
    {
        var r = ColorOrderAuto.ResolveOrder(Meta(LightBackendContract.RawChannels, location: @"HID: \\?\hid#vid_1234&pid_0001#x"), new[] { GrbByVid });
        Assert.Equal(LightChannelOrder.GRB, r.Order);
        Assert.Equal(DetectionConfidence.High, r.Confidence);
    }

    [Fact]
    public void Unknown_vid_pid_falls_back_to_rgb_with_low_confidence()
    {
        var r = ColorOrderAuto.ResolveOrder(Meta(LightBackendContract.RawChannels, location: @"HID: \\?\hid#vid_ffff&pid_0001#x"), new[] { GrbByVid });
        Assert.Equal(LightChannelOrder.RGB, r.Order);
        Assert.Equal(DetectionConfidence.Low, r.Confidence);
    }

    [Fact]
    public void Device_without_location_does_not_match_a_vid_rule()
    {
        var m = Meta(LightBackendContract.RawChannels);
        Assert.Null(m.VidPid);
        Assert.Equal(LightChannelOrder.RGB, ColorOrderAuto.ResolveOrder(m, new[] { GrbByVid }).Order);
    }

    [Fact]
    public void Undeclared_backend_reports_that_it_does_not_know()
    {
        var r = ColorOrderAuto.ResolveOrder(Meta(LightBackendContract.Unknown));
        Assert.Equal(LightChannelOrder.RGB, r.Order);
        Assert.Equal(DetectionConfidence.Unknown, r.Confidence);
    }

    [Fact]
    public void Zones_of_the_same_controller_resolve_independently()
    {
        var rule = new LightOrderRule(LightChannelOrder.GRB, "Zona medida", ZoneNameContains: "ARGB", OverridesNormalizedContract: true);
        Assert.Equal(LightChannelOrder.GRB, ColorOrderAuto.ResolveOrder(Meta(zone: "ARGB 1"), new[] { rule }).Order);
        Assert.Equal(LightChannelOrder.RGB, ColorOrderAuto.ResolveOrder(Meta(zone: "RGB 12V"), new[] { rule }).Order);
    }

    [Fact]
    public void External_header_warns_while_integrated_device_does_not()
    {
        Assert.Equal(DetectionConfidence.Medium, ColorOrderAuto.ResolveOrder(Meta(type: "motherboard", kind: LightZoneKind.Linear)).Confidence);
        Assert.Equal(DetectionConfidence.High, ColorOrderAuto.ResolveOrder(Meta(type: "dram", kind: LightZoneKind.Linear)).Confidence);
    }

    [Fact]
    public void Manual_choice_wins_over_everything()
    {
        var r = ColorOrderAuto.ResolveOrder(Meta(LightBackendContract.RawChannels, location: "vid_1234&pid_0001"), new[] { GrbByVid }, manual: "bgr");
        Assert.Equal(LightChannelOrder.BGR, r.Order);
    }

    [Fact]
    public void Invalid_manual_value_is_ignored()
    {
        Assert.Equal(LightChannelOrder.RGB, ColorOrderAuto.ResolveOrder(Meta(), manual: "XYZ").Order);
    }

    [Fact]
    public void Rule_without_criteria_matches_nothing()
    {
        Assert.False(new LightOrderRule(LightChannelOrder.GRB, "vazia").Matches(Meta()));
    }

    [Theory]
    [InlineData(LightChannelOrder.RGB, 1, 2, 3)]
    [InlineData(LightChannelOrder.RBG, 1, 3, 2)]
    [InlineData(LightChannelOrder.GRB, 2, 1, 3)]
    [InlineData(LightChannelOrder.GBR, 2, 3, 1)]
    [InlineData(LightChannelOrder.BRG, 3, 1, 2)]
    [InlineData(LightChannelOrder.BGR, 3, 2, 1)]
    public void Every_permutation_maps_the_channels(LightChannelOrder order, byte a, byte b, byte c)
    {
        Assert.Equal((a, b, c), LightChannelMapper.For(order)(((byte)1, (byte)2, (byte)3)));
    }

    [Fact]
    public void Mapper_is_a_single_shared_instance_per_order()
    {
        Assert.Same(LightChannelMapper.For(LightChannelOrder.GRB), LightChannelMapper.For(LightChannelOrder.GRB));
        var vm = new LightDeviceVm { Order = "GRB" };
        var first = vm.Mapper;
        Assert.Same(first, vm.Mapper);
        vm.Order = "GRB";
        Assert.Same(first, vm.Mapper);
    }

    [Fact]
    public void Mapping_every_led_does_not_allocate()
    {
        var mapper = LightChannelMapper.For(LightChannelOrder.GRB);
        var buffer = new (byte, byte, byte)[1000];
        mapper((1, 2, 3)); // o JIT não conta como custo por LED
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < buffer.Length; i++) buffer[i] = mapper(((byte)i, 2, 3));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(30, 33.3)]
    [InlineData(10, 100)]
    [InlineData(500, 33.3)] // nunca acima de 30 por segundo, mesmo com o arquivo editado
    public void Light_interval_is_capped_at_30_per_second(int fps, double ms)
    {
        Assert.Equal(ms, MainWindow.LightFrameInterval(fps).TotalMilliseconds, 0);
    }
}

public class SettingsMigrationV3Tests
{
    [Fact]
    public void Manual_orders_survive_and_are_normalized()
    {
        var s = new AppSettings { SettingsVersion = 2 };
        s.LightColorOrder["OpenRGB|a"] = "grb";
        s.LightColorOrder["OpenRGB|b"] = "BGR";
        s.LightColorOrder["OpenRGB|c"] = "lixo";
        SettingsMigration.Apply(s, 16);
        Assert.Equal("GRB", s.LightColorOrder["OpenRGB|a"]);
        Assert.Equal("BGR", s.LightColorOrder["OpenRGB|b"]);
        Assert.Equal("lixo", s.LightColorOrder["OpenRGB|c"]); // nada é apagado
    }

    [Fact]
    public void Avoid_cpu0_saved_by_the_old_default_is_cleared_with_many_threads()
    {
        var s = new AppSettings { SettingsVersion = 2, GameModeAvoidCpu0 = true };
        SettingsMigration.Apply(s, 16);
        Assert.False(s.GameModeAvoidCpu0);
    }

    [Fact]
    public void Avoid_cpu0_chosen_below_the_old_threshold_stays()
    {
        var s = new AppSettings { SettingsVersion = 2, GameModeAvoidCpu0 = true };
        SettingsMigration.Apply(s, 8);
        Assert.True(s.GameModeAvoidCpu0);
    }

    [Fact]
    public void After_migrating_a_later_choice_is_kept()
    {
        var s = new AppSettings { SettingsVersion = 2 };
        SettingsMigration.Apply(s, 16);
        s.GameModeAvoidCpu0 = true;
        SettingsMigration.Apply(s, 16);
        Assert.True(s.GameModeAvoidCpu0);
    }

    [Theory]
    [InlineData(500, 30)]
    [InlineData(0, 30)]
    [InlineData(15, 15)]
    public void Light_fps_is_brought_into_range(int saved, int expected)
    {
        var s = new AppSettings { SettingsVersion = 2, LightMaxFps = saved };
        SettingsMigration.Apply(s, 8);
        Assert.Equal(expected, s.LightMaxFps);
    }
}

public class LightPumpTests
{
    private sealed class FakeBackend : ILightBackend
    {
        public readonly List<byte[]> Frames = new();
        public Func<int>? OnSend;
        public ManualResetEventSlim? Hold;
        public ManualResetEventSlim Entered = new(false);
        public string Name => "fake";
        public int MinIntervalMs => 0;
        public LightBackendContract Contract => LightBackendContract.NormalizedRgb;
        public int Acquire(LightDevice d, bool exclusive) => 0;
        public int Send(LightDevice d, ReadOnlySpan<(byte R, byte G, byte B)> colors)
        {
            Entered.Set();
            Hold?.Wait(TimeSpan.FromSeconds(10));
            lock (Frames) Frames.Add(new[] { colors[0].R });
            return OnSend?.Invoke() ?? 0;
        }
        public void Release(LightDevice d) { }
        public void EndSession() { }
        public string ErrorText(int e) => "";
        public void Shutdown() { }
    }

    private static LightDevice Device(ILightBackend b, string id = "a") => new() { Backend = b, Id = id, Name = id, LedX = new[] { 0.0 } };
    private static (byte, byte, byte)[] Color(byte r) => new[] { (r, (byte)0, (byte)0) };

    [Fact]
    public void Latest_frame_wins_while_the_device_is_busy()
    {
        var b = new FakeBackend { Hold = new ManualResetEventSlim(false) };
        using var pump = new LightPump();
        var d = Device(b);
        pump.Post(d, Color(1), null);
        Assert.True(b.Entered.Wait(TimeSpan.FromSeconds(5)));
        for (byte i = 2; i <= 50; i++) pump.Post(d, Color(i), null);
        b.Hold.Set();
        Assert.True(SpinWait.SpinUntil(() => { lock (b.Frames) return b.Frames.Count >= 2; }, TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);
        lock (b.Frames)
        {
            Assert.Equal(2, b.Frames.Count); // nada de fila: só o primeiro e o último
            Assert.Equal(50, b.Frames[1][0]);
        }
    }

    [Fact]
    public void Caller_buffer_can_change_after_post()
    {
        var b = new FakeBackend();
        using var pump = new LightPump();
        var colors = Color(7);
        var sent = new ManualResetEventSlim(false);
        pump.Post(Device(b), colors, _ => sent.Set());
        colors[0] = (99, 0, 0);
        Assert.True(sent.Wait(TimeSpan.FromSeconds(5)));
        lock (b.Frames) Assert.True(b.Frames[0][0] is 7 or 99);
    }

    [Fact]
    public void Backend_exception_is_reported_and_does_not_kill_the_thread()
    {
        var b = new FakeBackend { OnSend = () => throw new InvalidOperationException("caiu") };
        using var pump = new LightPump();
        var errors = new List<int>();
        var done = new AutoResetEvent(false);
        Action<int> report = e => { lock (errors) errors.Add(e); done.Set(); };
        pump.Post(Device(b), Color(1), report);
        Assert.True(done.WaitOne(TimeSpan.FromSeconds(5)));
        b.OnSend = () => 0;
        pump.Post(Device(b), Color(2), report);
        Assert.True(done.WaitOne(TimeSpan.FromSeconds(5)));
        lock (errors) Assert.Equal(new[] { -1, 0 }, errors);
        Assert.Equal(1, pump.FailedSends);
    }

    [Fact]
    public void Exception_in_the_callback_does_not_kill_the_thread()
    {
        var b = new FakeBackend();
        using var pump = new LightPump();
        pump.Post(Device(b), Color(1), _ => throw new InvalidOperationException("callback"));
        Assert.True(b.Entered.Wait(TimeSpan.FromSeconds(5)));
        var ok = new ManualResetEventSlim(false);
        pump.Post(Device(b), Color(2), _ => ok.Set());
        Assert.True(ok.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Post_after_shutdown_is_refused_without_throwing()
    {
        var pump = new LightPump();
        Assert.True(pump.Shutdown(TimeSpan.FromSeconds(5)));
        Assert.False(pump.Post(Device(new FakeBackend()), Color(1), null));
        Assert.True(pump.Shutdown(TimeSpan.FromSeconds(1))); // segunda vez não lança
        pump.Dispose();
    }

    [Fact]
    public void Shutdown_during_a_send_reports_the_timeout_and_finishes_later()
    {
        var b = new FakeBackend { Hold = new ManualResetEventSlim(false) };
        var pump = new LightPump();
        var d = Device(b);
        pump.Post(d, Color(1), null);
        Assert.True(b.Entered.Wait(TimeSpan.FromSeconds(5)));
        pump.Post(d, Color(2), null);
        Assert.False(pump.Shutdown(TimeSpan.FromMilliseconds(50)));
        b.Hold.Set();
        Assert.True(pump.Shutdown(TimeSpan.FromSeconds(5)));
        lock (b.Frames) Assert.Single(b.Frames); // o quadro pendente não sai depois do encerramento
    }

    [Fact]
    public void Drain_drops_pending_frames_and_waits_for_the_send_in_progress()
    {
        var b = new FakeBackend { Hold = new ManualResetEventSlim(false) };
        using var pump = new LightPump();
        var d = Device(b);
        pump.Post(d, Color(1), null);
        Assert.True(b.Entered.Wait(TimeSpan.FromSeconds(5)));
        pump.Post(d, Color(2), null);
        Assert.False(pump.Drain(TimeSpan.FromMilliseconds(30))); // ainda enviando
        b.Hold.Set();
        Assert.True(pump.Drain(TimeSpan.FromSeconds(5)));
        Thread.Sleep(30);
        lock (b.Frames) Assert.Single(b.Frames);
    }

    [Fact]
    public async Task Concurrent_post_and_shutdown_never_throw()
    {
        for (int i = 0; i < 25; i++)
        {
            var pump = new LightPump();
            var d = Device(new FakeBackend());
            var poster = Task.Run(() => { for (int n = 0; n < 2000; n++) pump.Post(d, Color(1), null); });
            pump.Shutdown(TimeSpan.FromSeconds(5));
            await poster;
        }
    }
}
