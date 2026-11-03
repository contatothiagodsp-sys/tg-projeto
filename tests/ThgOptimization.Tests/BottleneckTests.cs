namespace ThgOptimization.Tests;

public class BottleneckTests
{
    private static LoadSample Amostra(float? gpu = null, float? cpuTotal = null, float? cpuCore = null, double? vram = null,
                                      float? ram = null, float? cpuTemp = null, bool gpuThermal = false) =>
        new(gpu, cpuTotal, cpuCore, null, vram, ram, null, cpuTemp, gpuThermal, null);

    [Fact] public void Vram_cheia_vem_primeiro() => Assert.Equal(Limiter.Vram, Amostra(gpu: 99, vram: 97, cpuTemp: 99).Classify());
    [Fact] public void Termico_pela_gpu() => Assert.Equal(Limiter.Thermal, Amostra(gpu: 99, gpuThermal: true).Classify());
    [Fact] public void Termico_pela_cpu() => Assert.Equal(Limiter.Thermal, Amostra(gpu: 99, cpuTemp: 95).Classify());
    [Fact] public void Gpu_no_limite() => Assert.Equal(Limiter.Gpu, Amostra(gpu: 90, cpuCore: 99).Classify());
    [Fact] public void Cpu_por_nucleo() => Assert.Equal(Limiter.Cpu, Amostra(gpu: 60, cpuCore: 85).Classify());
    [Fact] public void Cpu_pelo_total() => Assert.Equal(Limiter.Cpu, Amostra(gpu: 60, cpuTotal: 85).Classify());
    [Fact] public void Ram_cheia() => Assert.Equal(Limiter.Ram, Amostra(gpu: 60, cpuCore: 50, ram: 92).Classify());
    [Fact] public void Nada_no_limite() => Assert.Equal(Limiter.Capped, Amostra(gpu: 60, cpuCore: 50, ram: 50).Classify());
    [Fact] public void Sem_leitura_nenhuma() => Assert.Equal(Limiter.Capped, Amostra().Classify());

    [Theory]
    [InlineData(200, 100, "GPU muito mais forte")]
    [InlineData(150, 100, "GPU um pouco à frente")]
    [InlineData(100, 100, "Hardware equilibrado")]
    [InlineData(50, 100, "CPU mais forte")]
    [InlineData(30, 100, "GPU muito fraca")]
    public void Balance_escolhe_o_texto_pela_razao(double gpu, double cpu, string trecho)
    {
        var (ratio, text) = BottleneckAnalyzer.Balance(gpu, cpu);
        Assert.Equal(gpu / cpu, ratio, 6);
        Assert.Contains(trecho, text);
    }
}
