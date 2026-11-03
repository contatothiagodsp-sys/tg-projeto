using Xunit;

namespace ThgOptimization.Tests;

public class Release160TweakTests
{
    [Theory]
    [InlineData("interruptsteering")]
    [InlineData("inputqueues")]
    public void Experimental_tweaks_stay_out_of_turbo(string id)
    {
        var t = WindowsTweaks.Definitions().Single(x => x.Id == id); // inputqueues só aparece em All() para quem já aplicou
        Assert.Equal(0, t.Weight);
        Assert.True(t.NeedsRestart);
        Assert.StartsWith("EXPERIMENTAL", t.Title);
    }

    [Fact]
    public void Tweak_ids_are_unique()
    {
        var ids = WindowsTweaks.Definitions().Select(t => t.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Input_queue_is_not_below_a_safe_size()
    {
        Assert.InRange(WindowsTweaks.InputQueueSize, 30, 100);
    }

    [Fact]
    public void Core_parking_has_lower_weight_after_baseline_review()
    {
        Assert.Equal(1, WindowsTweaks.All().Single(x => x.Id == "coreparking").Weight);
    }

    [Theory]
    [InlineData(22631, true, false)]
    [InlineData(26100, false, false)]
    [InlineData(26100, true, true)]
    public void Shader_delivery_needs_24h2_and_xbox_app(int build, bool xbox, bool ready)
    {
        Assert.Equal(ready, GamerAudit.EvaluateShaderDelivery(build, xbox, "RX 9070 XT").Ready);
    }

    [Theory]
    [InlineData(null, false, CheckLevel.Ok)]
    [InlineData(1, false, CheckLevel.Ok)]
    [InlineData(0, false, CheckLevel.Warn)]
    [InlineData(1, true, CheckLevel.Warn)]
    public void Driver_blocklist_absent_means_on_and_winring0_warns(int? value, bool ring0, CheckLevel expected)
    {
        var services = ring0 ? new[] { "WinRing0x64" } : Array.Empty<string>();
        Assert.Equal(expected, SecurityAudit.EvaluateDriverBlocklist(value, services).Level);
    }
}
