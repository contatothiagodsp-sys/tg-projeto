using System.Diagnostics;
using System.Security.Principal;

namespace ThgOptimization.Tests;

public class PresentationClassifierTests
{
    [Fact]
    public void Too_few_frames_is_unknown_and_never_alerts()
    {
        var r = PresentationClassifier.Classify(new PresentationEvidence(Presents: 5, InFrameComposed: 5));
        Assert.Equal(PresentationMode.Unknown, r.Mode);
        Assert.Equal(DetectionConfidence.Unknown, r.Confidence);
        Assert.False(r.Alert);
    }

    [Fact]
    public void Present_events_alone_do_not_decide_the_mode()
    {
        var r = PresentationClassifier.Classify(new PresentationEvidence(Presents: 5000));
        Assert.Equal(PresentationMode.Unknown, r.Mode);
    }

    [Fact]
    public void Independent_flip_with_clear_majority()
    {
        var r = PresentationClassifier.Classify(new PresentationEvidence(Presents: 500, FlipTokens: 500, InFrameIndependent: 490, InFrameComposed: 10));
        Assert.Equal(PresentationMode.HardwareIndependentFlip, r.Mode);
        Assert.Equal(DetectionConfidence.High, r.Confidence);
        Assert.Equal(0.02, r.DwmShare, 3);
        Assert.False(r.Alert);
    }

    [Fact]
    public void Several_active_planes_lower_the_confidence_because_they_are_system_wide()
    {
        var r = PresentationClassifier.Classify(new PresentationEvidence(Presents: 500, FlipTokens: 500, InFrameIndependent: 500, MultiPlaneVSyncs: 80, VSyncs: 100));
        Assert.Equal(PresentationMode.HardwareComposedIndependentFlip, r.Mode);
        Assert.Equal(DetectionConfidence.Low, r.Confidence);
        Assert.False(r.Alert);
    }

    [Fact]
    public void Composed_flip_does_not_alert()
    {
        var r = PresentationClassifier.Classify(new PresentationEvidence(Presents: 500, FlipTokens: 500, InFrameComposed: 500));
        Assert.Equal(PresentationMode.ComposedFlip, r.Mode);
        Assert.False(r.Alert);
        Assert.Contains("não significa", r.Limitations);
    }

    [Fact]
    public void Direct_blit_with_present_history_is_composed_copy_and_alerts()
    {
        var r = PresentationClassifier.Classify(new PresentationEvidence(Presents: 500, BlitsDirect: 500, PresentHistory: 500));
        Assert.Equal(PresentationMode.ComposedCopyGpuGdi, r.Mode);
        Assert.True(r.Alert);
    }

    [Fact]
    public void Redirected_blit_is_cpu_copy()
    {
        Assert.Equal(PresentationMode.ComposedCopyCpuGdi, PresentationClassifier.Classify(new PresentationEvidence(Presents: 500, BlitsRedirected: 500)).Mode);
    }

    [Fact]
    public void Direct_blit_without_history_is_legacy_copy()
    {
        Assert.Equal(PresentationMode.HardwareLegacyCopy, PresentationClassifier.Classify(new PresentationEvidence(Presents: 500, BlitsDirect: 500)).Mode);
    }

    [Fact]
    public void Flip_without_win32k_token_is_legacy_flip()
    {
        var r = PresentationClassifier.Classify(new PresentationEvidence(Presents: 500, LegacyFlips: 500));
        Assert.Equal(PresentationMode.HardwareLegacyFlip, r.Mode);
        Assert.True(r.Alert);
    }

    [Fact]
    public void Mixed_sample_is_unknown_instead_of_a_guess()
    {
        var r = PresentationClassifier.Classify(new PresentationEvidence(Presents: 500, FlipTokens: 500, InFrameIndependent: 250, InFrameComposed: 250));
        Assert.Equal(PresentationMode.Unknown, r.Mode);
        Assert.False(r.Alert);
    }

    [Fact]
    public void Medium_share_gives_medium_confidence()
    {
        var r = PresentationClassifier.Classify(new PresentationEvidence(Presents: 500, FlipTokens: 500, InFrameIndependent: 400, InFrameComposed: 100));
        Assert.Equal(DetectionConfidence.Medium, r.Confidence);
    }

    [Fact]
    public void Unavailable_report_states_it_and_never_alerts()
    {
        var r = PresentationReport.Unavailable("sem permissão");
        Assert.Equal(PresentationCollectionState.Unavailable, r.State);
        Assert.False(r.Alert);
        Assert.Contains("indisponível", r.Text);
    }

    /// <summary>Sessão real e curta, só de leitura. Sem administrador, o que se confere é a falha sem exceção.</summary>
    [Fact]
    public async Task Real_sample_never_throws_and_answers_without_a_game()
    {
        var monitor = new EtwPresentationModeMonitor();
        var report = await monitor.SampleAsync(Environment.ProcessId, TimeSpan.FromMilliseconds(300));
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            Assert.Equal(PresentationCollectionState.Completed, report.State);
        Assert.Equal(PresentationMode.Unknown, report.Mode);
    }

    [Fact]
    public async Task Cancelled_sample_returns_unavailable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var report = await new EtwPresentationModeMonitor().SampleAsync(Environment.ProcessId, TimeSpan.FromSeconds(30), cts.Token);
        Assert.Equal(PresentationCollectionState.Unavailable, report.State);
    }
}
