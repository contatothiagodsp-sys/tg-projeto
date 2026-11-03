using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

public class UiGuardTests
{
    private sealed class Resource : IDisposable
    {
        public int Disposed;
        public void Dispose() => Disposed++;
    }

    [Fact]
    public async Task Failing_body_is_reported_and_does_not_escape()
    {
        Exception? seen = null;
        await UiGuard.RunAsync("teste", async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("coleta falhou");
        }, ex => seen = ex);
        Assert.Equal("coleta falhou", seen?.Message);
    }

    [Fact]
    public async Task Successful_body_reports_nothing()
    {
        bool reported = false;
        await UiGuard.RunAsync("teste", () => Task.CompletedTask, _ => reported = true);
        Assert.False(reported);
    }

    [Fact]
    public async Task Resource_opened_while_the_app_is_closing_is_released()
    {
        var resource = new Resource();
        var opened = await UiGuard.OpenUnlessExitingAsync("teste", () => resource, () => true);
        Assert.Null(opened);
        Assert.Equal(1, resource.Disposed);
    }

    [Fact]
    public async Task Resource_opened_normally_stays_open()
    {
        var resource = new Resource();
        Assert.Same(resource, await UiGuard.OpenUnlessExitingAsync("teste", () => resource, () => false));
        Assert.Equal(0, resource.Disposed);
    }

    [Fact]
    public async Task Failure_while_opening_returns_null()
    {
        Assert.Null(await UiGuard.OpenUnlessExitingAsync<Resource>("teste", () => throw new System.IO.IOException("sem HID"), () => false));
    }
}

