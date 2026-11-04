using ThgOptimization.Core.Performance;

namespace ThgOptimization.Tests;

public class SchedulerTweakTests
{
    private sealed class FakeCell : IRegistryDword
    {
        public int? Value;
        public int Writes;
        public int? Read() => Value;
        public void Write(int value) { Value = value; Writes++; }
        public void Delete() => Value = null;
    }

    [Fact]
    public void Item_w32_aparece_com_valor_atual()
    {
        var check = SchedulerTweak.BuildW32Check(0x26, hasBackup: false);
        Assert.Equal("comp.scheduler.measure", check.Id);
        Assert.Equal(CheckLevel.Info, check.Level);
        Assert.Contains("0x26", check.Found);
        Assert.Equal("APLICAR 0x2A", check.SecondaryLabel);
    }

    [Fact]
    public void Item_mmcss_gpu_aparece_com_valor_atual()
    {
        var check = SchedulerTweak.BuildGpuCheck(4, hasBackup: false);
        Assert.Contains("4", check.Found);
        Assert.Equal("APLICAR 8", check.SecondaryLabel);
        Assert.Empty(SchedulerTweak.BuildGpuCheck(8, false).SecondaryLabel);
    }

    /// <summary>Os dois são guia e medição: nenhum entra na correção automática nem no "corrigir tudo".</summary>
    [Fact]
    public void Itens_sao_guia_e_nunca_correcao_automatica()
    {
        foreach (var check in new[] { SchedulerTweak.BuildW32Check(0x26, false), SchedulerTweak.BuildGpuCheck(4, false) })
        {
            Assert.Equal(FixKind.Bios, check.Fix);
            Assert.Null(check.AutoFix);
            Assert.False(check.InTurbo);
        }
    }

    [Fact]
    public void AutoFix_de_w32_exige_confirmacao()
    {
        var cell = new FakeCell { Value = 0x26 };
        var s = new AppSettings();
        Assert.NotEmpty(SchedulerTweak.Apply(SchedulerTweak.W32Backup, 0x2A, confirmed: false, cell, s));
        Assert.Equal(0, cell.Writes);
        Assert.Empty(s.TweakBackup);

        Assert.Empty(SchedulerTweak.Apply(SchedulerTweak.W32Backup, 0x2A, confirmed: true, cell, s));
        Assert.Equal(0x2A, cell.Value);
        Assert.Equal("d:38", s.TweakBackup[SchedulerTweak.W32Backup]);
    }

    [Fact]
    public void Restaurar_de_w32_devolve_valor_anterior()
    {
        var cell = new FakeCell { Value = 0x26 };
        var s = new AppSettings();
        SchedulerTweak.Apply(SchedulerTweak.W32Backup, 0x2A, true, cell, s);
        SchedulerTweak.Apply(SchedulerTweak.W32Backup, 0x18, true, cell, s); // aplicar de novo não apaga o original

        Assert.True(SchedulerTweak.Restore(SchedulerTweak.W32Backup, cell, s));
        Assert.Equal(0x26, cell.Value);
        Assert.False(s.TweakBackup.ContainsKey(SchedulerTweak.W32Backup));
    }

    [Fact]
    public void Restaurar_valor_que_nao_existia_apaga()
    {
        var cell = new FakeCell();
        var s = new AppSettings();
        SchedulerTweak.Apply(SchedulerTweak.GpuBackup, 8, true, cell, s);
        Assert.True(SchedulerTweak.Restore(SchedulerTweak.GpuBackup, cell, s));
        Assert.Null(cell.Value);
    }

    [Theory]
    [InlineData("texto qualquer")]
    [InlineData("s:0x2A")]
    public void Copia_em_formato_estranho_nao_grava_nada(string previous)
    {
        var cell = new FakeCell { Value = 0x26 };
        var s = new AppSettings();
        s.TweakBackup[SchedulerTweak.W32Backup] = previous;
        Assert.False(SchedulerTweak.Restore(SchedulerTweak.W32Backup, cell, s));
        Assert.Equal(0, cell.Writes);
    }

    [Theory]
    [InlineData(0x2A, "intervalos curtos, fixos, primeiro plano 3× maior")]
    [InlineData(0x26, "intervalos curtos, variáveis, primeiro plano 3× maior")]
    [InlineData(0x18, "intervalos longos, fixos, sem vantagem para o primeiro plano")]
    public void Valor_e_explicado_pelos_bits(int value, string text)
    {
        Assert.Equal(text, SchedulerTweak.DescribeW32(value));
    }

    [Fact]
    public void Desfazer_do_shield_conhece_as_copias()
    {
        Assert.False(SchedulerTweak.RestoreByKey("sec.outra", new AppSettings()));
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoText.Root, "SecurityAudit.cs"));
        Assert.Contains("SchedulerTweak.RestoreByKey(key, s)", source);
    }
}
