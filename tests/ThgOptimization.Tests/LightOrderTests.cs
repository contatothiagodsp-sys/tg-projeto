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

    // ---------------- regras por fabricante (T79) ----------------

    [Theory]
    [InlineData("ASUS ROG STRIX · Aura Addressable 1", "regra ASUS")]
    [InlineData("Gigabyte RGB Fusion · D_LED1", "regra Gigabyte")]
    [InlineData("ASRock Polychrome · ARGB 1", "regra ASRock")]
    public void Addressable_header_of_these_makers_uses_grb_and_asks_for_a_check(string name, string source)
    {
        var r = ColorOrderAuto.Effective(Meta(name: name, type: "motherboard", kind: LightZoneKind.Linear), autoOrder: true);
        Assert.Equal(LightChannelOrder.GRB, r.Order);
        Assert.Equal(DetectionConfidence.Medium, r.Confidence);
        Assert.Equal(source, r.Source);
        Assert.True(ColorOrderAuto.NeedsColorTest(r.Confidence));
    }

    [Fact]
    public void Msi_header_stays_rgb_by_rule()
    {
        var r = ColorOrderAuto.Effective(Meta(name: "MSI Mystic Light · JRAINBOW1", type: "motherboard", kind: LightZoneKind.Linear), autoOrder: true);
        Assert.Equal(LightChannelOrder.RGB, r.Order);
        Assert.Equal("regra MSI", r.Source);
    }

    [Theory]
    [InlineData("ASUS ROG Strix Scope", "keyboard")]
    [InlineData("ASUS ROG Spatha", "mouse")]
    [InlineData("Gigabyte Aorus RGB DRAM", "dram")]
    public void Integrated_devices_of_the_same_makers_are_not_swapped_twice(string name, string type)
    {
        var r = ColorOrderAuto.Effective(Meta(name: name, type: type, kind: LightZoneKind.Linear), autoOrder: true);
        Assert.Equal(LightChannelOrder.RGB, r.Order);
        Assert.Equal(DetectionConfidence.High, r.Confidence);
    }

    [Theory]
    [InlineData("OpenRGB", LightBackendContract.NormalizedRgb, "Corsair K70", "regra Corsair")]
    [InlineData("Razer Chroma", LightBackendContract.NormalizedRgb, "Teclado", "regra Razer")]
    [InlineData("OpenRGB", LightBackendContract.NormalizedRgb, "Logitech G502", "regra Logitech")]
    public void Sdk_makers_stay_rgb_by_rule(string backend, LightBackendContract contract, string name, string source)
    {
        var r = ColorOrderAuto.Effective(Meta(contract, backend, name), autoOrder: true);
        Assert.Equal(LightChannelOrder.RGB, r.Order);
        Assert.Equal(source, r.Source);
    }

    [Fact]
    public void Unknown_device_falls_back_to_rgb_with_unknown_confidence()
    {
        var r = ColorOrderAuto.Effective(Meta(LightBackendContract.Unknown, "Outra", "Marca desconhecida"), autoOrder: true);
        Assert.Equal(LightChannelOrder.RGB, r.Order);
        Assert.Equal(DetectionConfidence.Unknown, r.Confidence);
        Assert.Equal("fallback RGB", r.Source);
        Assert.True(ColorOrderAuto.NeedsColorTest(r.Confidence));
    }

    [Fact]
    public void Auto_order_off_ignores_the_rules_and_keeps_the_manual_choice()
    {
        var asus = Meta(name: "ASUS ROG STRIX · Aura Addressable 1", type: "motherboard", kind: LightZoneKind.Linear);
        Assert.Equal(LightChannelOrder.RGB, ColorOrderAuto.Effective(asus, autoOrder: false).Order);
        var manual = ColorOrderAuto.Effective(asus, autoOrder: false, manual: "BGR");
        Assert.Equal(LightChannelOrder.BGR, manual.Order);
        Assert.Equal("manual", manual.Source);
    }

    [Fact]
    public void Manual_choice_beats_the_rules()
    {
        var asus = Meta(name: "ASUS ROG STRIX · Aura Addressable 1", type: "motherboard", kind: LightZoneKind.Linear);
        Assert.Equal(LightChannelOrder.RGB, ColorOrderAuto.Effective(asus, autoOrder: true, manual: "RGB").Order);
    }

    [Fact]
    public void Color_test_lights_red_green_and_blue_in_order()
    {
        Assert.Equal(new[] { "vermelho", "verde", "azul" }, ColorOrderAuto.TestSequence.Select(c => c.Name));
        Assert.Equal(((byte)255, (byte)0, (byte)0), ColorOrderAuto.TestSequence[0].Color);
    }

    [Theory]
    [InlineData(DetectionConfidence.Unknown, true)]
    [InlineData(DetectionConfidence.Low, true)]
    [InlineData(DetectionConfidence.Medium, true)]
    [InlineData(DetectionConfidence.High, false)]
    public void Color_test_is_offered_below_high_confidence(DetectionConfidence confidence, bool offered)
    {
        Assert.Equal(offered, ColorOrderAuto.NeedsColorTest(confidence));
    }

    [Fact]
    public void Vm_shows_where_the_decision_came_from()
    {
        var auto = ColorOrderAuto.Effective(Meta(name: "ASUS ROG STRIX · Aura Addressable 1", type: "motherboard", kind: LightZoneKind.Linear), autoOrder: true);
        var vm = new LightDeviceVm { DefaultOrder = auto.Order.ToString(), Automatic = auto, Order = auto.Order.ToString() };
        Assert.Equal("GRB · regra ASUS", vm.OrderText);
        Assert.Equal(System.Windows.Visibility.Visible, vm.ColorTestVisibility);
        vm.Order = "RGB";
        Assert.Equal("RGB · manual", vm.OrderText);
        Assert.Equal(System.Windows.Visibility.Collapsed, vm.ColorTestVisibility);
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
    public void Unknown_vid_pid_falls_back_to_grb_with_low_confidence()
    {
        var r = ColorOrderAuto.ResolveOrder(Meta(LightBackendContract.RawChannels, location: @"HID: \\?\hid#vid_ffff&pid_0001#x"), new[] { GrbByVid });
        Assert.Equal(LightChannelOrder.GRB, r.Order);
        Assert.Equal(DetectionConfidence.Low, r.Confidence);
        Assert.Equal("fallback GRB", r.Source);
    }

    [Fact]
    public void Device_without_location_does_not_match_a_vid_rule()
    {
        var m = Meta(LightBackendContract.RawChannels);
        Assert.Null(m.VidPid);
        var r = ColorOrderAuto.ResolveOrder(m, new[] { GrbByVid });
        Assert.Equal("fallback GRB", r.Source); // a regra não bateu: o que vale é a reserva da ponte de canais crus
    }

    // ---------------- Lian Li e conectores endereçáveis ----------------

    [Theory]
    [InlineData("Lian Li UNI FAN")]
    [InlineData("Lian-Li Strimer")]
    [InlineData("SL-INF 120")]
    [InlineData("TL LCD")]
    [InlineData("GA II Trinity")]
    [InlineData("L-Connect")]
    public void Lian_li_names_are_recognized(string name)
    {
        Assert.Equal("LianLi", ColorOrderAuto.DetectMaker("OpenRGB", name, ""));
    }

    [Fact]
    public void Lian_li_backend_uses_the_rule_on_top_with_medium_confidence()
    {
        Assert.Equal("LianLi", ColorOrderAuto.DetectMaker("LianLi", "qualquer", ""));
        var r = ColorOrderAuto.Effective(Meta(LightBackendContract.RawChannels, "LianLi", "Hub"), autoOrder: true);
        Assert.Equal((LightChannelOrder.GRB, DetectionConfidence.Medium, "regra Lian Li"), (r.Order, r.Confidence, r.Source));
        Assert.Equal("LianLi", ColorOrderAuto.Regras[0].Maker);
    }

    [Theory]
    [InlineData((ushort)0x0CF2, "LianLi")]
    [InlineData((ushort)0x1A86, "LianLi")]
    [InlineData((ushort)0x048D, "Gigabyte")]
    [InlineData((ushort)0x26CE, "ASRock")]
    public void Controller_vendor_ids_are_known(ushort vid, string maker) => Assert.Equal(maker, ColorOrderAuto.MakerFromVid(vid));

    [Theory]
    [InlineData("motherboard", LightZoneKind.Single)]
    [InlineData("motherboard", LightZoneKind.Matrix)]
    [InlineData("ledstrip", LightZoneKind.Single)]
    [InlineData("cooler", LightZoneKind.Linear)]
    public void Header_types_count_as_external_in_any_zone_kind(string type, LightZoneKind kind)
    {
        Assert.True(ColorOrderAuto.IsExternalHeader(Meta(type: type, kind: kind)));
    }

    [Theory]
    [InlineData("Gen2 1")]
    [InlineData("ADDR_LED2")]
    [InlineData("Polychrome 1")]
    [InlineData("Aura Addressable")]
    public void Header_names_count_as_external_without_a_controller_type(string zone)
    {
        Assert.True(ColorOrderAuto.IsExternalHeader(Meta(zone: zone, kind: LightZoneKind.Single)));
    }

    [Theory]
    [InlineData("keyboard")]
    [InlineData("mouse")]
    [InlineData("dram")]
    [InlineData("gpu")]
    public void Name_never_turns_an_integrated_device_into_a_header(string type)
    {
        var r = ColorOrderAuto.Effective(Meta(name: "ASUS Aura", type: type, kind: LightZoneKind.Linear), autoOrder: true);
        Assert.Equal(LightChannelOrder.RGB, r.Order); // o driver já converte: GRB aqui seria a troca dupla
    }

    [Fact]
    public void External_header_falls_back_to_grb_only_with_auto_order_on()
    {
        var header = Meta(type: "ledstrip", kind: LightZoneKind.Linear);
        Assert.Equal(LightChannelOrder.GRB, ColorOrderAuto.Effective(header, autoOrder: true).Order);
        Assert.Equal(LightChannelOrder.RGB, ColorOrderAuto.Effective(header, autoOrder: false).Order);
    }

    [Theory]
    [InlineData(LightChannelOrder.RGB, 1, 2, 3)]
    [InlineData(LightChannelOrder.RBG, 1, 3, 2)]
    [InlineData(LightChannelOrder.GRB, 2, 1, 3)]
    [InlineData(LightChannelOrder.GBR, 2, 3, 1)]
    [InlineData(LightChannelOrder.BRG, 3, 1, 2)]
    [InlineData(LightChannelOrder.BGR, 3, 2, 1)]
    public void Create_mapper_applies_the_physical_permutation(LightChannelOrder order, byte a, byte b, byte c)
    {
        Assert.Equal((a, b, c), ColorOrderAuto.CreateMapper(order)(((byte)1, (byte)2, (byte)3)));
    }

    [Fact]
    public void Manual_order_stays_manual_and_the_default_stays_automatic()
    {
        var header = Meta(type: "ledstrip", kind: LightZoneKind.Linear);
        var auto = ColorOrderAuto.Effective(header, autoOrder: true);
        var effective = ColorOrderAuto.Effective(header, autoOrder: true, manual: "BGR");
        var vm = new LightDeviceVm { DefaultOrder = auto.Order.ToString(), Automatic = auto, Order = effective.Order.ToString() };
        Assert.Equal("BGR · manual", vm.OrderText);
        Assert.True(vm.IsManual);
    }

    // ---------------- protocolo HID do hub Lian Li ----------------

    [Theory]
    [InlineData(0, 0x10)]
    [InlineData(3, 0x13)]
    public void Activate_packet_has_the_documented_layout(int channel, byte command)
    {
        var p = new byte[LianLiHidProtocol.PacketLength];
        p.AsSpan().Fill(0xAA);
        LianLiHidProtocol.WriteActivate(p, channel, 2);
        Assert.Equal(new byte[] { 0xE0, command, 0x00, 0x01, 0x00, 2, 0x00 }, p[..7]);
        Assert.All(p[7..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Color_packet_carries_the_mapped_bytes_in_order()
    {
        var colors = new (byte R, byte G, byte B)[] { (10, 20, 30), (40, 50, 60) };
        var p = new byte[LianLiHidProtocol.PacketLength];
        Assert.Equal(2, LianLiHidProtocol.WriteColors(p, 1, 7, colors));
        Assert.Equal(new byte[] { 0xE0, 0x31, 7, 2, 10, 20, 30, 40, 50, 60, 0 }, p[..11]);
    }

    [Fact]
    public void Colors_are_split_in_blocks_of_twenty_inside_65_bytes()
    {
        var colors = new (byte R, byte G, byte B)[45];
        var p = new byte[LianLiHidProtocol.PacketLength];
        Assert.Equal(3, LianLiHidProtocol.PacketCount(colors.Length));
        Assert.Equal(20, LianLiHidProtocol.WriteColors(p, 0, 0, colors));
        Assert.Equal(20, p[3]);
        Assert.Equal(5, LianLiHidProtocol.WriteColors(p, 0, 2, colors.AsSpan(40)));
        Assert.True(LianLiHidProtocol.HeaderLength + 20 * 3 <= LianLiHidProtocol.PacketLength);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Channel_out_of_range_never_reaches_the_fan_speed_commands(int channel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LianLiHidProtocol.WriteActivate(new byte[LianLiHidProtocol.PacketLength], channel, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LianLiHidProtocol.WriteColors(new byte[LianLiHidProtocol.PacketLength], channel, 0, ReadOnlySpan<(byte, byte, byte)>.Empty));
    }

    [Fact]
    public void Hub_backend_declares_raw_channels()
    {
        var hub = new LianLiHubBackend();
        Assert.Equal(LightBackendContract.RawChannels, hub.Contract);
        Assert.Equal("LianLi", hub.Name);
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
        long smallest = long.MaxValue; // a menor de várias rodadas: custo por LED aparece em todas, e o do runtime, numa só
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < buffer.Length; i++) buffer[i] = mapper(((byte)i, 2, 3));
            smallest = Math.Min(smallest, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Assert.Equal(0, smallest);
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
