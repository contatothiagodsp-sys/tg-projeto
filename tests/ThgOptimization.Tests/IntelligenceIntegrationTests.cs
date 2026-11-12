using System.IO;
using System.Net;
using System.Text.Json;
using ThgOptimization.Core.Benchmark;
using ThgOptimization.Core.Intelligence;
using ThgOptimization.Infrastructure;
using ThgOptimization.Infrastructure.Intelligence;

namespace ThgOptimization.Tests;

/// <summary>A ligação do tHg Intelligence com o app: gravação de sessão, catálogo de ações sobre as configurações, sonda de rede e inventário.</summary>
public class IntelligenceIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "thg-intel-app-" + Guid.NewGuid().ToString("N"));

    public IntelligenceIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static CpuTopologySnapshot Uniform(int cores) =>
        CpuSetPlannerSample(cores, hybrid: false);

    private static CpuTopologySnapshot CpuSetPlannerSample(int cores, bool hybrid)
    {
        var cpus = new List<LogicalCpu>();
        for (int i = 0; i < cores; i++) cpus.Add(new LogicalCpu((uint)(256 + i), 0, (byte)i, i, 0, (byte)(hybrid && i < cores / 2 ? 1 : 0)));
        return new CpuTopologySnapshot(cpus, new[] { new CacheDomain(0, 32UL << 20) }, hybrid ? "GenuineIntel" : "AuthenticAMD");
    }

    // ---------------------------------------------------------------- gravação de sessão

    [Fact]
    public void Session_file_written_by_the_previous_version_still_opens()
    {
        // Linha de amostra como a 1.9 gravava: sem as colunas da 2.0.
        string old = "{\"TimestampMs\":1000,\"FrametimeMs\":8.3,\"Fps\":120,\"CpuTemp\":60,\"CpuLoad\":30,\"CpuClock\":5000,\"CpuPower\":70,\"GpuTemp\":65,\"GpuLoad\":97," +
                     "\"GpuClock\":2100,\"GpuPower\":200,\"VramPct\":50,\"RamPct\":40,\"FanCpuRpm\":null,\"Throttled\":false,\"StuttersThisSecond\":0,\"WheaError\":false}";
        var sample = JsonSerializer.Deserialize<SessionSample>(old)!;
        Assert.Equal(97, sample.GpuLoad);
        Assert.Null(sample.CpuMaxCoreLoad);
        Assert.Null(sample.CommitPct);
        Assert.Null(sample.NetLatencyMs);
        Assert.Null(sample.NetLost);
    }

    [Fact]
    public void New_columns_are_recorded_saved_and_loaded()
    {
        var store = new SessionStore(_dir);
        var clock = new DateTime(2026, 11, 1, 20, 0, 0);
        var recorder = new SessionRecorder(store, () => clock);
        recorder.Begin("Jogo");
        for (int i = 0; i < 5; i++)
        {
            clock = clock.AddSeconds(1);
            recorder.Add(10, 100, 60, 30, 5000, 70, 65, 90, 2000, 180, 50, 40, 0, false, false, cpuMaxCore: 95, commitPct: 61, netLatencyMs: i == 2 ? 23 : null, netLost: i == 2 ? false : null);
        }
        var saved = recorder.End()!;
        var loaded = SessionStore.Load(saved.FilePath!)!;
        Assert.Equal(95, loaded.Samples[0].CpuMaxCoreLoad);
        Assert.Equal(61, loaded.Samples[0].CommitPct);
        Assert.Equal(23, loaded.Samples[2].NetLatencyMs);
        Assert.Null(loaded.Samples[0].NetLost);

        var telemetry = SessionTelemetryAdapter.From(loaded, new SessionContext("Jogo", "k", clock));
        Assert.Equal(95, telemetry.Points[0].CpuMaxCoreLoad);
        Assert.Equal(23, telemetry.Points[2].NetLatencyMs);
        Assert.Null(telemetry.Points[0].WorstFrameMs); // sem gravação de frames, a média do segundo não vira "pior frame"
    }

    [Fact]
    public void Adapter_keeps_missing_readings_missing_and_drops_non_finite_values()
    {
        var samples = new List<SessionSample>
        {
            new(0, null, null, null, null, null, null, null, null, null, null, double.NaN, null, null, false, 0, true),
            new(1000, 40, 90, 70, 50, 4800, 80, 60, 95, 2000, 200, 80, 60, null, false, 2, false),
        };
        var timeline = new SessionTimeline(new SessionHeader(1, "Jogo", new DateTime(2026, 11, 1), 2, 90, 60, 2), samples, new float[] { 10, 40, 10 });
        var t = SessionTelemetryAdapter.From(timeline, new SessionContext("Jogo", "k", new DateTime(2026, 11, 1)));
        Assert.Null(t.Points[0].Fps);
        Assert.Null(t.Points[0].VramPct);
        Assert.True(t.Points[0].HardwareError);
        Assert.Equal(40, t.Points[1].WorstFrameMs);
        Assert.Equal(2, t.Points[1].Stutters);
        Assert.Equal(3, t.FrameTimesMs.Count);
    }

    // ---------------------------------------------------------------- catálogo de ações

    [Fact]
    public void Catalog_is_a_closed_list_of_game_mode_options_without_sensitive_hardware()
    {
        var catalog = ActionCatalog.Build(new AppSettings(), () => Uniform(8), _ => { });
        Assert.Equal(new[] { ActionIds.AutoGameMode, ActionIds.ProBalance, ActionIds.EcoQos, ActionIds.PowerPlan, ActionIds.CpuSets, ActionIds.BackgroundCores },
                     catalog.Select(c => c.Spec.Id).ToArray());
        Assert.DoesNotContain(catalog, c => c.Spec.Risk == RiskClass.D);
        Assert.Equal(RiskClass.C, catalog.Single(c => c.Spec.Id == ActionIds.CpuSets).Spec.Risk);
        Assert.All(catalog, c =>
        {
            Assert.True(c.Spec.Reversible);
            Assert.False(c.Spec.NeedsRestart);
            Assert.NotEmpty(c.Spec.Changes);
            Assert.NotEmpty(c.Spec.HowToUndo);
            Assert.NotEmpty(c.Spec.Compatibility);
        });
    }

    [Fact]
    public void Action_flips_only_its_own_setting_and_reverts_to_the_previous_value()
    {
        var s = new AppSettings { GameModeProBalance = false, GameModeEcoQos = true };
        int saves = 0;
        var journal = new List<ActionRecord>();
        var executor = new ActionExecutor(ActionCatalog.Build(s, () => Uniform(8), _ => saves++), journal.Add);
        var before = JsonSerializer.Serialize(s);

        var r = executor.Apply(ActionIds.ProBalance, ActionScope.Global, AutomationPolicy.Default, userApprovedNow: true, "teste");
        Assert.Equal(ActionStatus.Applied, r.Status);
        Assert.True(s.GameModeProBalance);
        Assert.True(saves >= 1);
        Assert.Equal(ActionStatus.AlreadyEquivalent, executor.Apply(ActionIds.EcoQos, ActionScope.Global, AutomationPolicy.Default, true, "teste").Status);

        Assert.Equal(ActionStatus.Reverted, executor.Revert(r.Record!).Status);
        Assert.False(s.GameModeProBalance);
        Assert.Equal(before, JsonSerializer.Serialize(s)); // nada além do próprio campo mudou, e ele voltou
    }

    [Fact]
    public void Revert_respects_a_setting_the_user_changed_by_hand()
    {
        var s = new AppSettings { GameModeAffinity = false };
        var executor = new ActionExecutor(ActionCatalog.Build(s, () => CpuSetPlannerSample(16, hybrid: true), _ => { }), _ => { });
        var rec = executor.Apply(ActionIds.CpuSets, ActionScope.Global, AutomationPolicy.Default, true, "teste").Record!;
        Assert.True(s.GameModeAffinity);
        s.GameModeAffinity = false; // o usuário desligou na página do Modo Jogo
        Assert.Equal(ActionStatus.Reverted, executor.Revert(rec).Status); // já estava no valor anterior: só fecha o registro
        Assert.False(s.GameModeAffinity);
    }

    [Fact]
    public void Cpu_actions_are_incompatible_where_the_topology_does_not_justify_them()
    {
        var uniform = new ActionExecutor(ActionCatalog.Build(new AppSettings { GameModeAffinity = false, GameModeBgCores = false }, () => Uniform(8), _ => { }), _ => { });
        Assert.Equal(ActionStatus.Incompatible, uniform.Apply(ActionIds.CpuSets, ActionScope.Global, AutomationPolicy.Default, true, "teste").Status);
        Assert.Equal(ActionStatus.Incompatible, uniform.Apply(ActionIds.BackgroundCores, ActionScope.Global, AutomationPolicy.Default, true, "teste").Status);

        var hybrid = new ActionExecutor(ActionCatalog.Build(new AppSettings { GameModeAffinity = false, GameModeBgCores = false }, () => CpuSetPlannerSample(16, hybrid: true), _ => { }), _ => { });
        Assert.True(hybrid.Check(ActionIds.CpuSets, ActionScope.Global).Applicable);
        Assert.True(hybrid.Check(ActionIds.BackgroundCores, ActionScope.Global).Applicable);
    }

    [Fact]
    public void Settings_from_older_versions_get_safe_intelligence_defaults()
    {
        // Um settings.json da 1.9 não tem nenhum campo do Intelligence.
        var s = JsonSerializer.Deserialize<AppSettings>("{\"SettingsVersion\":3,\"AutoGameMode\":true}")!;
        Assert.Equal("recommend", s.IntelligenceAutomation);
        Assert.Empty(s.IntelligenceAutoActions);
        Assert.False(s.IntelligenceOnboardingDone);
        Assert.True(s.IntelligenceNetProbe);
        Assert.Equal(180, s.IntelligenceRetentionDays);
        Assert.True(s.AutoGameMode);
    }

    [Fact]
    public void Service_maps_the_automation_setting_and_never_defaults_to_automatic()
    {
        var s = new AppSettings();
        using var service = new IntelligenceService(s, Path.Combine(_dir, "i.json"), () => Uniform(8), _ => { });
        Assert.Equal(AutomationLevel.Recommend, service.Level);
        s.IntelligenceAutomation = "valor desconhecido";
        Assert.Equal(AutomationLevel.Recommend, service.Level);
        s.IntelligenceAutomation = "diagnosis";
        Assert.Equal(AutomationLevel.DiagnosisOnly, service.Level);
        s.IntelligenceAutomation = "safe-auto";
        s.IntelligenceAutoActions.Add(ActionIds.ProBalance);
        Assert.Equal(AutomationLevel.SafeAuto, service.Level);
        Assert.Contains(ActionIds.ProBalance, service.Policy.PreApproved);
        foreach (AutomationLevel level in Enum.GetValues<AutomationLevel>()) Assert.NotEmpty(IntelligenceService.LevelKey(level));
    }

    [Fact]
    public void Service_follows_the_session_states_and_shuts_down_cleanly_without_a_game_engine()
    {
        var s = new AppSettings { AutoGameMode = true };
        var service = new IntelligenceService(s, Path.Combine(_dir, "i.json"), () => Uniform(8), _ => { });
        service.Observe(null, capturing: false);
        Assert.Equal(SessionState.Detecting, service.Controller.State);
        s.AutoGameMode = false;
        service.Observe(null, capturing: false);
        Assert.Equal(SessionState.Idle, service.Controller.State);
        service.SelfTick(gaming: false);
        service.SelfTick(gaming: false);
        Assert.Equal(0, service.SelfImpactLevel);
        service.Dispose();
        service.Dispose();
        service.Observe(null, capturing: false); // depois de fechar, nada acontece
        Assert.Equal(SessionState.Idle, service.Controller.State);
    }

    [Fact]
    public void Applied_actions_land_in_the_local_journal()
    {
        var s = new AppSettings { GameModeProBalance = false };
        string path = Path.Combine(_dir, "i.json");
        using (var service = new IntelligenceService(s, path, () => Uniform(8), _ => { }))
        {
            var r = service.Executor.Apply(ActionIds.ProBalance, new ActionScope("Steam:1", "Jogo"), service.Policy, true, "programas de fundo");
            Assert.True(r.Ok);
        }
        var reopened = new IntelligenceStore(path);
        var rec = Assert.Single(reopened.Read(d => d.Journal.ToList()));
        Assert.Equal(ActionIds.ProBalance, rec.ActionId);
        Assert.Equal("off", rec.Before);
        Assert.True(rec.IsActive);
    }

    // ---------------------------------------------------------------- sonda de rede

    private static async Task<List<(double? Ms, bool Lost)>> Collect(SessionNetProbe probe, int count)
    {
        var got = new List<(double?, bool)>();
        var limit = DateTime.UtcNow.AddSeconds(10);
        while (got.Count < count && DateTime.UtcNow < limit)
        {
            if (probe.TryTake(out double? ms, out bool lost)) got.Add((ms, lost));
            else await Task.Delay(5);
        }
        return got;
    }

    [Fact]
    public async Task Probe_reports_latency_and_loss_from_the_gateway()
    {
        int n = 0;
        using var probe = new SessionNetProbe((_, _) => Task.FromResult(Interlocked.Increment(ref n) % 3 == 0 ? (false, 0L) : (true, 12L)));
        probe.Start(IPAddress.Parse("192.168.0.1"), TimeSpan.FromMilliseconds(10));
        var got = await Collect(probe, 6);
        probe.Stop();
        Assert.Equal(6, got.Count);
        Assert.Contains(got, g => g is { Ms: 12, Lost: false });
        Assert.Contains(got, g => g is { Ms: null, Lost: true });
        Assert.Equal("roteador", probe.Target);
        Assert.False(probe.TryTake(out _, out _));
    }

    [Fact]
    public async Task Gateway_that_never_answers_ping_is_not_counted_as_packet_loss()
    {
        var gateway = IPAddress.Parse("192.168.0.1");
        using var probe = new SessionNetProbe((address, _) => Task.FromResult(address.Equals(gateway) ? (false, 0L) : (true, 30L)));
        probe.Start(gateway, TimeSpan.FromMilliseconds(10));
        var got = await Collect(probe, 3);
        probe.Stop();
        Assert.NotEmpty(got);
        Assert.All(got, g => Assert.False(g.Lost));
        Assert.Equal("servidor público de referência", probe.Target);
    }

    [Fact]
    public async Task Probe_survives_exceptions_and_stops_on_request()
    {
        using var probe = new SessionNetProbe((_, _) => throw new System.Net.NetworkInformation.PingException("sem rede"));
        probe.Start(IPAddress.Parse("192.168.0.1"), TimeSpan.FromMilliseconds(10));
        await Task.Delay(100);
        probe.Stop();
        probe.Stop();
        Assert.False(probe.TryTake(out _, out bool lost) && !lost); // nunca respondeu: nada de latência inventada
    }

    // ---------------------------------------------------------------- inventário

    [Fact]
    public void Machine_profile_is_built_from_what_the_app_already_read_and_leaves_identifiers_out()
    {
        var system = new InfoNode { Header = "Sistema" };
        system.Add("Nome", "Windows 11");
        system.Add("Nome do computador", "PC-DO-FULANO");
        var board = system.Add("Placa-mãe");
        board.Add("Modelo", "B650");
        board.Add("Nº de série", "ABC123");
        var video = new InfoNode { Header = "Vídeo" };
        var gpu = video.Add("RX 9070 XT");
        gpu.Add("Resolução atual", "2560x1440 @ 144 Hz");
        gpu.Add("Versão do driver", "");

        var m = MachineProfileBuilder.Build(new[] { system, video }, Array.Empty<GpuInfo>(), CpuSetPlannerSample(16, hybrid: true), monitor: null,
                                            new GamerAudit.PowerState(false, false, 0), "Core i7", frameCapture: false, new DateTime(2026, 11, 1));
        Assert.Equal("2560x1440@144", m.Resolution);
        Assert.Equal(144, m.RefreshHz);
        Assert.True(m.IsHybridCpu);
        Assert.Equal(16, m.LogicalProcessors);
        Assert.False(m.IsLaptop);
        Assert.StartsWith("Core i7", m.HardwareId);
        var all = m.Groups.SelectMany(g => g.Items).ToList();
        Assert.DoesNotContain(all, c => c.Value.Contains("PC-DO-FULANO") || c.Value.Contains("ABC123"));
        Assert.Contains(all, c => c.Name.Contains("Versão do driver") && c.State == ReadingState.Unavailable);
        Assert.Contains(m.Sensors, c => c.Name.StartsWith("GPU Busy") && c.State == ReadingState.Unsupported);
        Assert.Contains(m.Sensors, c => c.Name == "Temperatura da CPU" && c.State == ReadingState.Unavailable);
        Assert.NotEmpty(m.Blind());
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics 770", true)]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("NVIDIA GeForce RTX 4070", false)]
    [InlineData("AMD Radeon RX 9070 XT", false)]
    public void Integrated_graphics_are_recognized_by_name(string name, bool integrated) => Assert.Equal(integrated, MachineProfileBuilder.IsIntegrated(name));

    // ---------------------------------------------------------------- overlay e navegação

    [Fact]
    public void Overlay_offers_the_limiter_and_profile_lines_off_by_default()
    {
        var limiter = OverlayMetrics.All.Single(m => m.Key == "limiter");
        var profile = OverlayMetrics.All.Single(m => m.Key == "intel_profile");
        Assert.False(limiter.Default);
        Assert.False(profile.Default);
        Assert.True(OverlayMetrics.NeedsLoadSample(new HashSet<string> { "limiter" }));
        Assert.False(OverlayMetrics.NeedsLoadSample(new HashSet<string> { "intel_profile" }));
    }

    [Fact]
    public void Every_destination_of_a_recommendation_is_a_real_page_or_a_settings_uri()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoText.Root, "MainWindow.xaml"));
        foreach (string destination in Destinations.All)
        {
            Assert.NotEmpty(Destinations.Label(destination));
            if (destination.StartsWith("page:", StringComparison.Ordinal)) Assert.Contains($"x:Name=\"{destination[5..]}\"", xaml);
            else Assert.StartsWith("uri:ms-settings:", destination);
        }
    }

    [Fact]
    public void Intelligence_pages_are_wired_into_navigation()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoText.Root, "MainWindow.xaml"));
        string areas = File.ReadAllText(Path.Combine(RepoText.Root, "MainWindow.Areas.cs"));
        string window = File.ReadAllText(Path.Combine(RepoText.Root, "MainWindow.xaml.cs"));
        string code = File.ReadAllText(Path.Combine(RepoText.Root, "MainWindow.Intelligence.cs"));
        foreach (string page in new[] { "PageIntel", "PageIntelGames", "PageIntelPc" })
        {
            Assert.Contains($"x:Name=\"{page}\"", xaml);
            Assert.Contains($"Tag=\"{page}\"", xaml);
            Assert.Contains($"new AreaTab(\"{page}\"", areas);
            Assert.Contains($"case \"{page}\":", window);
        }
        // Todo manipulador citado no XAML das páginas novas existe no código: nenhum botão sem função.
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xaml, @"Click=""((?:BtnIntel|Intel|BtnDashIntel)\w*)"""))
            Assert.Contains($"void {m.Groups[1].Value}(", code);
        Assert.Contains("SelectionChanged=\"IntelGameList_SelectionChanged\"", xaml);
    }

    [Fact]
    public void Real_overclock_stays_disabled()
    {
        Assert.False(Core.Oc.OcGate.RealHardwareEnabled);
    }
}
