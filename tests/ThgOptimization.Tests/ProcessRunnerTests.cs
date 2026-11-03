using System.Diagnostics;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task Output_comes_back()
    {
        string output = await ProcessRunner.RunAsync("cmd.exe", "/c echo tudo certo", TimeSpan.FromSeconds(10));
        Assert.Contains("tudo certo", output);
    }

    [Fact]
    public async Task Stuck_program_times_out_and_is_killed()
    {
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync("ping.exe", "-n 60 127.0.0.1", TimeSpan.FromMilliseconds(300)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), $"Levou {sw.Elapsed}.");
    }

    [Fact]
    public void Default_deadline_is_five_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ProcessRunner.DefaultTimeout);
    }

    [Fact]
    public void Sync_call_inside_a_single_threaded_context_does_not_deadlock()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                Assert.Contains("ok", ProcessRunner.Run("cmd.exe", "/c echo ok", TimeSpan.FromSeconds(10)));
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(20)), "Travou na thread da janela.");
        Assert.Null(error);
    }
}
