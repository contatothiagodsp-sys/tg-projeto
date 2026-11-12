using System.IO;
using ThgOptimization.Core.Intelligence;

namespace ThgOptimization.Tests;

/// <summary>Alvo de ação em memória: um estado, com as falhas que os testes pedem.</summary>
internal sealed class FakeTarget : IActionTarget
{
    public string? State = "off";
    public string Want = "on";
    public string? Incompatible;
    public Func<string, Exception?>? FailWrite;
    /// <summary>A escrita "funciona" mas o valor não muda (política de grupo, outro programa revertendo).</summary>
    public bool Ignore;
    public TimeSpan Delay;
    public int Writes;

    public string? Incompatibility(ActionScope scope) => Incompatible;
    public string? Read(ActionScope scope) => State;
    public string Desired(ActionScope scope) => Want;

    public void Write(ActionScope scope, string state, CancellationToken ct)
    {
        Writes++;
        if (Delay > TimeSpan.Zero) ct.WaitHandle.WaitOne(Delay);
        ct.ThrowIfCancellationRequested();
        if (FailWrite?.Invoke(state) is Exception ex) throw ex;
        if (!Ignore) State = state;
    }
}

internal sealed class IntelRig
{
    public readonly List<ActionRecord> Journal = new();
    public readonly Dictionary<string, FakeTarget> Targets = new();
    public readonly ActionExecutor Executor;
    public static readonly AutomationPolicy Recommend = AutomationPolicy.Default;

    public IntelRig(params ActionSpec[] extra)
    {
        var specs = new List<ActionSpec>
        {
            Spec(ActionIds.ProBalance, RiskClass.B, DiagnosisKind.ConcurrentProcesses),
            Spec(ActionIds.EcoQos, RiskClass.B, DiagnosisKind.ConcurrentProcesses),
            Spec(ActionIds.PowerPlan, RiskClass.B, DiagnosisKind.PowerLimit),
            Spec(ActionIds.CpuSets, RiskClass.C, DiagnosisKind.MainThreadBound, DiagnosisKind.CpuBound),
            Spec(ActionIds.BackgroundCores, RiskClass.B, DiagnosisKind.MainThreadBound, DiagnosisKind.CpuBound),
            Spec("oc.clock", RiskClass.D),
        };
        specs.AddRange(extra);
        foreach (var s in specs) Targets[s.Id] = new FakeTarget();
        Executor = new ActionExecutor(specs.Select(s => (s, (IActionTarget)Targets[s.Id])), Upsert, () => IntelSim.T0);
    }

    public static ActionSpec Spec(string id, RiskClass risk, params DiagnosisKind[] evidence) =>
        new(id, "Ação " + id, "descrição", "objetivo.", risk) { Evidence = evidence, Timeout = TimeSpan.FromSeconds(2) };

    private void Upsert(ActionRecord r)
    {
        if (!Journal.Contains(r)) Journal.Insert(0, r);
    }

    public static readonly ActionScope Game = new("Steam:1", "Jogo", @"C:\jogo\jogo.exe");

    public ActionResult Apply(string id, bool approved = true, AutomationPolicy? policy = null, CancellationToken ct = default) =>
        Executor.Apply(id, Game, policy ?? Recommend, approved, "teste", ct: ct);
}

public class IntelligenceActionTests
{
    [Fact]
    public void Apply_records_the_previous_state_and_confirms_the_new_one()
    {
        var rig = new IntelRig();
        var r = rig.Apply(ActionIds.ProBalance);
        Assert.Equal(ActionStatus.Applied, r.Status);
        Assert.Equal("on", rig.Targets[ActionIds.ProBalance].State);
        var rec = Assert.Single(rig.Journal);
        Assert.Equal("off", rec.Before);
        Assert.Equal("on", rec.Applied);
        Assert.True(rec.IsActive);
        Assert.False(rec.Automatic);
    }

    [Fact]
    public void Applying_twice_is_idempotent()
    {
        var rig = new IntelRig();
        rig.Apply(ActionIds.ProBalance);
        var again = rig.Apply(ActionIds.ProBalance);
        Assert.Equal(ActionStatus.AlreadyEquivalent, again.Status);
        Assert.Single(rig.Journal);
        Assert.Equal(1, rig.Targets[ActionIds.ProBalance].Writes);
    }

    [Fact]
    public void Unknown_action_is_refused_there_is_no_free_form_command()
    {
        var rig = new IntelRig();
        Assert.Equal(ActionStatus.Refused, rig.Apply("cmd /c qualquer coisa").Status);
        Assert.Empty(rig.Journal);
    }

    [Fact]
    public void Sensitive_hardware_class_is_never_applied_even_with_approval()
    {
        var rig = new IntelRig();
        var r = rig.Apply("oc.clock", approved: true, policy: new AutomationPolicy(AutomationLevel.SafeAuto, new HashSet<string> { "oc.clock" }));
        Assert.Equal(ActionStatus.Refused, r.Status);
        Assert.Equal(0, rig.Targets["oc.clock"].Writes);
    }

    [Fact]
    public void Advanced_action_needs_explicit_approval_and_cannot_be_pre_approved()
    {
        var rig = new IntelRig();
        var auto = new AutomationPolicy(AutomationLevel.SafeAuto, new HashSet<string> { ActionIds.CpuSets });
        Assert.Equal(ActionStatus.Refused, rig.Apply(ActionIds.CpuSets, approved: false, policy: auto).Status);
        Assert.Equal(ActionStatus.Applied, rig.Apply(ActionIds.CpuSets, approved: true, policy: auto).Status);
    }

    [Fact]
    public void Automation_off_refuses_everything_that_writes()
    {
        var rig = new IntelRig();
        var off = new AutomationPolicy(AutomationLevel.DiagnosisOnly, new HashSet<string>());
        Assert.Equal(ActionStatus.Refused, rig.Apply(ActionIds.ProBalance, approved: true, policy: off).Status);
        Assert.Equal("off", rig.Targets[ActionIds.ProBalance].State);
    }

    [Fact]
    public void Safe_auto_applies_only_what_the_user_pre_approved()
    {
        var rig = new IntelRig();
        var auto = new AutomationPolicy(AutomationLevel.SafeAuto, new HashSet<string> { ActionIds.ProBalance });
        Assert.Equal(ActionStatus.Applied, rig.Apply(ActionIds.ProBalance, approved: false, policy: auto).Status);
        Assert.True(rig.Journal[0].Automatic);
        Assert.Equal(ActionStatus.Refused, rig.Apply(ActionIds.EcoQos, approved: false, policy: auto).Status);
        Assert.Equal(ActionStatus.Refused, rig.Apply(ActionIds.PowerPlan, approved: false, policy: IntelRig.Recommend).Status);
    }

    [Fact]
    public void Incompatible_target_changes_nothing()
    {
        var rig = new IntelRig();
        rig.Targets[ActionIds.CpuSets].Incompatible = "CPU sem núcleos diferentes";
        var r = rig.Apply(ActionIds.CpuSets);
        Assert.Equal(ActionStatus.Incompatible, r.Status);
        Assert.Contains("núcleos", r.Message);
        Assert.Empty(rig.Journal);
    }

    [Fact]
    public void Unreadable_state_stops_before_any_write()
    {
        var rig = new IntelRig();
        rig.Targets[ActionIds.ProBalance].State = null;
        Assert.Equal(ActionStatus.Failed, rig.Apply(ActionIds.ProBalance).Status);
        Assert.Equal(0, rig.Targets[ActionIds.ProBalance].Writes);
    }

    [Fact]
    public void Failed_write_restores_the_previous_state_and_does_not_claim_success()
    {
        var rig = new IntelRig();
        rig.Targets[ActionIds.ProBalance].FailWrite = state => state == "on" ? new UnauthorizedAccessException("acesso negado") : null;
        var r = rig.Apply(ActionIds.ProBalance);
        Assert.Equal(ActionStatus.RolledBack, r.Status);
        Assert.False(r.Ok);
        Assert.Equal("off", rig.Targets[ActionIds.ProBalance].State);
        Assert.Contains("acesso negado", r.Message);
        Assert.False(rig.Journal[0].IsActive);
    }

    [Fact]
    public void Write_that_does_not_take_effect_is_detected_by_the_confirmation()
    {
        var rig = new IntelRig();
        rig.Targets[ActionIds.ProBalance].Ignore = true;
        var r = rig.Apply(ActionIds.ProBalance);
        Assert.Equal(ActionStatus.RolledBack, r.Status);
        Assert.Contains("esperado", r.Message);
    }

    [Fact]
    public void Partial_failure_with_failed_restore_is_reported_with_the_original_value()
    {
        var rig = new IntelRig();
        var t = rig.Targets[ActionIds.ProBalance];
        int n = 0;
        t.FailWrite = _ => ++n == 1 ? null : new IOException("disco");
        t.Want = "on";
        // A primeira escrita "funciona" mas deixa um estado errado; a restauração falha.
        t.Ignore = false;
        t.FailWrite = state => state == "on" ? MakeWrong(t) : new IOException("disco");
        var r = rig.Apply(ActionIds.ProBalance);
        Assert.Equal(ActionStatus.RollbackFailed, r.Status);
        Assert.Contains("Valor anterior: off", r.Message);

        static Exception? MakeWrong(FakeTarget t) { t.State = "meio"; return new InvalidOperationException("parou no meio"); }
    }

    [Fact]
    public void Write_that_hangs_times_out_and_is_reported_as_failure()
    {
        var rig = new IntelRig(IntelRig.Spec("lenta", RiskClass.B) with { Timeout = TimeSpan.FromMilliseconds(150) });
        rig.Targets["lenta"].Delay = TimeSpan.FromSeconds(30);
        var r = rig.Apply("lenta");
        Assert.False(r.Ok);
        Assert.Contains("tempo esgotado", r.Message);
        Assert.Equal("off", rig.Targets["lenta"].State);
    }

    [Fact]
    public void Cancellation_before_the_write_changes_nothing()
    {
        var rig = new IntelRig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(ActionStatus.Cancelled, rig.Apply(ActionIds.ProBalance, ct: cts.Token).Status);
        Assert.Equal(0, rig.Targets[ActionIds.ProBalance].Writes);
        Assert.Empty(rig.Journal);
    }

    [Fact]
    public void Revert_returns_to_the_original_state()
    {
        var rig = new IntelRig();
        var rec = rig.Apply(ActionIds.ProBalance).Record!;
        var r = rig.Executor.Revert(rec);
        Assert.Equal(ActionStatus.Reverted, r.Status);
        Assert.Equal("off", rig.Targets[ActionIds.ProBalance].State);
        Assert.False(rec.IsActive);
        Assert.Equal(ActionStatus.NothingToRevert, rig.Executor.Revert(rec).Status);
    }

    [Fact]
    public void Revert_does_not_overwrite_a_value_changed_by_someone_else()
    {
        var rig = new IntelRig();
        var rec = rig.Apply(ActionIds.ProBalance).Record!;
        rig.Targets[ActionIds.ProBalance].State = "custom"; // outro programa, ou o usuário, mudou depois
        var r = rig.Executor.Revert(rec);
        Assert.Equal(ActionStatus.Conflict, r.Status);
        Assert.Equal("custom", rig.Targets[ActionIds.ProBalance].State);
        Assert.Contains("Valor original: off", r.Message);
    }

    [Fact]
    public void Revert_of_something_already_undone_by_hand_just_closes_the_record()
    {
        var rig = new IntelRig();
        var rec = rig.Apply(ActionIds.ProBalance).Record!;
        rig.Targets[ActionIds.ProBalance].State = "off";
        int writes = rig.Targets[ActionIds.ProBalance].Writes;
        Assert.Equal(ActionStatus.Reverted, rig.Executor.Revert(rec).Status);
        Assert.Equal(writes, rig.Targets[ActionIds.ProBalance].Writes);
    }

    [Fact]
    public void Per_game_action_requires_a_game()
    {
        var rig = new IntelRig(IntelRig.Spec("por.jogo", RiskClass.B) with { PerGame = true });
        Assert.Equal(ActionStatus.Refused, rig.Executor.Apply("por.jogo", ActionScope.Global, IntelRig.Recommend, true, "teste").Status);
        Assert.Equal(ActionStatus.Applied, rig.Apply("por.jogo").Status);
    }

    [Fact]
    public void Check_reports_equivalent_configuration()
    {
        var rig = new IntelRig();
        Assert.True(rig.Executor.Check(ActionIds.ProBalance, IntelRig.Game).Applicable);
        rig.Targets[ActionIds.ProBalance].State = "on";
        var (applicable, why) = rig.Executor.Check(ActionIds.ProBalance, IntelRig.Game);
        Assert.False(applicable);
        Assert.Contains("equivalente", why);
    }
}

public class IntelligenceSessionControllerTests
{
    private sealed class Probe
    {
        public readonly List<string> Calls = new();
        public readonly List<SessionState> States = new();
        public Exception? PrepareError, DiagnoseError, RestoreError;
        public GameSessionController Controller;

        public Probe()
        {
            Controller = new GameSessionController(new SessionHooks
            {
                Prepare = s => { Calls.Add("prepare:" + s.GameName); if (PrepareError != null) throw PrepareError; },
                Diagnose = s => { Calls.Add("diagnose:" + s.GameName); if (DiagnoseError != null) throw DiagnoseError; },
                Restore = s => { Calls.Add("restore:" + s.GameName); if (RestoreError != null) throw RestoreError; },
            }, () => IntelSim.T0);
            Controller.StateChanged += (_, to) => States.Add(to);
        }
    }

    private static readonly GameObservation Waiting = new(true, null, null, null);
    private static GameObservation Playing(int pid = 10, string name = "CS2", bool capturing = false) => new(true, pid, name, "Steam:730", capturing);

    [Fact]
    public void Full_session_goes_through_the_states_in_order()
    {
        var p = new Probe();
        p.Controller.Observe(Waiting);
        p.Controller.Observe(Playing());
        Assert.Equal(SessionState.Gaming, p.Controller.State);
        Assert.NotNull(p.Controller.Current);
        p.Controller.Observe(Waiting);
        Assert.Equal(new[] { SessionState.Detecting, SessionState.Preparing, SessionState.Gaming, SessionState.Diagnosing, SessionState.Restoring, SessionState.Detecting }, p.States);
        Assert.Equal(new[] { "prepare:CS2", "diagnose:CS2", "restore:CS2" }, p.Calls);
        Assert.Null(p.Controller.Current);
        Assert.NotNull(p.Controller.Last!.EndedAt);
    }

    [Fact]
    public void Repeated_observation_does_not_repeat_work()
    {
        var p = new Probe();
        for (int i = 0; i < 5; i++) p.Controller.Observe(Playing());
        Assert.Single(p.Calls);
        for (int i = 0; i < 5; i++) p.Controller.Observe(Waiting);
        Assert.Equal(3, p.Calls.Count);
    }

    [Fact]
    public void Ordinary_program_without_a_recognized_game_never_starts_a_session()
    {
        var p = new Probe();
        p.Controller.Observe(new GameObservation(true, 50, null, null)); // PID sem nome de jogo: a detecção não reconheceu
        Assert.Equal(SessionState.Detecting, p.Controller.State);
        Assert.Empty(p.Calls);
    }

    [Fact]
    public void Another_game_between_two_ticks_closes_one_session_before_opening_the_next()
    {
        var p = new Probe();
        p.Controller.Observe(Playing(10, "CS2"));
        p.Controller.Observe(Playing(22, "Valorant"));
        Assert.Equal(new[] { "prepare:CS2", "diagnose:CS2", "restore:CS2", "prepare:Valorant" }, p.Calls);
        Assert.Equal(22, p.Controller.Current!.Pid);
    }

    [Fact]
    public void Capture_is_a_state_inside_the_session()
    {
        var p = new Probe();
        p.Controller.Observe(Playing());
        p.Controller.Observe(Playing(capturing: true));
        Assert.Equal(SessionState.Capturing, p.Controller.State);
        p.Controller.Observe(Playing());
        Assert.Equal(SessionState.Gaming, p.Controller.State);
        Assert.Single(p.Calls);
    }

    [Fact]
    public void Game_that_vanishes_abruptly_still_gets_diagnosed_and_restored()
    {
        var p = new Probe();
        p.Controller.Observe(Playing(capturing: true));
        p.Controller.Observe(new GameObservation(false, null, null, null)); // processo sumiu e a detecção foi desligada
        Assert.Equal(SessionState.Idle, p.Controller.State);
        Assert.Contains("restore:CS2", p.Calls);
    }

    [Fact]
    public void Failure_while_preparing_restores_and_does_not_leave_a_half_session()
    {
        var p = new Probe { PrepareError = new InvalidOperationException("falhou") };
        p.Controller.Observe(Playing());
        Assert.Equal(new[] { "prepare:CS2", "restore:CS2" }, p.Calls);
        Assert.Null(p.Controller.Current);
        Assert.Contains("preparar", p.Controller.LastError);
        Assert.Contains(SessionState.Error, p.States);
        Assert.Equal(SessionState.Detecting, p.Controller.State);
    }

    [Fact]
    public void Failure_while_diagnosing_never_skips_the_restore()
    {
        var p = new Probe { DiagnoseError = new IOException("disco") };
        p.Controller.Observe(Playing());
        p.Controller.Observe(Waiting);
        Assert.Equal("restore:CS2", p.Calls[^1]);
        Assert.Contains("diagnosticar", p.Controller.LastError);
        Assert.Equal(SessionState.Detecting, p.Controller.State);
    }

    [Fact]
    public void Failure_while_restoring_is_recorded_and_the_controller_keeps_working()
    {
        var p = new Probe { RestoreError = new IOException("disco") };
        p.Controller.Observe(Playing());
        p.Controller.Observe(Waiting);
        Assert.Contains("restaurar", p.Controller.LastError);
        p.RestoreError = null;
        p.Controller.Observe(Playing(11));
        Assert.Equal(SessionState.Gaming, p.Controller.State);
    }

    [Fact]
    public void App_closing_mid_game_restores_without_diagnosing_and_then_ignores_everything()
    {
        var p = new Probe();
        p.Controller.Observe(Playing());
        p.Controller.Shutdown();
        Assert.Equal(new[] { "prepare:CS2", "restore:CS2" }, p.Calls);
        p.Controller.Observe(Playing(99));
        p.Controller.Shutdown();
        Assert.Equal(2, p.Calls.Count);
    }

    [Fact]
    public void Concurrent_observations_start_a_single_session()
    {
        var p = new Probe();
        Parallel.For(0, 64, _ => p.Controller.Observe(Playing()));
        Assert.Single(p.Calls);
        Parallel.For(0, 64, _ => p.Controller.Observe(Waiting));
        Assert.Equal(3, p.Calls.Count);
    }

    [Fact]
    public void Invalid_transitions_are_rejected_by_the_table()
    {
        Assert.False(GameSessionController.CanGo(SessionState.Idle, SessionState.Gaming));
        Assert.False(GameSessionController.CanGo(SessionState.Preparing, SessionState.Diagnosing));
        Assert.False(GameSessionController.CanGo(SessionState.Diagnosing, SessionState.Gaming));
        Assert.True(GameSessionController.CanGo(SessionState.Diagnosing, SessionState.Restoring));
        foreach (SessionState s in Enum.GetValues<SessionState>()) Assert.NotEmpty(GameSessionController.Label(s));
    }
}

public class IntelligenceLearningTests
{
    private static SessionSummary S(int day, double fps = 140, double low = 100, double p99 = 11, string signature = "hw|drv1|1440p|ac", double minutes = 30) =>
        new(IntelSim.T0.AddDays(day), signature, minutes) { AvgFps = fps, Low1Fps = low, P99Ms = p99, StuttersPerMinute = 0.2 };

    private static (LearningEngine Engine, List<GameBaseline> Baselines) Established()
    {
        var engine = new LearningEngine();
        var b = new List<GameBaseline>();
        engine.Learn(b, S(0, 140));
        engine.Learn(b, S(1, 142));
        engine.Learn(b, S(2, 138));
        engine.Learn(b, S(3, 141));
        return (engine, b);
    }

    [Fact]
    public void Too_short_session_teaches_nothing()
    {
        var b = new List<GameBaseline>();
        Assert.Equal(LearningOutcomeKind.TooShort, new LearningEngine().Learn(b, S(0, minutes: 2)).Kind);
        Assert.Empty(b);
    }

    [Fact]
    public void Baseline_is_established_gradually()
    {
        var engine = new LearningEngine();
        var b = new List<GameBaseline>();
        Assert.Equal(LearningOutcomeKind.NewContext, engine.Learn(b, S(0)).Kind);
        Assert.False(b[0].Established);
        Assert.Empty(LearningEngine.Compare(b[0], S(1, fps: 10))); // sem referência estabelecida não há "anormal"
        engine.Learn(b, S(1));
        var third = engine.Learn(b, S(2));
        Assert.Equal(LearningOutcomeKind.Learned, third.Kind);
        Assert.True(b[0].Established);
    }

    [Fact]
    public void Normal_variation_is_learned_without_alarm()
    {
        var (engine, b) = Established();
        var o = engine.Learn(b, S(4, 139, 99, 11.1));
        Assert.Equal(LearningOutcomeKind.Learned, o.Kind);
        Assert.Empty(o.Deviations);
        Assert.Equal(5, b[0].Sessions.Count);
    }

    [Fact]
    public void Abnormal_session_does_not_contaminate_the_baseline()
    {
        var (engine, b) = Established();
        var o = engine.Learn(b, S(4, fps: 70, low: 35, p99: 30));
        Assert.Equal(LearningOutcomeKind.OutlierHeld, o.Kind);
        Assert.True(o.HasRegression);
        Assert.Equal(4, b[0].Sessions.Count);
        Assert.Single(b[0].Held);
        Assert.InRange(b[0].Stat(s => s.AvgFps)!.Value.Center, 139, 142);
    }

    [Fact]
    public void Return_to_normal_discards_the_held_exception()
    {
        var (engine, b) = Established();
        engine.Learn(b, S(4, fps: 70, low: 35, p99: 30));
        engine.Learn(b, S(5));
        Assert.Empty(b[0].Held);
        Assert.Equal(5, b[0].Sessions.Count);
    }

    [Fact]
    public void Consistent_change_rebuilds_the_baseline()
    {
        var (engine, b) = Established();
        engine.Learn(b, S(4, fps: 100, low: 70, p99: 16));
        engine.Learn(b, S(5, fps: 101, low: 71, p99: 16));
        var o = engine.Learn(b, S(6, fps: 99, low: 69, p99: 16));
        Assert.Equal(LearningOutcomeKind.RegimeChange, o.Kind);
        Assert.Equal(3, b[0].Sessions.Count);
        Assert.Empty(b[0].Held);
        Assert.InRange(b[0].Stat(s => s.AvgFps)!.Value.Center, 99, 101);
        Assert.NotNull(b[0].RebuiltAt);
        Assert.DoesNotContain(b[0].Sessions, s => s.Outlier);
    }

    [Fact]
    public void Abnormal_sessions_in_opposite_directions_are_not_a_regime_change()
    {
        var (engine, b) = Established();
        engine.Learn(b, S(4, fps: 70, low: 35, p99: 30));
        engine.Learn(b, S(5, fps: 220, low: 180, p99: 6));
        var o = engine.Learn(b, S(6, fps: 70, low: 35, p99: 30));
        Assert.Equal(LearningOutcomeKind.OutlierHeld, o.Kind);
        Assert.Equal(4, b[0].Sessions.Count);
    }

    [Fact]
    public void New_driver_or_hardware_starts_a_separate_baseline()
    {
        var (engine, b) = Established();
        var o = engine.Learn(b, S(4, fps: 90, signature: "hw|drv2|1440p|ac"));
        Assert.Equal(LearningOutcomeKind.NewContext, o.Kind);
        Assert.Equal(2, b.Count);
        Assert.Equal(4, b[0].Sessions.Count);
        engine.Learn(b, S(5, fps: 60, signature: "hw2|drv2|1440p|ac"));
        Assert.Equal(3, b.Count);
    }

    [Fact]
    public void Baseline_keeps_a_bounded_number_of_sessions()
    {
        var engine = new LearningEngine();
        var b = new List<GameBaseline>();
        for (int i = 0; i < 60; i++) engine.Learn(b, S(i, 140 + i % 3));
        Assert.Equal(GameBaseline.MaxSessions, b[0].Sessions.Count);
    }

    [Fact]
    public void Context_signature_separates_incomparable_conditions()
    {
        var a = IntelSim.Context();
        Assert.Empty(a.DifferencesFrom(IntelSim.Context()));
        Assert.Equal(new[] { "driver de vídeo" }, a.DifferencesFrom(IntelSim.Context(driver: "33.0")));
        Assert.Contains("resolução", a.DifferencesFrom(IntelSim.Context(resolution: "1920x1080@60")));
        Assert.NotEqual(a.Signature, IntelSim.Context(hardware: "outro").Signature);
        Assert.Empty(a.DifferencesFrom(a with { GpuDriver = null })); // desconhecido não é "diferente"
    }
}

public class IntelligenceExperimentTests
{
    private static List<SessionSummary> Runs(params (double Low, double P99)[] values) =>
        values.Select((v, i) => new SessionSummary(IntelSim.T0.AddDays(i), "sig", 20) { Low1Fps = v.Low, P99Ms = v.P99, AvgFps = v.Low * 1.4, StuttersPerMinute = 0 }).ToList();

    [Fact]
    public void Equal_results_are_within_noise()
    {
        var a = Runs((100, 12), (101, 12.1), (99, 11.9), (100, 12));
        var r = ExperimentEvaluator.Evaluate(a, Runs((100, 12), (101, 12.1), (99, 11.9), (100, 12)));
        Assert.Equal(ExperimentVerdict.WithinNoise, r.Verdict);
        Assert.False(r.KeepChange);
        Assert.True(r.ShouldRevert);
    }

    [Fact]
    public void Real_improvement_is_recognized()
    {
        var r = ExperimentEvaluator.Evaluate(Runs((100, 12), (101, 12.1), (99, 11.9), (100, 12)), Runs((115, 10.2), (116, 10.1), (114, 10.3), (115, 10.2)));
        Assert.Equal(ExperimentVerdict.Improvement, r.Verdict);
        Assert.True(r.KeepChange);
        Assert.Contains(r.Effects, e => e.Metric == "1% low" && e.Interval!.Value.ExcludesZero);
    }

    [Fact]
    public void Regression_is_recognized_even_when_another_metric_improves()
    {
        var r = ExperimentEvaluator.Evaluate(Runs((100, 12), (101, 12.1), (99, 11.9), (100, 12)), Runs((112, 15), (113, 15.2), (111, 14.9), (112, 15.1)));
        Assert.Equal(ExperimentVerdict.Regression, r.Verdict);
        Assert.True(r.ShouldRevert);
    }

    [Fact]
    public void Small_gain_inside_a_noisy_measurement_is_not_announced()
    {
        var noisy = Runs((100, 12), (112, 11), (91, 13.4), (105, 11.6), (95, 12.6));
        var r = ExperimentEvaluator.Evaluate(noisy, Runs((104, 11.6), (115, 10.8), (93, 13), (108, 11.3), (99, 12.2)));
        Assert.NotEqual(ExperimentVerdict.Improvement, r.Verdict);
        Assert.False(r.KeepChange);
    }

    [Fact]
    public void One_lucky_run_does_not_create_a_fake_gain()
    {
        var r = ExperimentEvaluator.Evaluate(Runs((100, 12), (101, 12.1), (99, 11.9), (100, 12)), Runs((100, 12), (101, 12), (160, 7), (99, 12.1)));
        Assert.NotEqual(ExperimentVerdict.Improvement, r.Verdict);
    }

    [Fact]
    public void Too_few_runs_are_inconclusive()
    {
        var r = ExperimentEvaluator.Evaluate(Runs((100, 12), (101, 12)), Runs((130, 9), (131, 9)));
        Assert.Equal(ExperimentVerdict.Inconclusive, r.Verdict);
        Assert.False(r.ShouldRevert);
        Assert.Contains("3 sessões", r.Summary);
    }

    [Fact]
    public void Different_conditions_make_the_comparison_invalid()
    {
        var b = Runs((100, 12), (101, 12), (99, 12)).Select(s => s with { Signature = "outra" }).ToList();
        var r = ExperimentEvaluator.Evaluate(Runs((100, 12), (101, 12), (99, 12)), b);
        Assert.Equal(ExperimentVerdict.Invalid, r.Verdict);
        Assert.False(r.ShouldRevert);
    }

    [Fact]
    public void Sessions_without_the_metrics_are_inconclusive_not_zero()
    {
        var empty = Enumerable.Range(0, 3).Select(i => new SessionSummary(IntelSim.T0.AddDays(i), "sig", 20)).ToList();
        var r = ExperimentEvaluator.Evaluate(empty, empty);
        Assert.Equal(ExperimentVerdict.Inconclusive, r.Verdict);
        Assert.Empty(r.Effects);
    }

    [Fact]
    public void Zero_baseline_does_not_divide_by_zero()
    {
        var e = ExperimentEvaluator.Effect("Engasgos por minuto", false, new double[] { 0, 0, 0 }, new double[] { 3, 4, 3 });
        Assert.Equal(ExperimentVerdict.Regression, e.Verdict);
        Assert.Equal(ExperimentVerdict.WithinNoise, ExperimentEvaluator.Effect("Engasgos por minuto", false, new double[] { 0, 0, 0 }, new double[] { 0, 0.1, 0 }).Verdict);
        Assert.NotEmpty(e.Line);
    }
}

public class IntelligenceRecommendationTests
{
    private static readonly MachineProfile Plain = new(IntelSim.T0, "hw", Array.Empty<CapabilityGroup>()) { LogicalProcessors = 8, PhysicalCores = 8 };
    private static readonly MachineProfile Hybrid = Plain with { IsHybridCpu = true, LogicalProcessors = 24, PhysicalCores = 16 };

    private static IReadOnlyList<Recommendation> Recommend(IntelRig rig, DiagnosisReport report, MachineProfile? machine = null, GamePreset? preset = null, ISet<string>? dismissed = null) =>
        new RecommendationEngine(rig.Executor).Recommend(report, machine ?? Plain, preset ?? GamePreset.Balanced, IntelRig.Game, dismissed);

    private static DiagnosisReport WithHogs() =>
        IntelSim.Diagnose(IntelSim.Session(15, external: new ExternalEvidence { BackgroundHogs = new[] { new BackgroundHog("chrome", 9) } }));

    private static DiagnosisReport MainThread(double minutes = 15) =>
        IntelSim.Diagnose(IntelSim.Session(minutes, (_, p) => p with { CpuLoad = 40, CpuMaxCoreLoad = 97, GpuLoad = 55 }));

    [Fact]
    public void Healthy_session_recommends_nothing()
    {
        var (frames, mark) = IntelSim.Frames(15, Array.Empty<int>());
        Assert.Empty(Recommend(new IntelRig(), IntelSim.Diagnose(IntelSim.Session(15, mark, frames))));
    }

    [Fact]
    public void Thermal_problem_gets_advice_and_never_a_clock_action()
    {
        var report = IntelSim.Diagnose(IntelSim.Session(20, (i, p) => i < 600 ? p with { CpuTemp = 72, CpuLoad = 80 } : p with { CpuTemp = 96, CpuClockMhz = 4300, CpuLoad = 80 }));
        var recs = Recommend(new IntelRig(), report);
        var r = Assert.Single(recs, x => x.Source == DiagnosisKind.CpuTemperature);
        Assert.Null(r.ActionId);
        Assert.Equal(Destinations.Fans, r.Destination);
        Assert.Contains("Não alteramos clock", r.Proposal);
    }

    [Fact]
    public void Background_contention_offers_the_reversible_actions_that_are_still_off()
    {
        var rig = new IntelRig();
        var recs = Recommend(rig, WithHogs());
        Assert.Contains(recs, r => r.ActionId == ActionIds.ProBalance && r.Risk == RiskClass.B);
        Assert.Contains(recs, r => r.ActionId == ActionIds.EcoQos);

        rig.Targets[ActionIds.ProBalance].State = "on"; // o usuário já usa
        Assert.DoesNotContain(Recommend(rig, WithHogs()), r => r.ActionId == ActionIds.ProBalance);
    }

    [Fact]
    public void Every_recommendation_explains_itself()
    {
        foreach (var r in Recommend(new IntelRig(), WithHogs()))
        {
            Assert.NotEmpty(r.Title);
            Assert.NotEmpty(r.Problem);
            Assert.NotEmpty(r.Proposal);
            Assert.NotEmpty(r.RiskText);
            Assert.NotEmpty(r.HowToUndo);
            Assert.NotEmpty(r.Evidence);
            Assert.True(r.Confidence.Score > 0);
            if (r.Destination != null) Assert.Contains(r.Destination, Destinations.All);
            if (r.ActionId != null) Assert.Contains("Sem promessa", r.Benefit);
        }
    }

    [Fact]
    public void Cpu_sets_are_suggested_only_where_the_topology_justifies()
    {
        Assert.DoesNotContain(Recommend(new IntelRig(), MainThread(), Plain), r => r.ActionId == ActionIds.CpuSets);
        var hybrid = Recommend(new IntelRig(), MainThread(), Hybrid);
        var cpuSets = Assert.Single(hybrid, r => r.ActionId == ActionIds.CpuSets);
        Assert.Equal(RiskClass.C, cpuSets.Risk);
        Assert.Contains("preferência, não uma trava", cpuSets.Proposal);
    }

    [Fact]
    public void Weak_evidence_does_not_unlock_an_action()
    {
        // 3 minutos: o diagnóstico existe, mas com confiança abaixo da mínima da ação.
        var report = MainThread(minutes: 3);
        Assert.True(report.Find(DiagnosisKind.MainThreadBound)!.Confidence.Level < ConfidenceLevel.Medium);
        Assert.DoesNotContain(Recommend(new IntelRig(), report, Hybrid), r => r.ActionId != null);
    }

    [Fact]
    public void Incompatible_action_is_not_offered()
    {
        var rig = new IntelRig();
        rig.Targets[ActionIds.CpuSets].Incompatible = "sem suporte";
        Assert.DoesNotContain(Recommend(rig, MainThread(), Hybrid), r => r.ActionId == ActionIds.CpuSets);
    }

    [Fact]
    public void Presets_shape_what_is_suggested()
    {
        Assert.DoesNotContain(Recommend(new IntelRig(), WithHogs(), preset: GamePreset.Custom), r => r.ActionId != null);
        Assert.False(GamePreset.Quiet.Suggests(ActionIds.PowerPlan));
        Assert.True(GamePreset.Performance.Suggests(ActionIds.PowerPlan));
        Assert.Equal(GamePreset.Balanced, GamePreset.Parse("inexistente"));
        Assert.Equal(5, GamePreset.All.Count);
    }

    [Fact]
    public void Dismissed_recommendation_does_not_come_back()
    {
        var recs = Recommend(new IntelRig(), WithHogs(), dismissed: new HashSet<string> { "action:" + ActionIds.ProBalance });
        Assert.DoesNotContain(recs, r => r.ActionId == ActionIds.ProBalance);
        Assert.Contains(recs, r => r.ActionId == ActionIds.EcoQos);
    }

    [Fact]
    public void Gpu_bound_only_explains_and_points_to_the_windows_screen_on_dual_gpu_machines()
    {
        var report = IntelSim.Diagnose(IntelSim.Session(15, (_, p) => p with { GpuLoad = 98 }));
        Assert.All(Recommend(new IntelRig(), report), r => Assert.Null(r.ActionId));
        var dual = Recommend(new IntelRig(), report, Plain with { HasIntegratedAndDedicatedGpu = true });
        Assert.Contains(dual, r => r.Destination == Destinations.WindowsGraphics);
    }
}

public class IntelligenceStoreAndOrchestratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "thg-intel-" + Guid.NewGuid().ToString("N"));
    private string File1 => Path.Combine(_dir, "intelligence.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private (IntelligenceOrchestrator O, IntelRig Rig, IntelligenceStore Store) Make(AutomationPolicy? policy = null)
    {
        var store = new IntelligenceStore(File1);
        var journal = new List<ActionRecord>();
        IntelRig? rig = null;
        var targets = new Dictionary<string, FakeTarget>();
        rig = new IntelRig();
        // O executor do orquestrador grava o diário no próprio store, como o app faz.
        var executor = new ActionExecutor(rig.Executor.Specs.Select(s => (s, (IActionTarget)rig.Targets[s.Id])),
            rec => store.Mutate(d => { if (!d.Journal.Contains(rec)) d.Journal.Insert(0, rec); }));
        var o = new IntelligenceOrchestrator(store, executor, () => policy ?? AutomationPolicy.Default, new DiagnosisEngine(now: () => IntelSim.T0.AddDays(60)));
        return (o, rig, store);
    }

    private static SessionTelemetry Session(int day, IEnumerable<int>? spikes = null, ExternalEvidence? external = null, string driver = "32.0.1", double fps = 100)
    {
        var (frames, mark) = IntelSim.Frames(15, spikes ?? Array.Empty<int>());
        return IntelSim.Session(15, (i, p) => mark(i, p) with { Fps = fps }, frames, external, IntelSim.Context(driver: driver, start: IntelSim.T0.AddDays(day)));
    }

    private static readonly int[] Many = Enumerable.Range(0, 80).Select(k => 20 + k * 11).ToArray();

    [Fact]
    public void Store_round_trips_and_survives_reopening()
    {
        var (o, _, store) = Make();
        o.Analyze(Session(0), null);
        var again = new IntelligenceStore(File1);
        Assert.Null(again.LoadWarning);
        Assert.Equal(1, again.Read(d => d.History.Count));
        var game = again.Read(d => d.Games["Steam:1"]);
        Assert.Single(game.History);
        Assert.Single(game.Baselines);
        Assert.NotNull(again.Read(d => d.History[0].Report));
        Assert.True(store.SizeBytes > 0);
    }

    [Fact]
    public void Corrupted_file_is_set_aside_and_the_app_goes_on()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File1, "{ isto não é json");
        string? warned = null;
        var store = new IntelligenceStore(File1, w => warned = w);
        Assert.NotNull(store.LoadWarning);
        Assert.NotNull(warned);
        Assert.True(File.Exists(File1 + ".corrompido"));
        Assert.Equal(0, store.Read(d => d.Games.Count));
        store.Mutate(d => d.History.Add(new DiagnosisEntry { GameKey = "x" }));
        Assert.True(File.Exists(File1));
    }

    [Fact]
    public void File_from_a_newer_version_is_never_rewritten()
    {
        Directory.CreateDirectory(_dir);
        string newer = "{\"Version\":99,\"Games\":{},\"History\":[],\"Journal\":[],\"Futuro\":true}";
        File.WriteAllText(File1, newer);
        var store = new IntelligenceStore(File1);
        Assert.NotNull(store.LoadWarning);
        store.Mutate(d => d.History.Add(new DiagnosisEntry { GameKey = "x" }));
        Assert.Equal(newer, File.ReadAllText(File1));
    }

    [Fact]
    public void Limits_hold_and_active_actions_are_never_trimmed()
    {
        var store = new IntelligenceStore(File1);
        store.Mutate(d =>
        {
            for (int i = 0; i < 500; i++) d.History.Add(new DiagnosisEntry { At = IntelSim.T0.AddMinutes(-i), GameKey = "g", Report = "texto" });
            d.Journal.Add(new ActionRecord { EntryId = "ativa", Status = ActionStatus.Applied, Before = "off", Applied = "on", At = IntelSim.T0.AddYears(-3) });
            for (int i = 0; i < 500; i++) d.Journal.Insert(0, new ActionRecord { EntryId = "r" + i, Status = ActionStatus.Reverted, At = IntelSim.T0 });
        });
        Assert.Equal(IntelligenceData.MaxHistory, store.Read(d => d.History.Count));
        Assert.Equal(IntelligenceData.MaxReports, store.Read(d => d.History.Count(h => h.Report != null)));
        Assert.Equal(IntelligenceData.MaxJournal, store.Read(d => d.Journal.Count));
        Assert.Contains(store.Read(d => d.Journal.ToList()), r => r.EntryId == "ativa");

        store.Prune(TimeSpan.FromDays(30), IntelSim.T0.AddDays(60));
        Assert.Equal(0, store.Read(d => d.History.Count));
        Assert.Equal("ativa", Assert.Single(store.Read(d => d.Journal.ToList())).EntryId);

        store.DeleteAll();
        Assert.Single(store.Read(d => d.Journal.ToList()));
    }

    [Fact]
    public void Export_and_import_move_profiles_but_never_someone_elses_action_journal()
    {
        var (o, rig, store) = Make();
        o.Analyze(Session(0), null);
        rig.Apply(ActionIds.ProBalance);
        store.Mutate(d => d.Journal.Add(new ActionRecord { EntryId = "de-outro-pc", Status = ActionStatus.Applied, Before = "x", Applied = "y" }));
        string export = Path.Combine(_dir, "export.json");
        store.Export(export);

        var other = new IntelligenceStore(Path.Combine(_dir, "outro.json"));
        var (ok, message) = other.Import(export);
        Assert.True(ok, message);
        Assert.Single(other.Read(d => d.Games.Keys.ToList()));
        Assert.Equal(0, other.Read(d => d.Journal.Count));
        Assert.Equal(1, other.Read(d => d.History.Count));

        Assert.False(other.Import(Path.Combine(_dir, "nao-existe.json")).Ok);
        File.WriteAllText(export, "[]");
        Assert.False(other.Import(export).Ok);
        Assert.True(other.DeleteGame("Steam:1"));
        Assert.Equal(0, other.Read(d => d.History.Count));
    }

    [Fact]
    public void Analysis_diagnoses_learns_and_writes_an_honest_report()
    {
        var (o, _, store) = Make();
        var a = o.Analyze(Session(0), null);
        Assert.Equal(LearningOutcomeKind.NewContext, a.Learning.Kind);
        Assert.Contains("RELATÓRIO DE SESSÃO", a.ReportText);
        Assert.Contains("VERIFICADO E SEM PROBLEMA", a.ReportText);
        Assert.Contains("Latência de entrada (input lag): não medida", a.ReportText);
        Assert.Contains("0,1% low:", a.ReportText);
        Assert.Contains("Nenhuma: não há evidência", a.ReportText);
        Assert.Equal(a.Report.Headline, store.Read(d => d.History[0].Headline));
        for (int day = 1; day <= 3; day++) a = o.Analyze(Session(day), null);
        Assert.Equal(LearningOutcomeKind.Learned, a.Learning.Kind);
        Assert.True(store.Read(d => d.Games["Steam:1"].Baselines[0].Established));
    }

    [Fact]
    public void Report_marks_missing_frame_metrics_as_unavailable()
    {
        var report = IntelSim.Diagnose(IntelSim.Session(15));
        string text = SessionReport.Text(report, null, Array.Empty<Recommendation>());
        Assert.Contains("estimado do FPS por segundo", text);
        Assert.Contains("indisponíveis (sem gravação de frames)", text);
    }

    [Fact]
    public void Action_that_brings_a_measured_improvement_is_kept()
    {
        var (o, rig, store) = Make();
        var hogs = new ExternalEvidence { BackgroundHogs = new[] { new BackgroundHog("chrome", 9) } };
        for (int day = 0; day < 4; day++) o.Analyze(Session(day, Many, hogs), null);
        var applied = o.Executor.Apply(ActionIds.ProBalance, IntelRig.Game, AutomationPolicy.Default, true, "programas de fundo", signature: IntelSim.Context().Signature);
        applied.Record!.At = IntelSim.T0.AddDays(3.5);
        SessionAnalysis last = null!;
        for (int day = 4; day < 7; day++) last = o.Analyze(Session(day), null); // sem engasgos depois da mudança
        var rec = store.Read(d => d.Journal.Single());
        Assert.Equal("kept", rec.Evaluation);
        Assert.True(rec.IsActive);
        Assert.Equal("on", rig.Targets[ActionIds.ProBalance].State);
        Assert.Contains(last.ActionNotes, n => n.Contains("mantida"));
        Assert.Contains("3 sessões comparáveis", rec.EvaluationText);
    }

    [Fact]
    public void Action_followed_by_a_measured_regression_is_undone_automatically()
    {
        var (o, rig, store) = Make();
        for (int day = 0; day < 4; day++) o.Analyze(Session(day), null);
        var applied = o.Executor.Apply(ActionIds.CpuSets, IntelRig.Game, AutomationPolicy.Default, true, "teste", signature: IntelSim.Context().Signature);
        applied.Record!.At = IntelSim.T0.AddDays(3.5);
        SessionAnalysis last = null!;
        // Depois da mudança as sessões passam a ter engasgos. As duas primeiras ficam retidas como anormais; a terceira confirma a mudança.
        for (int day = 4; day < 7; day++) last = o.Analyze(Session(day, Many), null);
        var rec = store.Read(d => d.Journal.Single());
        Assert.Equal("reverted-regression", rec.Evaluation);
        Assert.Equal(ActionStatus.Reverted, rec.Status);
        Assert.Equal("off", rig.Targets[ActionIds.CpuSets].State);
    }

    [Fact]
    public void Action_without_measurable_benefit_is_not_kept()
    {
        var (o, rig, store) = Make();
        for (int day = 0; day < 4; day++) o.Analyze(Session(day), null);
        var applied = o.Executor.Apply(ActionIds.EcoQos, IntelRig.Game, AutomationPolicy.Default, true, "teste", signature: IntelSim.Context().Signature);
        applied.Record!.At = IntelSim.T0.AddDays(3.5);
        for (int day = 4; day < 7; day++) o.Analyze(Session(day), null);
        var rec = store.Read(d => d.Journal.Single());
        Assert.Equal("reverted-no-benefit", rec.Evaluation);
        Assert.Equal("off", rig.Targets[ActionIds.EcoQos].State);
    }

    [Fact]
    public void Automatic_undo_respects_a_value_changed_from_outside()
    {
        var (o, rig, store) = Make();
        for (int day = 0; day < 4; day++) o.Analyze(Session(day), null);
        var applied = o.Executor.Apply(ActionIds.EcoQos, IntelRig.Game, AutomationPolicy.Default, true, "teste", signature: IntelSim.Context().Signature);
        applied.Record!.At = IntelSim.T0.AddDays(3.5);
        rig.Targets[ActionIds.EcoQos].State = "custom";
        for (int day = 4; day < 7; day++) o.Analyze(Session(day), null);
        Assert.Equal("conflict", store.Read(d => d.Journal.Single()).Evaluation);
        Assert.Equal("custom", rig.Targets[ActionIds.EcoQos].State);
    }

    [Fact]
    public void Safe_auto_applies_pre_approved_class_b_and_leaves_the_rest_as_recommendations()
    {
        var policy = new AutomationPolicy(AutomationLevel.SafeAuto, new HashSet<string> { ActionIds.ProBalance });
        var (o, rig, store) = Make(policy);
        var hogs = new ExternalEvidence { BackgroundHogs = new[] { new BackgroundHog("chrome", 9) } };
        var a = o.Analyze(Session(0, external: hogs), null);
        Assert.Equal("on", rig.Targets[ActionIds.ProBalance].State);
        Assert.Equal("off", rig.Targets[ActionIds.EcoQos].State);
        Assert.Contains(a.ActionNotes, n => n.StartsWith("Automático"));
        Assert.DoesNotContain(a.Recommendations, r => r.ActionId == ActionIds.ProBalance);
        Assert.Contains(a.Recommendations, r => r.ActionId == ActionIds.EcoQos);
        Assert.True(store.Read(d => d.Journal.Single()).Automatic);
    }

    [Fact]
    public void Default_policy_never_applies_anything_by_itself()
    {
        var (o, rig, store) = Make();
        o.Analyze(Session(0, external: new ExternalEvidence { BackgroundHogs = new[] { new BackgroundHog("chrome", 9) } }), null);
        Assert.All(rig.Targets.Values, t => Assert.Equal("off", t.State));
        Assert.Equal(0, store.Read(d => d.Journal.Count));
    }

    [Fact]
    public void Drop_right_after_a_driver_change_is_flagged_as_possible_not_certain()
    {
        var (o, _, _) = Make();
        for (int day = 0; day < 4; day++) o.Analyze(Session(day), null);
        var a = o.Analyze(Session(5, driver: "33.0.0", fps: 100, spikes: Many), null);
        var f = a.Report.Find(DiagnosisKind.Driver);
        Assert.NotNull(f);
        Assert.Equal(ConfidenceLevel.Low, f!.Confidence.Level);
        Assert.Contains("não prova", f.Interpretation);
        Assert.Equal(LearningOutcomeKind.NewContext, a.Learning.Kind);
    }

    [Fact]
    public void Inspect_does_not_learn_or_write()
    {
        var (o, _, store) = Make();
        var (report, recs) = o.Inspect(Session(0), null);
        Assert.NotNull(report);
        Assert.Empty(recs);
        Assert.Equal(0, store.Read(d => d.History.Count));
    }
}

public class IntelligenceSelfImpactTests
{
    [Fact]
    public void Sustained_overhead_during_a_game_reduces_collection_one_step_at_a_time()
    {
        var g = new SelfImpactGovernor(sustain: 3, calm: 4);
        Assert.Equal(SelfImpactDecision.Keep, g.Observe(4, gaming: true));
        Assert.Equal(SelfImpactDecision.Keep, g.Observe(4, gaming: true));
        Assert.Equal(SelfImpactDecision.Reduce, g.Observe(4, gaming: true));
        Assert.Equal(1, g.Level);
        for (int i = 0; i < 2; i++) g.Observe(4, true);
        Assert.Equal(SelfImpactDecision.Reduce, g.Observe(4, true));
        Assert.Equal(SelfImpactGovernor.MaxLevel, g.Level);
        for (int i = 0; i < 10; i++) Assert.Equal(SelfImpactDecision.Keep, g.Observe(4, true)); // já no último degrau
    }

    [Fact]
    public void A_single_spike_decides_nothing()
    {
        var g = new SelfImpactGovernor(sustain: 3);
        g.Observe(9, true);
        g.Observe(0.3, true);
        g.Observe(9, true);
        g.Observe(0.3, true);
        Assert.Equal(0, g.Level);
    }

    [Fact]
    public void Calm_period_or_end_of_game_restores_collection()
    {
        var g = new SelfImpactGovernor(sustain: 2, calm: 3);
        g.Observe(5, true);
        g.Observe(5, true);
        Assert.Equal(1, g.Level);
        g.Observe(0.2, true);
        g.Observe(0.2, true);
        Assert.Equal(SelfImpactDecision.Restore, g.Observe(0.2, true));
        Assert.Equal(0, g.Level);

        g.Observe(5, true);
        g.Observe(5, true);
        Assert.Equal(SelfImpactDecision.Restore, g.Observe(5, gaming: false));
        Assert.Equal(SelfImpactDecision.Keep, g.Observe(5, gaming: false)); // fora do jogo o orçamento é outro e nada é reduzido
    }
}
