using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ThgOptimization.Tests;

public class AppUpdaterVersionTests
{
    [Theory]
    [InlineData("v1.6.0", "tHg Optimization 1.5.9", "1.6.0")] // a tag manda
    [InlineData("latest", "tHg Optimization 1.5.207", "1.5.207")] // tag sem versão: usa o título
    [InlineData("1.2.3", "", "1.2.3")]
    public void ParseVersion_prefere_a_tag(string tag, string title, string esperada) =>
        Assert.Equal(new Version(esperada), AppUpdater.ParseVersion(tag, title));

    [Theory]
    [InlineData("latest", "sem versão")]
    [InlineData("", "")]
    [InlineData("99999999999.0.0", "")] // não cabe em Version: não pode lançar exceção
    public void ParseVersion_sem_versao_devolve_null(string tag, string title) =>
        Assert.Null(AppUpdater.ParseVersion(tag, title));
}

public class BalanceGuardTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Balance_sem_dados_da_cpu_nao_divide_por_zero(double cpu)
    {
        var (ratio, text) = BottleneckAnalyzer.Balance(100, cpu);
        Assert.Equal(1.0, ratio);
        Assert.Contains("insuficientes", text);
    }
}

public class FanCurveCacheTests
{
    private static FanChannelView View(string preset = "custom", string custom = "30:20, 70:80") =>
        new(new FanOutput("id", "chip", "fan", _ => { }, () => { }, () => ""), new FanChannelSettings { Preset = preset, Custom = custom });

    [Fact]
    public void Mesma_curva_nao_e_recalculada()
    {
        var v = View();
        Assert.Same(v.CurvePoints(), v.CurvePoints());
    }

    [Fact]
    public void Mudar_o_texto_personalizado_recalcula()
    {
        var v = View();
        var antes = v.CurvePoints();
        v.Custom = "30:50, 70:90";
        Assert.NotSame(antes, v.CurvePoints());
        Assert.Equal(50, v.CurvePoints()[0].P);
    }

    [Fact]
    public void Mudar_o_preset_recalcula()
    {
        var v = View();
        var antes = v.CurvePoints();
        v.Preset = "turbo";
        Assert.NotSame(antes, v.CurvePoints());
        Assert.Equal(FanCurves.Curve(v.Config), v.CurvePoints());
    }

    [Fact]
    public void Mudanca_direta_na_configuracao_tambem_recalcula()
    {
        var v = View();
        var antes = v.CurvePoints();
        v.Config.Custom = "10:10, 20:20";
        Assert.NotSame(antes, v.CurvePoints());
    }

    [Fact]
    public void Step_usa_a_curva_nova_depois_da_mudanca()
    {
        var v = View(custom: "0:40, 100:40");
        var g = new FanSettings { RiseRate = 1000, FallRate = 1000, FallDelayS = 0, HysteresisC = 0 };
        FanCurves.Step(v, g, 50, null, 1);
        Assert.Equal(40, v.Duty, 3);
        v.Custom = "0:70, 100:70";
        FanCurves.Step(v, g, 50, null, 1);
        Assert.Equal(70, v.Duty, 3);
    }
}

public class BatteryMatchTests
{
    private static Dictionary<string, object?> Row(string? inst, uint? tag, long valor)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Valor"] = valor };
        if (inst != null) d["InstanceName"] = inst;
        if (tag != null) d["Tag"] = tag;
        return d;
    }

    [Fact]
    public void Casa_por_InstanceName_mesmo_fora_de_ordem()
    {
        var rows = new List<Dictionary<string, object?>> { Row("BAT1", 2, 22), Row("BAT0", 1, 11) };
        Assert.Equal(11L, BatteryInfo.Match(rows, Row("BAT0", 1, 0), 0)!["Valor"]);
        Assert.Equal(22L, BatteryInfo.Match(rows, Row("BAT1", 2, 0), 1)!["Valor"]);
    }

    [Fact]
    public void InstanceName_ignora_maiusculas()
    {
        var rows = new List<Dictionary<string, object?>> { Row("ACPI\\PNP0C0A\\1_0", null, 5) };
        Assert.Equal(5L, BatteryInfo.Match(rows, Row("acpi\\pnp0c0a\\1_0", null, 0), 0)!["Valor"]);
    }

    [Fact]
    public void Sem_InstanceName_casa_pela_Tag()
    {
        var rows = new List<Dictionary<string, object?>> { Row(null, 2, 22), Row(null, 1, 11) };
        Assert.Equal(11L, BatteryInfo.Match(rows, Row(null, 1, 0), 0)!["Valor"]);
    }

    [Fact]
    public void Sem_identificacao_nenhuma_casa_pela_posicao()
    {
        var rows = new List<Dictionary<string, object?>> { Row(null, null, 1), Row(null, null, 2) };
        Assert.Equal(2L, BatteryInfo.Match(rows, Row(null, null, 0), 1)!["Valor"]);
        Assert.Equal(2L, BatteryInfo.Match(rows, null, 1)!["Valor"]);
    }

    [Fact]
    public void Linha_de_outra_bateria_nao_e_usada_pela_posicao()
    {
        var rows = new List<Dictionary<string, object?>> { Row("BAT1", 2, 22) };
        Assert.Null(BatteryInfo.Match(rows, Row("BAT0", 1, 0), 0));
    }

    [Fact]
    public void Indice_fora_da_lista_devolve_null() =>
        Assert.Null(BatteryInfo.Match(new List<Dictionary<string, object?>>(), null, 3));
}

public class InstallerRunningTests
{
    [Fact]
    public void Acha_processo_que_roda_da_pasta()
    {
        string dir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        Assert.True(DriverInstallers.AnyRunningFrom(new[] { dir }));
    }

    [Fact]
    public void Pasta_sem_processo_devolve_false() =>
        Assert.False(DriverInstallers.AnyRunningFrom(new[] { @"C:\thg-pasta-que-nao-existe" }));

    /// <summary>Antes eram mais de 100 Win32Exception por chamada (processos protegidos no MainModule).</summary>
    [Fact]
    public void Nao_gera_excecao_nos_processos_protegidos()
    {
        int thread = Environment.CurrentManagedThreadId, count = 0;
        void Handler(object? _, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread && e.Exception is Win32Exception) count++;
        }
        AppDomain.CurrentDomain.FirstChanceException += Handler;
        try { DriverInstallers.AnyRunningFrom(new[] { @"C:\thg-pasta-que-nao-existe" }); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Handler; }
        Assert.Equal(0, count);
    }
}

public sealed class AmdSnapshotSequenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("thg-amd2-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Zip(string name, string json)
    {
        string path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var s = zip.CreateEntry("rssettings.json").Open();
        s.Write(Encoding.UTF8.GetBytes(json));
        return path;
    }

    /// <summary>O resultado de um arquivo não pode valer para o próximo.</summary>
    [Fact]
    public void Uma_chamada_nao_influencia_a_seguinte()
    {
        AmdSnapshot.Generate(Zip("a.zip", """{"S":[{"Name":"Radeon Chill","Value":"TRUE"}]}"""), Path.Combine(_dir, "a-out.zip"));
        Assert.Throws<InvalidDataException>(() =>
            AmdSnapshot.Generate(Zip("b.zip", """{"S":[{"Name":"Outra","Value":"x"}]}"""), Path.Combine(_dir, "b-out.zip")));
    }
}
