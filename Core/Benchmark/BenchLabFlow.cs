namespace ThgOptimization.Core.Benchmark;

/// <summary>Regras do fluxo do Performance Lab, fora da janela para poderem ser testadas.</summary>
public static class BenchLabFlow
{
    /// <summary>Captura interrompida antes disso não representa os 60 s das outras, e é descartada.</summary>
    public const double MinKeptSeconds = 30;

    /// <summary>A repetição vai para o lado com menos capturas, para os dois crescerem juntos.</summary>
    public static string NextSlot(int capturesA, int capturesB) => capturesA <= capturesB ? "A" : "B";

    /// <summary>Comparar jogos diferentes não mede nada. Mensagem para o usuário, ou null se estiver tudo certo.</summary>
    public static string? GameConflict(string? experimentGame, string foregroundGame) =>
        experimentGame != null && !string.Equals(experimentGame, foregroundGame, StringComparison.OrdinalIgnoreCase)
            ? $"Este teste é de {experimentGame}, mas {foregroundGame} está em primeiro plano. Volte ao jogo ou comece um NOVO TESTE."
            : null;
}
