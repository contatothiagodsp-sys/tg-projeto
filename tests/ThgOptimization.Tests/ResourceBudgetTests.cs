using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using LibreHardwareMonitor.Hardware;
using ThgOptimization.Infrastructure;
using ThgOptimization.UI.ViewModels;

namespace ThgOptimization.Tests;

/// <summary>
/// Orçamento do que o app gasta. Tempo depende da máquina: o CI não mede relógio de janela, e o que mede tempo aqui fica com o melhor de várias
/// rodadas, para outro teste em paralelo não reprovar. "App parado sem timer órfão" e "CPU em repouso" pedem o app real: ver docs/auditoria-consumo.md.
/// </summary>
[Collection(WpfCollection.Name)]
public class ResourceBudgetTests
{
    private static SensorMonitor Monitor(int rows)
    {
        var m = new SensorMonitor();
        for (int i = 0; i < rows; i++)
        {
            var row = new SensorRow { Id = $"s/{i}", Hardware = $"H{i % 6}", HardwareType = HardwareType.Cpu, Type = SensorType.Temperature, Category = "Temperatura", Name = $"Sensor {i}" };
            row.Update(40, 30, 50);
            m.Rows.Add(row);
        }
        return m;
    }

    [ManualHardwareFact] // manual: o relógio da janela depende do PC e da carga; no CI seria um teste que falha por acaso
    public void Primeira_pintura_da_janela_abaixo_de_2s()
    {
        XamlSmokeTests.OnSta(() =>
        {
            XamlSmokeTests.EnsureTheme();
            string xaml = XamlSmokeTests.Loose(File.ReadAllText(Path.Combine(RepoText.Root, "MainWindow.xaml")));
            var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/tHgOptimization;component/") };
            var sw = Stopwatch.StartNew();
            var win = (Window)XamlReader.Parse(xaml, context);
            win.Measure(new Size(1320, 840));
            win.Arrange(new Rect(0, 0, 1320, 840));
            win.UpdateLayout();
            Assert.True(sw.ElapsedMilliseconds < 2000, $"Montar a janela levou {sw.ElapsedMilliseconds} ms.");
        });
    }

    [Fact]
    public void Snapshot_do_AppSettings_serializa_em_menos_de_50ms()
    {
        var s = new AppSettings();
        for (int i = 0; i < 500; i++)
        {
            s.BenchRecords.Add(new BenchRecord(DateTime.Now, "gpu", i, "resumo"));
            s.GameSessions.Add(new GameSessionRecord(DateTime.Now, "jogo", 1, i, 1, false));
            s.PinnedSensors.Add($"/amdcpu/0/temperature/{i}");
            s.OpenNavGroups.Add($"grupo{i}");
            s.RemovedApps.Add($"app{i}");
            s.TweakBackup[$"k{i}"] = "v";
        }
        _ = s.SerializeSnapshot(); // a primeira chamada monta o cache de tipos do serializador
        long best = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            _ = s.SerializeSnapshot();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        Assert.True(best < 50, $"Melhor de 5 rodadas: {best} ms.");
    }

    [Fact]
    public void SensorRefresh_nao_aloca_mais_de_2kb_em_regime()
    {
        var monitor = Monitor(500);
        var vm = new SensorPageViewModel();
        vm.Rebuild(monitor, new AppSettings());
        for (int i = 0; i < 10; i++) vm.Refresh(monitor); // aquece o JIT

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) vm.Refresh(monitor);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(bytes < 2048 * 100, $"{bytes / 100} bytes por leitura.");
    }

    [Fact]
    public void Medicao_de_cpu_e_sobre_todos_os_nucleos()
    {
        Assert.Equal(25, ResourceProbe.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 4));
        Assert.Equal(100, ResourceProbe.CpuPercent(TimeSpan.FromSeconds(99), TimeSpan.FromSeconds(1), 4));
        Assert.Equal(0, ResourceProbe.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
        Assert.Equal(0, ResourceProbe.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0));
    }

    [Fact]
    public async Task Medicao_devolve_memoria_e_duracao_reais()
    {
        var result = await ResourceProbe.MeasureAsync(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(10));
        Assert.True(result.PeakWorkingSetMb > 0);
        Assert.InRange(result.AverageWorkingSetMb, 1, result.PeakWorkingSetMb);
        Assert.True(result.Duration >= TimeSpan.FromMilliseconds(60));
        Assert.Contains("pico", result.Summary);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Painel_nao_desenha_com_jogo_em_primeiro_plano(bool game, bool windowActive, bool skip)
    {
        Assert.Equal(skip, UiThrottle.SkipDashDraw(game, windowActive));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void Dica_da_bandeja_nao_repete_o_que_a_pagina_mostra(bool sensors, bool dash, bool skip)
    {
        Assert.Equal(skip, UiThrottle.SkipTrayTip(sensors, dash));
    }

    /// <summary>Leitura leve também toca a CPU: se o resumo só viesse da completa, overlay, RGB e a trava do overclock leriam temperatura velha durante o jogo.</summary>
    [Fact]
    public void Leitura_leve_continua_atualizando_os_valores_principais()
    {
        string source = File.ReadAllText(Path.Combine(RepoText.Root, "SensorMonitor.cs"));
        Assert.Contains("() => Task.Run(() => Poll(h => !light || IsLight(h), includesCpu: true))", source);
        Assert.Contains("Poll(h => h.HardwareType == HardwareType.Motherboard, includesCpu: false)", source);
    }
}
