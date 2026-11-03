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
