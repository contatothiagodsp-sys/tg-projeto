using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibreHardwareMonitor.Hardware;
using ThgOptimization.UI.ViewModels;

namespace ThgOptimization.Tests;

public class SensorPageTests
{
    private static SensorRow Row(string id, string hardware, HardwareType kind, SensorType type, string name, float? value, bool unavailable = false)
    {
        var row = new SensorRow { Id = id, Hardware = hardware, HardwareType = kind, Type = type, Category = SensorRow.CategoryName(type), Name = name };
        row.Update(value, value, value, unavailable);
        return row;
    }

    private static SensorMonitor Monitor(params SensorRow[] rows)
    {
        var m = new SensorMonitor();
        foreach (var r in rows) m.Rows.Add(r);
        return m;
    }

    private static SensorMonitor Sample() => Monitor(
        Row("cpu/temp", "Ryzen", HardwareType.Cpu, SensorType.Temperature, "Core (Tctl/Tdie)", 55),
        Row("cpu/load", "Ryzen", HardwareType.Cpu, SensorType.Load, "CPU Total", 12),
        Row("gpu/temp", "Radeon", HardwareType.GpuAmd, SensorType.Temperature, "GPU Core", 61),
        Row("mem/used", "Memória Total", HardwareType.Memory, SensorType.Data, "Memória Usada", 20),
        Row("cpu/clock", "Ryzen", HardwareType.Cpu, SensorType.Clock, "Core #1", null, unavailable: true));

    private static IEnumerable<string> VisibleIds(SensorPageViewModel vm) => vm.Groups.SelectMany(g => g.Sensors).Select(s => s.Id);

    [Fact]
    public void Rebuild_cria_um_grupo_por_hardware_com_contador()
    {
        var vm = new SensorPageViewModel();
        vm.Rebuild(Sample(), new AppSettings());
        Assert.Equal(new[] { "Ryzen", "Radeon", "Memória Total" }, vm.Groups.Select(g => g.Hardware));
        Assert.Equal("3 sensores", vm.Groups[0].CountLabel);
        Assert.Equal("1 sensor", vm.Groups[1].CountLabel);
        Assert.Equal(5, vm.TotalCount);
    }

    [Fact]
    public void Rebuild_abre_so_cpu_e_gpu_e_mantem_o_que_o_usuario_deixou()
    {
        var vm = new SensorPageViewModel();
        var settings = new AppSettings();
        var monitor = Sample();
        vm.Rebuild(monitor, settings);
        Assert.Equal(new[] { true, true, false }, vm.Groups.Select(g => g.IsExpanded));
        vm.Groups[0].IsExpanded = false;
        monitor.Rows.Add(Row("cpu/extra", "Ryzen", HardwareType.Cpu, SensorType.Power, "Package", 40));
        vm.Rebuild(monitor, settings);
        Assert.False(vm.Groups[0].IsExpanded);
    }

    [Fact]
    public void Refresh_atualiza_valores_sem_recriar_grupos()
    {
        var vm = new SensorPageViewModel();
        var monitor = Sample();
        vm.Rebuild(monitor, new AppSettings());
        var group = vm.Groups[0];
        var item = group.Sensors.First(s => s.Id == "cpu/temp");

        monitor.Rows[0].Update(70, 55, 70);
        vm.Refresh(monitor);

        Assert.Same(group, vm.Groups[0]);
        Assert.Same(item, vm.Groups[0].Sensors.First(s => s.Id == "cpu/temp"));
        Assert.Contains("70", item.Value);
        Assert.Equal(SensorTone.Warn, item.Tone);
        Assert.Equal(62.5, item.FillPercent);
    }

    [Fact]
    public void Refresh_so_avisa_a_tela_do_que_mudou()
    {
        var vm = new SensorPageViewModel();
        var monitor = Sample();
        vm.Rebuild(monitor, new AppSettings());
        var item = vm.Groups[0].Sensors.First(s => s.Id == "cpu/temp");
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Refresh(monitor);
        Assert.Empty(changed);

        monitor.Rows[0].Update(56, 55, 55);
        vm.Refresh(monitor);
        Assert.Contains(nameof(item.Value), changed);
        Assert.DoesNotContain(nameof(item.Min), changed);
    }

    [Fact]
    public void Refresh_com_outra_linha_na_mesma_contagem_refaz_a_lista()
    {
        var vm = new SensorPageViewModel();
        var settings = new AppSettings();
        var monitor = Monitor(Row("a", "Ryzen", HardwareType.Cpu, SensorType.Load, "A", 1));
        vm.Rebuild(monitor, settings);
        monitor.Rows[0] = Row("b", "Ryzen", HardwareType.Cpu, SensorType.Load, "B", 2);
        vm.Refresh(monitor);
        Assert.Equal(new[] { "b" }, VisibleIds(vm));
    }

    [Fact]
    public void TogglePin_persiste_e_move_para_o_topo()
    {
        var vm = new SensorPageViewModel();
        var settings = new AppSettings();
        vm.Rebuild(Sample(), settings);
        Assert.Empty(vm.Pinned);

        vm.TogglePin("cpu/temp", settings);
        Assert.Contains("cpu/temp", settings.PinnedSensors);
        Assert.Equal("cpu/temp", Assert.Single(vm.Pinned).Id);

        vm.TogglePin("cpu/temp", settings);
        Assert.Empty(settings.PinnedSensors);
        Assert.Empty(vm.Pinned);
    }

    [Fact]
    public void Favoritos_salvos_voltam_na_abertura_e_id_que_sumiu_nao_quebra()
    {
        var vm = new SensorPageViewModel();
        var settings = new AppSettings();
        settings.PinnedSensors.AddRange(new[] { "gpu/temp", "cpu/temp", "disco/que-saiu" });
        vm.Rebuild(Sample(), settings);
        Assert.Equal(new[] { "cpu/temp", "gpu/temp" }, vm.Pinned.Select(p => p.Id));
        Assert.Contains("disco/que-saiu", settings.PinnedSensors); // o disco pode voltar: o favorito continua guardado
    }

    [Fact]
    public void Filtro_por_categoria_esconde_os_demais()
    {
        var vm = new SensorPageViewModel();
        vm.Rebuild(Sample(), new AppSettings());
        vm.CategoryFilter = "Temperatura";
        Assert.Equal(new[] { "cpu/temp", "gpu/temp" }, VisibleIds(vm));
        Assert.Equal(Visibility.Collapsed, vm.Groups.Single(g => g.Hardware == "Memória Total").GroupVisibility);
        Assert.Equal("1 de 3", vm.Groups[0].CountLabel);
        Assert.Contains("2 de 5", vm.StatusLine);
    }

    [Fact]
    public void Filtro_por_hardware_junta_por_tipo_de_peca()
    {
        var vm = new SensorPageViewModel();
        vm.Rebuild(Sample(), new AppSettings());
        Assert.Equal(new[] { "CPU", "GPU", "Memória" }, vm.HardwareFilters);
        vm.HardwareFilter = "GPU";
        Assert.Equal(new[] { "gpu/temp" }, VisibleIds(vm));
    }

    [Theory]
    [InlineData("memoria")]
    [InlineData("MEMÓRIA")]
    public void Busca_ignora_acentos_e_caixa(string search)
    {
        var vm = new SensorPageViewModel();
        vm.Rebuild(Sample(), new AppSettings());
        vm.Search = search;
        Assert.Equal(new[] { "mem/used" }, VisibleIds(vm));
    }

    [Fact]
    public void Busca_abre_os_grupos_e_apagar_devolve_como_estava()
    {
        var vm = new SensorPageViewModel();
        vm.Rebuild(Sample(), new AppSettings());
        var memory = vm.Groups.Single(g => g.Hardware == "Memória Total");
        Assert.False(memory.IsExpanded);
        vm.Search = "mem";
        Assert.True(memory.IsExpanded);
        vm.Search = "";
        Assert.False(memory.IsExpanded);
    }

    [Fact]
    public void Sensores_indisponiveis_apenas()
    {
        var vm = new SensorPageViewModel();
        var monitor = Sample();
        vm.Rebuild(monitor, new AppSettings());
        vm.OnlyUnavailable = true;
        Assert.Equal(new[] { "cpu/clock" }, VisibleIds(vm));

        // Quando volta a ler, sai da lista no tique seguinte.
        monitor.Rows[4].Update(4000, 4000, 4000);
        vm.Refresh(monitor);
        Assert.Empty(VisibleIds(vm));
    }

    [Fact]
    public void Somente_favoritos()
    {
        var vm = new SensorPageViewModel();
        var settings = new AppSettings();
        vm.Rebuild(Sample(), settings);
        vm.TogglePin("gpu/temp", settings);
        vm.OnlyPinned = true;
        Assert.Equal(new[] { "gpu/temp" }, VisibleIds(vm));
        vm.TogglePin("gpu/temp", settings);
        Assert.Empty(VisibleIds(vm));
    }

    [Fact]
    public void Snapshot_do_AppSettings_inclui_PinnedSensors()
    {
        var s = new AppSettings();
        s.PinnedSensors.Add("/amdcpu/0/temperature/2");
        var copy = s.Snapshot();
        Assert.NotSame(s.PinnedSensors, copy.PinnedSensors);
        var back = JsonSerializer.Deserialize<AppSettings>(s.SerializeSnapshot())!;
        Assert.Equal(new[] { "/amdcpu/0/temperature/2" }, back.PinnedSensors);
    }

    [Fact]
    public void Migracao_cria_PinnedSensors_vazio_em_arquivo_antigo()
    {
        var absent = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.Empty(absent.PinnedSensors);

        var nulled = JsonSerializer.Deserialize<AppSettings>("""{"PinnedSensors":null}""")!;
        Infrastructure.SettingsMigration.Apply(nulled, logicalProcessors: 8);
        Assert.Empty(nulled.PinnedSensors);

        var legacy = JsonSerializer.Deserialize<AppSettings>("""{"PinnedSensors":["x"]}""")!;
        LegacySettingsMigration.Sanitize(legacy);
        Assert.Empty(legacy.PinnedSensors);
    }

    [Fact]
    public void Sparkline_e_congelada_e_tem_no_maximo_60_pontos()
    {
        // Thread própria: a PointCollection cria um Dispatcher na thread em que nasce, e uma thread do pool não pode ficar com um.
        XamlSmokeTests.OnSta(() =>
        {
            var vm = new SensorPageViewModel();
            var settings = new AppSettings();
            var monitor = Sample();
            vm.Rebuild(monitor, settings);
            vm.TogglePin("cpu/temp", settings);
            var item = vm.Pinned[0];
            for (int i = 0; i < 100; i++)
            {
                monitor.Rows[0].Update(40 + i % 7, 40, 46);
                vm.Refresh(monitor);
            }
            Assert.NotNull(item.SparklinePoints);
            Assert.True(item.SparklinePoints!.IsFrozen);
            Assert.Equal(SensorItemViewModel.HistorySize, item.SparklinePoints.Count);
            Assert.Equal(60, item.SparklinePoints.Count);
            Assert.All(item.SparklinePoints, p => Assert.InRange(p.Y, 0, 100));

            vm.TogglePin("cpu/temp", settings);
            Assert.Null(item.SparklinePoints);
        });
    }

    [Fact]
    public void Sparkline_de_valor_parado_nao_divide_por_zero()
    {
        // Thread própria: a PointCollection cria um Dispatcher na thread em que nasce, e uma thread do pool não pode ficar com um.
        XamlSmokeTests.OnSta(() =>
        {
            var vm = new SensorPageViewModel();
            var settings = new AppSettings();
            var monitor = Sample();
            vm.Rebuild(monitor, settings);
            vm.TogglePin("cpu/temp", settings);
            for (int i = 0; i < 3; i++) vm.Refresh(monitor);
            Assert.All(vm.Pinned[0].SparklinePoints!, p => Assert.Equal(50, p.Y));
        });
    }

    [Fact]
    public void Format_de_Level_multiplica_por_100()
    {
        Assert.Equal("50 %", Row("l", "x", HardwareType.Motherboard, SensorType.Level, "Nível", 0.5f).Value);
        Assert.Equal("75 %", Row("l", "x", HardwareType.Motherboard, SensorType.Level, "Nível", 0.75f).Value);
    }

    [Fact]
    public void Format_de_Level_nao_multiplica_se_ja_em_100()
    {
        Assert.Equal("75 %", Row("l", "x", HardwareType.Motherboard, SensorType.Level, "Nível", 75f).Value);
        Assert.Equal("2 %", Row("l", "x", HardwareType.Storage, SensorType.Level, "Percentage Used", 2f).Value);
    }

    [Fact]
    public void Status_diz_o_estado_antes_de_ter_linhas()
    {
        var vm = new SensorPageViewModel();
        Assert.Equal(SensorStatus.Detecting, vm.StatusLine);
        Assert.False(vm.NeedsDiagnostic);
        vm.SetMonitorState(open: true);
        Assert.Equal(SensorStatus.Empty, vm.StatusLine);
        Assert.True(vm.NeedsDiagnostic);
        vm.SetMonitorState(open: false, failure: "Sensores indisponíveis: motivo");
        Assert.Equal("Sensores indisponíveis: motivo", vm.StatusLine);
        Assert.True(vm.NeedsDiagnostic);
    }
}
