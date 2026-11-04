using System.Management;

namespace ThgOptimization.Core.Performance;

/// <summary>
/// Ryzen X3D de dois CCDs: quem leva o jogo ao CCD com V-Cache é o estacionamento de núcleos, que depende do otimizador da AMD, do Modo de Jogo do Windows e do
/// plano Equilibrado. O tHg já não troca o plano nessas CPUs; aqui ele confere se alguém trocou.
/// </summary>
public static class X3dGuardian
{
    public sealed record X3dStatus(bool IsDualCcdX3d, bool OptimizerInstalled, bool GameModeOn, bool BalancedPlan, string? Problem);

    /// <summary>A primeira falha, em frase curta. O otimizador vem antes: sem ele, os outros dois não adiantam.</summary>
    public static X3dStatus Evaluate(bool isDualCcdX3d, bool optimizerInstalled, bool gameModeOn, bool balancedPlan)
    {
        string? problem = !isDualCcdX3d ? null
            : !optimizerInstalled ? "O otimizador do 3D V-Cache da AMD não está instalado: o jogo pode rodar no CCD sem V-Cache."
            : !balancedPlan ? "O plano de energia não é o Equilibrado: o CCD sem V-Cache não é estacionado, e o jogo pode rodar nele."
            : !gameModeOn ? "O Modo de Jogo do Windows está desligado: o otimizador da AMD não percebe que um jogo abriu."
            : null;
        return new X3dStatus(isDualCcdX3d, optimizerInstalled, gameModeOn, balancedPlan, problem);
    }

    /// <summary>Lê o estado real. WMI e powercfg: fora da thread da janela. Fora dos X3D de dois CCDs, não consulta nada.</summary>
    public static X3dStatus Check(string cpuName)
    {
        if (!WindowsTweaks.IsDualCcdX3D(cpuName)) return Evaluate(false, false, false, false);
        return Evaluate(true, OptimizerInstalled(), GameModeOn(), PowerPlan.Active() == PowerPlan.Balanced);
    }

    private static bool OptimizerInstalled()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%3D V-Cache%'");
            using var results = searcher.Get();
            return results.Count > 0;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Infrastructure.AppLog.Warn("x3d", ex);
            return true; // sem conseguir ler, não acusa falta: o item do otimizador no TURBO mostra o estado dele
        }
    }

    /// <summary>Sem o valor no registro, o Windows deixa ligado.</summary>
    private static bool GameModeOn()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
        return key?.GetValue("AutoGameModeEnabled") is not int value || value == 1;
    }

    /// <summary>Peso 0: a nota já conta o otimizador e o plano em cpu.x3dopt. Corrige sozinho só plano e Modo de Jogo; instalar o otimizador é com aquele item.</summary>
    public static GamerCheck? BuildCheck(X3dStatus status, Tweak? gameModeTweak)
    {
        if (!status.IsDualCcdX3d) return null;
        bool fixable = status.OptimizerInstalled && status.Problem != null;
        var check = new GamerCheck
        {
            Id = "cpu.x3d.guardian", Area = "WINDOWS", Title = "Jogo no CCD com 3D V-Cache", Weight = 0,
            Level = status.Problem != null ? CheckLevel.Warn : CheckLevel.Ok,
            Found = status.Problem ?? "Otimizador instalado, plano Equilibrado e Modo de Jogo ligado.",
            Advice = "Em Ryzen X3D de dois CCDs, o estacionamento de núcleos é o que manda o jogo para o CCD com V-Cache, e o plano de alto desempenho desliga esse estacionamento.",
            Fix = fixable ? FixKind.Auto : FixKind.None, FixLabel = "CORRIGIR",
        };
        if (fixable) check.AutoFix = s => Repair(status, gameModeTweak, s);
        return check;
    }

    private static string Repair(X3dStatus status, Tweak? gameMode, AppSettings s)
    {
        if (!status.BalancedPlan && SecurityAudit.Run("powercfg.exe", $"/setactive {PowerPlan.Balanced}", 10_000).Code != 0)
            return "O Windows recusou a troca do plano.";
        // Pelo ajuste do OTIMIZA: guarda o valor anterior, e DESFAZER TUDO volta.
        if (!status.GameModeOn) gameMode?.Write(true, s);
        return "";
    }

    /// <summary>Aviso ao abrir o jogo: um por sessão.</summary>
    public sealed class SessionNotice
    {
        private bool _warned;

        public string? Next(X3dStatus status)
        {
            if (_warned || status.Problem == null) return null;
            _warned = true;
            return status.Problem;
        }
    }
}
