using ThgOptimization.Core.Benchmark;

namespace ThgOptimization.Tests;

public class BenchLabFlowTests
{
    [Theory]
    [InlineData(0, 0, "A")]
    [InlineData(1, 0, "B")]
    [InlineData(1, 1, "A")]
    [InlineData(2, 1, "B")]
    [InlineData(1, 3, "A")]
    public void Repetition_goes_to_the_side_with_fewer_captures(int a, int b, string expected)
    {
        Assert.Equal(expected, BenchLabFlow.NextSlot(a, b));
    }

    [Fact]
    public void Another_program_in_the_foreground_does_not_enter_the_test()
    {
        Assert.Null(BenchLabFlow.GameConflict(null, "cs2"));
        Assert.Null(BenchLabFlow.GameConflict("cs2", "CS2"));
        Assert.Contains("Discord", BenchLabFlow.GameConflict("cs2", "Discord"));
    }

    private static readonly DateTime Applied = new(2026, 11, 3, 20, 0, 0);
    private static readonly DateTime BootBefore = Applied.AddHours(-3), BootAfter = Applied.AddMinutes(5);

    public static TheoryData<bool, bool, LabTweakRun?, int, DateTime, LabTweakStep> Steps => new()
    {
        { false, false, null, 0, BootBefore, LabTweakStep.NeedsBefore },
        { true, false, null, 0, BootBefore, LabTweakStep.AlreadyApplied },
        { false, false, new LabTweakRun("hags", "e"), 0, BootBefore, LabTweakStep.NeedsBefore },
        { false, false, new LabTweakRun("hags", "e"), 2, BootBefore, LabTweakStep.ReadyToApply },
        { true, false, new LabTweakRun("hags", "e"), 2, BootBefore, LabTweakStep.AlreadyApplied }, // ligado por fora, depois do "antes"
        { true, false, new LabTweakRun("hags", "e") { AppliedAt = Applied }, 2, BootBefore, LabTweakStep.ReadyToMeasure },
        { true, true, new LabTweakRun("hags", "e") { AppliedAt = Applied }, 2, BootBefore, LabTweakStep.WaitingRestart },
        { true, true, new LabTweakRun("hags", "e") { AppliedAt = Applied }, 2, BootAfter, LabTweakStep.ReadyToMeasure },
        { false, true, new LabTweakRun("hags", "e") { AppliedAt = Applied }, 2, BootAfter, LabTweakStep.ReadyToApply }, // desligado por fora
        { false, false, new LabTweakRun("outro", "e") { AppliedAt = Applied }, 0, BootBefore, LabTweakStep.OtherTestActive },
        { false, false, new LabTweakRun("outro", "e"), 2, BootBefore, LabTweakStep.NeedsBefore }, // o outro nem aplicou: começar este o substitui
    };

    [Theory]
    [MemberData(nameof(Steps))]
    public void Tweak_test_only_measures_when_the_result_would_be_real(bool on, bool needsRestart, LabTweakRun? run, int before, DateTime boot, LabTweakStep expected)
    {
        Assert.Equal(expected, BenchLabFlow.StepOf("hags", on, needsRestart, run, before, boot));
    }

    private static Tweak FakeTweak(Func<bool> read, Action<bool> write, Action restore) => new()
    {
        Read = () => read(),
        Write = (on, _) => write(on),
        Restore = _ => restore(),
    };

    [Fact]
    public void Revert_uses_the_saved_original()
    {
        bool on = true;
        int writes = 0;
        var tweak = FakeTweak(() => on, _ => writes++, () => on = false);
        Assert.True(BenchLabFlow.RevertTweak(tweak, new AppSettings()));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Revert_turns_off_when_the_saved_original_was_on()
    {
        // O backup é da primeira alteração: quem ligou, desligou e testou tem "ligado" guardado, mas o estado de antes do teste era desligado.
        bool on = true;
        var tweak = FakeTweak(() => on, v => on = v, () => on = true);
        Assert.True(BenchLabFlow.RevertTweak(tweak, new AppSettings()));
        Assert.False(on);
    }

    [Fact]
    public void Revert_reports_when_windows_refuses()
    {
        var tweak = FakeTweak(() => true, _ => { }, () => { });
        Assert.False(BenchLabFlow.RevertTweak(tweak, new AppSettings()));
    }

    [Fact]
    public void Tweak_labels_fit_the_label_limit()
    {
        var (a, b) = BenchLabFlow.TweakLabels("Desativar gravação em segundo plano (Game DVR)");
        Assert.StartsWith("Sem: Desativar", a);
        Assert.StartsWith("Com: Desativar", b);
        Assert.True(a.Length <= BenchLabFlow.MaxLabelLength);
        Assert.Equal(("Sem: HAGS", "Com: HAGS"), BenchLabFlow.TweakLabels("HAGS"));
    }

    private static BenchmarkSession Session(string experiment = "e", string slot = "A") =>
        new("cs2", slot, new DateTime(2026, 11, 3, 20, 0, 0), 140, 90, 70, 11, 0.3, 7, 8000, 60, "x.csv") { ExperimentId = experiment, Slot = slot, P999Ms = 18 };

    [Fact]
    public void Sessions_are_capped_keeping_the_newest()
    {
        var list = new List<BenchmarkSession>();
        for (int i = 0; i < 205; i++) BenchLabFlow.RememberSession(list, Session(i.ToString()));
        Assert.Equal(BenchLabFlow.MaxSessions, list.Count);
        Assert.Equal("204", list[0].ExperimentId);
    }

    [Fact]
    public void Comparing_again_updates_the_line_of_that_test()
    {
        var list = new List<AbComparison>();
        var first = FrameTimeStatistics.Compare(Session(), Session(slot: "B"), aIsBaseline: true);
        BenchLabFlow.RememberComparison(list, first);
        BenchLabFlow.RememberComparison(list, first with { Verdict = "novo" });
        Assert.Equal("novo", Assert.Single(list).Verdict);
    }

    [Fact]
    public void Comparisons_are_capped_at_fifty()
    {
        var list = new List<AbComparison>();
        for (int i = 0; i < 55; i++)
            BenchLabFlow.RememberComparison(list, FrameTimeStatistics.Compare(Session(i.ToString()), Session(i.ToString(), "B"), aIsBaseline: true));
        Assert.Equal(BenchLabFlow.MaxComparisons, list.Count);
        Assert.Equal("54", list[0].ExperimentId);
    }

    [Fact]
    public void History_line_follows_the_format()
    {
        var c = FrameTimeStatistics.Compare(Session(), Session(slot: "B"), aIsBaseline: true) with { ComparedAt = new DateTime(2026, 11, 3, 21, 5, 0) };
        Assert.Equal("A vs B · cs2 · " + c.Verdict + " · 03/11/2026 21:05", BenchLabFlow.HistoryLine(c));
    }

    [Fact]
    public void History_survives_saving_and_opening_the_settings_file()
    {
        var a = new[] { Session(), Session() with { AvgFps = 141 }, Session() with { AvgFps = 139 } };
        var b = new[] { Session(slot: "B") with { AvgFps = 160 }, Session(slot: "B") with { AvgFps = 161 }, Session(slot: "B") with { AvgFps = 159 } };
        var s = new AppSettings { BenchLabExperimentId = "e", BenchLabTweakRun = new LabTweakRun("hags", "e") { AppliedAt = new DateTime(2026, 11, 3, 20, 0, 0) } };
        s.BenchLabSessions.AddRange(a);
        s.AbComparisons.Add(FrameTimeStatistics.Compare(a, b, aIsBaseline: true) with { ReportPath = "r.html" });

        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(s.SerializeSnapshot())!;

        Assert.Equal(3, back.BenchLabSessions.Count);
        Assert.Equal(("e", "A", 18.0), (back.BenchLabSessions[0].ExperimentId, back.BenchLabSessions[0].Slot, back.BenchLabSessions[0].P999Ms));
        var c = Assert.Single(back.AbComparisons);
        Assert.Equal(s.AbComparisons[0].Verdict, c.Verdict);
        Assert.Equal((AbVerdictKind.Improvement, AbTestMethod.StudentT, 3, "r.html"), (c.Kind, c.Method, c.CapturesB, c.ReportPath));
        Assert.NotNull(c.PValue);
        Assert.Equal(s.BenchLabTweakRun, back.BenchLabTweakRun);
    }

    [Fact]
    public void Inconclusive_result_can_be_saved_too()
    {
        var s = new AppSettings();
        s.AbComparisons.Add(FrameTimeStatistics.Compare(Session(), Session(slot: "B"), aIsBaseline: true));
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(s.SerializeSnapshot())!;
        Assert.Null(Assert.Single(back.AbComparisons).PValue);
    }

    [Theory]
    [InlineData(null, 0, 0, "A")]
    [InlineData(null, 1, 0, "B")]
    [InlineData(LabTweakStep.NeedsBefore, 0, 0, "A")]
    [InlineData(LabTweakStep.ReadyToApply, 3, 0, "A")]
    [InlineData(LabTweakStep.ReadyToMeasure, 3, 0, "B")]
    [InlineData(LabTweakStep.WaitingRestart, 3, 0, null)]
    [InlineData(LabTweakStep.AlreadyApplied, 0, 0, null)]
    [InlineData(LabTweakStep.OtherTestActive, 0, 0, null)]
    public void Hotkey_records_the_side_the_test_is_ready_for(LabTweakStep? step, int a, int b, string? expected)
    {
        Assert.Equal(expected, BenchLabFlow.HotkeySlot(step, a, b));
    }

    [Fact]
    public void Session_comes_from_the_capture_with_stutter_as_a_percentage()
    {
        var stats = new FrameStatistics(1000, 60, 100, 100, 70, 50, 70, 50, 10, 9.5, 12, 14, 16, 20, 40, 5, new int[7]);
        var capture = new CaptureResult("cs2", new DateTime(2026, 11, 3), 60, 1000, 100, 70, 50, 16, 40, 5, "x.csv", stats);
        var s = BenchmarkSession.From(capture, "XMP", "exp", "B");
        Assert.Equal(0.5, s.StutterPct);
        Assert.Equal(9.5, s.MedianFrameTimeMs);
        Assert.Equal(20, s.P999Ms);
        Assert.Equal(("XMP", "exp", "B", "x.csv"), (s.ConfigLabel, s.ExperimentId, s.Slot, s.CsvPath));
    }
}
