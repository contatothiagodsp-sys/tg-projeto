namespace ThgOptimization.Core.Oc;

/// <summary>O aceite do risco vale para a placa que estava no PC quando o usuário leu o aviso. Trocou de placa, lê de novo.</summary>
public static class OcRisk
{
    public static string HardwareId(string vendor, string model) => $"{vendor.Trim()}:{model.Trim()}";

    /// <summary>
    /// Quem aceitou antes da 1.7 não tem a placa guardada. O aceite foi dado para a placa que continua aí até prova em contrário, então ela é adotada
    /// sem perguntar de novo. Uma troca feita antes de atualizar para a 1.7 passa despercebida uma vez.
    /// </summary>
    public static string? MigratedId(bool accepted, string? storedId, string currentId) =>
        accepted && string.IsNullOrEmpty(storedId) && currentId.Length > 0 ? currentId : storedId;

    public static bool IsAccepted(bool accepted, string? storedId, string currentId) =>
        accepted && string.Equals(storedId, currentId, StringComparison.OrdinalIgnoreCase);
}
