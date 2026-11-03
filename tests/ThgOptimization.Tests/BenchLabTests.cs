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
