using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using ThgOptimization.Core.Benchmark;
using ThgOptimization.Core.Intelligence;
using ThgOptimization.Infrastructure.Intelligence;

namespace ThgOptimization.Tests;

[Collection(WpfCollection.Name)]
public class Release210Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "thg-210-" + Guid.NewGuid().ToString("N"));

    public Release210Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Version_is_2_1_0()
    {
        Assert.Equal("2.1.0", AppUpdater.Current.ToString());
    }

    [Fact]
    public void Session_sample_210_columns_serialize_and_remain_backwards_compatible()
    {
        string legacyJson = "{\"TimestampMs\":1000,\"FrametimeMs\":8.3,\"Fps\":120,\"CpuTemp\":60,\"CpuLoad\":30,\"CpuClock\":5000,\"CpuPower\":70,\"GpuTemp\":65,\"GpuLoad\":97," +
                            "\"GpuClock\":2100,\"GpuPower\":200,\"VramPct\":50,\"RamPct\":40,\"FanCpuRpm\":null,\"Throttled\":false,\"StuttersThisSecond\":0,\"WheaError\":false}";
        var legacy = JsonSerializer.Deserialize<SessionSample>(legacyJson)!;
        Assert.Null(legacy.DiskIoMbPerSec);
        Assert.Null(legacy.PageFaultsPerSec);

        var store = new SessionStore(_dir);
        var clock = new DateTime(2026, 11, 22, 20, 0, 0);
        var rec = new SessionRecorder(store, () => clock);
        rec.Begin("Jogo21");
        for (int i = 0; i < 4; i++)
        {
            clock = clock.AddSeconds(1);
            rec.Add(9.5, 110, 62, 35, 5000, 75, 64, 92, 2100, 190, 55, 48, 0, false, false,
                    cpuMaxCore: 82, commitPct: 52, diskIoMbPerSec: 42.5, pageFaultsPerSec: 12.0);
        }
        var saved = rec.End()!;
        var loaded = SessionStore.Load(saved.FilePath!)!;
        Assert.Equal(42.5, loaded.Samples[0].DiskIoMbPerSec);
        Assert.Equal(12.0, loaded.Samples[0].PageFaultsPerSec);

        var telemetry = SessionTelemetryAdapter.From(loaded, IntelSim.Context("Jogo21"));
        Assert.Equal(42.5, telemetry.Points[0].DiskIoMbPerSec);
        Assert.Equal(12.0, telemetry.Points[0].PageFaultsPerSec);
    }

    [Fact]
    public void Early_warmup_stutters_are_classified_as_shader_stutter_instead_of_frame_pacing()
    {
        var earlySpikes = Enumerable.Range(0, 22).Select(k => 15 + k * 8).ToArray();
        var (frames, mark) = IntelSim.Frames(12, earlySpikes, spikeMs: 90f);
        var r = IntelSim.Diagnose(IntelSim.Session(12, mark, frames));
        var shader = r.Find(DiagnosisKind.ShaderStutter);
        Assert.NotNull(shader);
        Assert.Equal(Severity.Info, shader!.Severity);
        Assert.Null(r.Find(DiagnosisKind.FramePacing));
    }

    [Fact]
    public void Disk_io_spikes_correlating_with_stutters_trigger_storage_finding_even_on_ssd()
    {
        var spikes = Enumerable.Range(0, 36).Select(k => 40 + k * 22).ToHashSet();
        var (frames, mark) = IntelSim.Frames(15, spikes);
        var ext = new ExternalEvidence { GameOnHdd = false };
        var r = IntelSim.Diagnose(IntelSim.Session(15, (i, p) => mark(i, p) with { DiskIoMbPerSec = spikes.Contains(i) ? 160 : 5 }, frames, ext));
        var f = r.Find(DiagnosisKind.Storage);
        Assert.NotNull(f);
        Assert.Equal(Severity.Problem, f!.Severity);
        Assert.Contains(f.Evidence, e => e.Metric == "Atividade de disco");
    }

    [Fact]
    public void Hard_page_faults_correlating_with_stutters_trigger_memory_pressure()
    {
        var spikes = Enumerable.Range(0, 36).Select(k => 40 + k * 22).ToHashSet();
        var (frames, mark) = IntelSim.Frames(15, spikes);
        var r = IntelSim.Diagnose(IntelSim.Session(15, (i, p) => mark(i, p) with { RamPct = 78, CommitPct = 70, PageFaultsPerSec = spikes.Contains(i) ? 140 : 2 }, frames));
        var f = r.Find(DiagnosisKind.MemoryPressure);
        Assert.NotNull(f);
        Assert.Equal(Severity.Problem, f!.Severity);
        Assert.Contains(f.Evidence, e => e.Metric.Contains("page faults", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Session_health_score_rewards_healthy_sessions_and_penalizes_stability_and_thermal()
    {
        var (frames, mark) = IntelSim.Frames(15, Array.Empty<int>());
        var healthy = IntelSim.Session(15, mark, frames);
        var healthyScore = SessionHealthScore.Evaluate(IntelSim.Diagnose(healthy), healthy);
        Assert.True(healthyScore.Score >= 90);
        Assert.Equal("A", healthyScore.Grade);

        var unstable = IntelSim.Session(15, (i, p) => mark(i, p) with { HardwareError = i == 100 }, frames);
        var unstableScore = SessionHealthScore.Evaluate(IntelSim.Diagnose(unstable), unstable);
        Assert.Equal(0, unstableScore.StabilityScore);
        Assert.True(unstableScore.Score <= 80);

        var summary = SessionSummary.From(healthy, IntelSim.Diagnose(healthy));
        Assert.Equal(healthyScore.Score, summary.HealthScore);
        Assert.NotNull(summary.FpsPerWatt);
        Assert.True(summary.FpsPerWatt > 0);
    }

    [Fact]
    public void Learning_persists_held_outlier_direction_across_store_reload()
    {
        string path = Path.Combine(_dir, "intel.json");
        var engine = new LearningEngine();
        var t = IntelSim.T0;

        static SessionSummary Make(DateTime at, double fps) =>
            new(at, "hw|drv|res|ac", 10) { AvgFps = fps, Low1Fps = fps * 0.7, P99Ms = 1000 / Math.Max(1, fps * 0.7) };

        var store1 = new IntelligenceStore(path);
        store1.Mutate(d =>
        {
            var g = store1.Game("Steam:1", "Jogo");
            for (int i = 0; i < 4; i++) engine.Learn(g.Baselines, Make(t.AddHours(i), 100));
            // Duas sessões piores retidas
            Assert.Equal(LearningOutcomeKind.OutlierHeld, engine.Learn(g.Baselines, Make(t.AddHours(5), 60)).Kind);
            Assert.Equal(LearningOutcomeKind.OutlierHeld, engine.Learn(g.Baselines, Make(t.AddHours(6), 62)).Kind);
        });

        // Reabre em um novo processo (store + LearningEngine novos): um desvio na direção oposta deve reiniciar a contagem, não reconstruir!
        var store2 = new IntelligenceStore(path);
        var engine2 = new LearningEngine();
        LearningOutcome? flip = null;
        store2.Mutate(d =>
        {
            var g = d.Games["Steam:1"];
            Assert.True(g.Baselines[0].HeldWorse == true);
            flip = engine2.Learn(g.Baselines, Make(t.AddHours(7), 150));
            Assert.Equal(LearningOutcomeKind.OutlierHeld, flip.Kind);
            Assert.Single(g.Baselines[0].Held);
            Assert.False(g.Baselines[0].HeldWorse);
        });
    }

    [Fact]
    public void Expanded_action_catalog_applies_reverts_and_recommends_with_evidence()
    {
        var s = new AppSettings { GameModeTimer = false, GameModeCleanRam = false, GameModeAutoEconomyOverlay = false };
        var journal = new List<ActionRecord>();
        var executor = new ActionExecutor(ActionCatalog.Build(s, save: _ => { }), journal.Add);
        var recommender = new RecommendationEngine(executor);

        foreach (string id in new[] { ActionIds.TimerResolution, ActionIds.CleanRam, ActionIds.EconomyOverlay })
        {
            var res = executor.Apply(id, ActionScope.Global, AutomationPolicy.Default, userApprovedNow: true, "teste 2.1");
            Assert.Equal(ActionStatus.Applied, res.Status);
            Assert.Equal(ActionStatus.Reverted, executor.Revert(res.Record!).Status);
        }
        Assert.False(s.GameModeTimer);
        Assert.False(s.GameModeCleanRam);
        Assert.False(s.GameModeAutoEconomyOverlay);

        var memReport = IntelSim.Diagnose(IntelSim.Session(15, (_, p) => p with { RamPct = 96 }));
        Assert.Contains(recommender.Recommend(memReport, null, UsagePreset.Balanced, ActionScope.Global), r => r.ActionId == ActionIds.CleanRam);

        var (frames, mark) = IntelSim.Frames(15, Enumerable.Range(0, 60).Select(k => 30 + k * 14));
        var pacingReport = IntelSim.Diagnose(IntelSim.Session(15, mark, frames));
        Assert.Contains(recommender.Recommend(pacingReport, null, UsagePreset.Competitive, ActionScope.Global), r => r.ActionId == ActionIds.TimerResolution);
        Assert.False(UsagePreset.Quiet.Suggests(ActionIds.TimerResolution));
    }

    [Fact]
    public void Frametime_statistics_populates_bootstrap_ci_and_report_renders_it()
    {
        var a = new[] { 100.0, 101.0, 99.0 }.Select((fps, i) =>
            new BenchmarkSession("CS2", "A", DateTime.Now.AddMinutes(i), fps, 70, 55, 14, 0.2, 9.9, 6000, 60, null) { P95Ms = 12, P999Ms = 20 }).ToList();
        var b = new[] { 120.0, 121.0, 119.0 }.Select((fps, i) =>
            new BenchmarkSession("CS2", "B", DateTime.Now.AddMinutes(10 + i), fps, 85, 68, 11, 0.1, 8.2, 7200, 60, null) { P95Ms = 10, P999Ms = 16 }).ToList();

        var cmp = FrameTimeStatistics.Compare(a, b, aIsBaseline: true);
        Assert.NotNull(cmp.AvgFpsCiLow);
        Assert.NotNull(cmp.AvgFpsCiHigh);
        Assert.True(cmp.AvgFpsCiLow > 0);
        Assert.NotNull(cmp.NoiseFloorPct);

        string html = BenchmarkReport.BuildHtml(cmp, null);
        Assert.Contains("Bootstrap IC 95%", html);
    }

    [Fact]
    public void Intelligence_html_report_encodes_untrusted_text_and_omits_machine_identifiers()
    {
        var ctx = IntelSim.Context("<script>alert(1)</script>") with { HardwareId = "Ryzen 7 + RTX 4070 + 32 GB" };
        var session = IntelSim.Session(15, context: ctx);
        var report = IntelSim.Diagnose(session);
        var health = SessionHealthScore.Evaluate(report, session);
        var machine = new MachineProfile(IntelSim.T0, ctx.HardwareId, Array.Empty<CapabilityGroup>());

        string html = IntelligenceHtmlReport.Build(report, health, null, Array.Empty<Recommendation>(), machine);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains($"{health.Score}/100", html);
        Assert.Contains("números de série e nome do computador são omitidos", html);
    }

    [Fact]
    public void Timeline_viewport_focus_centers_and_clamps_bounds()
    {
        var vp = new TimelineViewport(120_000);
        vp.Focus(60_000, 16_000);
        Assert.Equal(52_000, vp.StartMs);
        Assert.Equal(68_000, vp.EndMs);

        vp.Focus(2_000, 16_000);
        Assert.Equal(0, vp.StartMs);
        Assert.Equal(16_000, vp.EndMs);

        vp.Focus(119_000, 16_000);
        Assert.Equal(104_000, vp.StartMs);
        Assert.Equal(120_000, vp.EndMs);
    }

    [Fact]
    public void Overlay_and_theme_and_tweak_metadata_contracts_hold()
    {
        var defaults = OverlayMetrics.Defaults();
        foreach (string key in new[] { "fps_per_watt", "net_ping", "disk_io" })
        {
            Assert.Contains(OverlayMetrics.All, m => m.Key == key);
            Assert.DoesNotContain(key, defaults);
        }

        Assert.Equal("obsidian", ThemeManager.Obsidian);
        var settings = new AppSettings();
        Assert.Equal("card", settings.OverlayLayout);
        Assert.False(settings.GameModeAutoEconomyOverlay);

        var t = new Tweak { Title = "Teste", Impact = TweakImpact.High, Safety = TweakSafety.Safe, AntiCheatSafe = true };
        Assert.Equal("IMPACTO ALTO", t.EvidenceBadge);
        Assert.Equal("REVERSÍVEL · ANTI-CHEAT OK", t.SafetyBadge);
        Assert.Equal("", t.AppliedBadge);
        t.IsOn = true;
        Assert.Equal("APLICADO", t.AppliedBadge);
    }

    [Fact]
    public void Main_window_and_overlay_xaml_contain_210_controls()
    {
        XamlSmokeTests.OnSta(() =>
        {
            XamlSmokeTests.EnsureTheme();
            var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/tHgOptimization;component/") };
            string mainPath = Path.Combine(RepoText.Root, "MainWindow.xaml");
            var win = (Window)XamlReader.Parse(XamlSmokeTests.Loose(File.ReadAllText(mainPath)), context);
            foreach (string name in new[]
            {
                "DashIntelScorePill", "DashIntelScore", "BtnDashIntelEnable",
                "LabDistChart", "HistoryTrackLoad", "HistoryTrackMem", "HistoryTrackNet",
                "HistoryCompareCombo", "HistoryCompareText", "OvLayoutPanel", "SetThemeObsidian"
            })
                Assert.NotNull(win.FindName(name));

            var overlay = new OverlayWindow();
            overlay.LayoutMode = "bar";
            overlay.Update(null, Array.Empty<OverlayLine>(), new HashSet<string> { "fps" }, null);
            overlay.Close();
        });
    }
}
