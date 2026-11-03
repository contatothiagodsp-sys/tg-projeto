namespace ThgOptimization.Tests;

/// <summary>
/// A lógica da cópia "valor de antes|valor gravado". EnsurePerfOptions e RestoreAll chamam AppSettings.Save(), que gravaria
/// nas configurações reais do PC, e exigem HKLM: aqui o registro é simulado por uma variável.
/// </summary>
public class CompetitiveBackupTests
{
    private const int AboveNormal = CompetitiveProfiles.CpuAboveNormal, High = CompetitiveProfiles.CpuHigh;

    /// <summary>Mesmo caminho do EnsurePerfOptions: não mexe se já está no valor; senão atualiza a cópia e grava.</summary>
    private static void Ensure(ref string? backup, ref int? registry, int value)
    {
        if (registry == value) return;
        backup = CompetitiveProfiles.NextBackup(backup, registry, value);
        registry = value;
    }

    [Theory]
    [InlineData(null)]  // o valor não existia
    [InlineData(2)]     // existia outro valor
    public void Duas_prioridades_seguidas_e_restaurar_volta_ao_original(int? original)
    {
        string? backup = null;
        int? registry = original;

        Ensure(ref backup, ref registry, AboveNormal);
        Ensure(ref backup, ref registry, High);

        Assert.Equal($"{original?.ToString() ?? "<ausente>"}|{High}", backup);
        Assert.True(CompetitiveProfiles.ShouldRestore(backup, registry!.Value, out int? volta));
        Assert.Equal(original, volta);
    }

    [Fact]
    public void Sem_a_correcao_o_gravado_ficaria_defasado()
    {
        // O que o código antigo deixava: cópia da primeira gravação, com o registro já no segundo valor.
        Assert.False(CompetitiveProfiles.ShouldRestore($"<ausente>|{AboveNormal}", High, out _));
    }

    [Fact]
    public void Valor_mudado_pelo_usuario_depois_nao_e_restaurado()
    {
        string? backup = null;
        int? registry = null;
        Ensure(ref backup, ref registry, High);

        Assert.False(CompetitiveProfiles.ShouldRestore(backup, 99, out _));
    }

    [Fact]
    public void Mesmo_valor_nao_mexe_na_copia()
    {
        string? backup = null;
        int? registry = 5;
        Ensure(ref backup, ref registry, High);
        string? primeira = backup;
        Ensure(ref backup, ref registry, High);
        Assert.Equal(primeira, backup);
    }

    [Theory]
    [InlineData("lixo")]
    [InlineData("a|b|c")]
    [InlineData("")]
    public void Copia_invalida_recomeca_do_valor_atual(string invalida) =>
        Assert.Equal($"7|{High}", CompetitiveProfiles.NextBackup(invalida, 7, High));

    [Theory]
    [InlineData(null)]
    [InlineData("lixo")]
    public void Copia_invalida_nunca_restaura(string? invalida) =>
        Assert.False(CompetitiveProfiles.ShouldRestore(invalida, High, out _));
}
