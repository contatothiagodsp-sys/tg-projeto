namespace ThgOptimization.Core.Oc;

/// <summary>O aceite do risco vale para a placa que estava no PC quando o usuário leu o aviso. Trocou de placa, lê de novo.</summary>
public static class OcRisk
{
    public static string HardwareId(string vendor, string model) => $"{vendor.Trim()}:{model.Trim()}";

    public static bool IsAccepted(bool accepted, string? storedId, string currentId) =>
        accepted && string.Equals(storedId, currentId, StringComparison.OrdinalIgnoreCase);
}
