using ThgOptimization.Core.Performance;

namespace ThgOptimization.Tests;

public class X3dGuardianTests
{
    [Fact]
    public void Plano_balanced_e_otimizador_instalado_nao_tem_problema()
    {
        var status = X3dGuardian.Evaluate(true, optimizerInstalled: true, gameModeOn: true, balancedPlan: true);
        Assert.Null(status.Problem);
        Assert.Equal(CheckLevel.Ok, X3dGuardian.BuildCheck(status, null)!.Level);
    }

    [Fact]
    public void Plano_high_performance_gera_problema()
    {
        var status = X3dGuardian.Evaluate(true, optimizerInstalled: true, gameModeOn: true, balancedPlan: false);
        Assert.Contains("Equilibrado", status.Problem);
        var check = X3dGuardian.BuildCheck(status, null)!;
        Assert.Equal(CheckLevel.Warn, check.Level);
        Assert.Equal(FixKind.Auto, check.Fix);
        Assert.NotNull(check.AutoFix);
    }

    [Fact]
    public void Sem_otimizador_gera_problema()
    {
        var status = X3dGuardian.Evaluate(true, optimizerInstalled: false, gameModeOn: true, balancedPlan: true);
        Assert.Contains("otimizador", status.Problem);
        // Instalar o driver é com o item cpu.x3dopt: aqui, sem correção automática que não resolveria.
        Assert.Equal(FixKind.None, X3dGuardian.BuildCheck(status, null)!.Fix);
    }

    [Fact]
    public void Modo_de_jogo_desligado_gera_problema()
    {
        Assert.Contains("Modo de Jogo", X3dGuardian.Evaluate(true, true, gameModeOn: false, balancedPlan: true).Problem);
    }

    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor")]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor")]
    [InlineData("Intel(R) Core(TM) i9-14900K")]
    public void Cpu_nao_x3d_nao_gera(string cpu)
    {
        var status = X3dGuardian.Check(cpu);
        Assert.False(status.IsDualCcdX3d);
        Assert.Null(status.Problem);
        Assert.Null(X3dGuardian.BuildCheck(status, null));
    }

    [Fact]
    public void Peso_zero_para_nao_contar_duas_vezes_na_nota()
    {
        Assert.Equal(0, X3dGuardian.BuildCheck(X3dGuardian.Evaluate(true, true, true, false), null)!.Weight);
    }

    [Fact]
    public void Toast_uma_vez_por_sessao()
    {
        var problem = X3dGuardian.Evaluate(true, true, true, balancedPlan: false);
        var notice = new X3dGuardian.SessionNotice();
        Assert.NotNull(notice.Next(problem));
        Assert.Null(notice.Next(problem));
        Assert.NotNull(new X3dGuardian.SessionNotice().Next(problem)); // sessão nova, aviso novo
        Assert.Null(new X3dGuardian.SessionNotice().Next(X3dGuardian.Evaluate(true, true, true, true)));
    }
}
