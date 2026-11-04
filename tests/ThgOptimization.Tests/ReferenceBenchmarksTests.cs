using ThgOptimization.Core.Benchmark;

namespace ThgOptimization.Tests;

public class ReferenceBenchmarksTests
{
    private const string Json = """
        { "Version": 1,
          "Gpus": { "RX 9070 XT": { "rt": { "alta-1440p": 878 }, "game": { "1080p": 680 } },
                    "RTX 4070": { "rt": { "alta-1440p": 500 } } },
          "Cpus": { "Ryzen 7 9800X3D": { "bench-mult": 4800 } } }
        """;

    private static ReferenceBenchmarks Refs() => ReferenceBenchmarks.Parse(Json)!;

    [Fact]
    public void Json_valido_carrega()
    {
        Assert.NotNull(ReferenceBenchmarks.Parse(Json));
        Assert.Null(ReferenceBenchmarks.Parse("{ quebrado"));
        Assert.Null(ReferenceBenchmarks.Parse("""{ "Version": 0 }"""));
    }

    [Theory]
    [InlineData("RX 9070 XT")]
    [InlineData("AMD Radeon RX 9070 XT")]
    [InlineData("amd radeon rx 9070 xt")]
    public void Match_por_nome_exato_e_prefixo(string gpu)
    {
        Assert.Equal("RX 9070 XT", Refs().FindGpu(gpu, "rt", "alta-1440p", 800)!.Hardware);
    }

    /// <summary>Outro modelo não pode herdar a referência: uma RTX 4070 Ti comparada à 4070 daria um número falso.</summary>
    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070 Ti")]
    [InlineData("NVIDIA GeForce RTX 4070 SUPER")]
    [InlineData("NVIDIA GeForce RTX 40700")]
    [InlineData("AMD Radeon RX 9070")]
    [InlineData("Intel Arc B580")]
    public void Sem_match_devolve_null(string gpu)
    {
        Assert.Null(Refs().FindGpu(gpu, "rt", "alta-1440p", 800));
    }

    [Fact]
    public void Preset_sem_referencia_devolve_null()
    {
        Assert.Null(Refs().FindGpu("RX 9070 XT", "rt", "extrema-2160p", 800));
        Assert.Null(Refs().FindGpu("RX 9070 XT", "outro", "1080p", 800));
    }

    [Fact]
    public void Percentil_calculado_sobre_mediana()
    {
        var r = Refs().FindGpu("AMD Radeon RX 9070 XT", "rt", "alta-1440p", 736)!;
        Assert.Equal(736 / 878.0 * 100, r.Percent, precision: 6);
        Assert.Equal("Referência para RX 9070 XT: 878 · você está em 84% (736)", r.Line);
    }

    [Fact]
    public void Cpu_acha_pelo_nome_do_windows()
    {
        Assert.NotNull(Refs().FindCpu("AMD Ryzen 7 9800X3D 8-Core Processor", "bench-mult", 4000));
        Assert.Null(Refs().FindCpu("AMD Ryzen 7 7800X3D 8-Core Processor", "bench-mult", 4000));
    }

    [Fact]
    public void Compartilhar_gera_markdown()
    {
        string md = Refs().FindGpu("RX 9070 XT", "game", "1080p", 680)!.Markdown;
        Assert.Contains("| game 1080p | 680 | 680 | 100% |", md);
    }

    [Fact]
    public void Json_embutido_existe_no_assembly()
    {
        using var stream = typeof(ReferenceBenchmarks).Assembly.GetManifestResourceStream(ReferenceBenchmarks.ResourceName);
        Assert.NotNull(stream);
        Assert.NotNull(ReferenceBenchmarks.Load());
    }
}
