using System.IO;
using ThgOptimization.Infrastructure;
using ThgOptimization.UI.ViewModels;

namespace ThgOptimization.Tests;

public class SkeletonTests
{
    [Fact]
    public void Skeleton_aparece_antes_de_OpenAsync()
    {
        var vm = new SensorPageViewModel();
        Assert.True(vm.ShowSkeleton);

        vm.SetMonitorState(open: true);
        Assert.False(vm.ShowSkeleton); // aberto e vazio: quem fala é o aviso de "nenhum sensor"
    }

    [Fact]
    public void Skeleton_some_quando_a_deteccao_falha()
    {
        var vm = new SensorPageViewModel();
        vm.SetMonitorState(open: false, failure: "Sensores indisponíveis");
        Assert.False(vm.ShowSkeleton);
    }

    [Fact]
    public void Startup_tem_logs_de_inicio_e_fim()
    {
        string app = File.ReadAllText(Path.Combine(RepoText.Root, "App.xaml.cs"));
        string window = File.ReadAllText(Path.Combine(RepoText.Root, "MainWindow.xaml.cs"));
        Assert.Contains("StartupLog.Mark(\"App.OnStartup\")", app);
        Assert.Contains("StartupLog.Mark(\"MainWindow.Loaded\")", window);
        Assert.Contains("StartupLog.Mark(\"LoadHardwareAsync início\")", window);
        Assert.Contains("StartupLog.Mark(\"LoadHardwareAsync fim\")", window);
    }
}

[Collection(TimerRegistryCollection.Name)]
public class StartupLogTests
{
    [Fact]
    public void Marcas_ficam_na_ordem_e_em_ms_desde_o_inicio_do_processo()
    {
        StartupLog.Reset();
        StartupLog.Mark("a");
        StartupLog.Mark("b");
        var entries = StartupLog.Entries;
        Assert.Equal(new[] { "a", "b" }, entries.Select(e => e.Stage));
        Assert.True(entries[0].Ms > 0);
        Assert.True(entries[1].Ms >= entries[0].Ms);
        Assert.Contains("a: ", StartupLog.Summary);
        StartupLog.Reset();
    }
}
