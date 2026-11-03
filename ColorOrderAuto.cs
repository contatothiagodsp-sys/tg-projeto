using System.Text.RegularExpressions;

namespace ThgOptimization;

/// <summary>
/// Ordem de cores padrão e fabricante do aparelho. O padrão é sempre RGB: o OpenRGB já conhece o perfil de cada placa-mãe e converte
/// na ponta do driver, então adivinhar GRB trocaria os canais uma segunda vez. Quem tem uma fita fora do padrão escolhe a ordem na lista.
/// </summary>
public static partial class ColorOrderAuto
{
    public const string Rgb = "RGB", Grb = "GRB";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly (ushort Vid, string Maker)[] VendorIds =
    {
        (0x0B05, "ASUS"), (0x1462, "MSI"), (0x1044, "Gigabyte"), (0x1B1C, "Corsair"), (0x1532, "Razer"), (0x046D, "Logitech"),
        (0x26CE, "ASRock"), (0x0CF2, "LianLi"), (0x1E71, "NZXT"), (0x2516, "CoolerMaster"),
    };

    [GeneratedRegex(@"vid[_&=]?([0-9a-f]{4})", Options)] private static partial Regex VidInText();
    [GeneratedRegex(@"\b(ASUS|ASUSTeK|ROG|Aura)\b", Options)] private static partial Regex AsusName();
    [GeneratedRegex(@"\b(Gigabyte|Aorus|RGB Fusion)\b", Options)] private static partial Regex GigabyteName();
    [GeneratedRegex(@"\b(MSI|Mystic Light|Micro-Star)\b", Options)] private static partial Regex MsiName();
    [GeneratedRegex(@"\b(ASRock|Polychrome)\b", Options)] private static partial Regex AsrockName();
    [GeneratedRegex(@"\bCorsair\b", Options)] private static partial Regex CorsairName();
    [GeneratedRegex(@"\bRazer\b", Options)] private static partial Regex RazerName();
    [GeneratedRegex(@"\bLogitech\b", Options)] private static partial Regex LogitechName();
    [GeneratedRegex(@"\b(Lian[\s-]?Li|UNI\s?FAN|Strimer)\b", Options)] private static partial Regex LianLiName();
    [GeneratedRegex(@"\b(NZXT|Kraken)\b", Options)] private static partial Regex NzxtName();
    [GeneratedRegex(@"\bCooler\s?Master\b", Options)] private static partial Regex CoolerMasterName();

    public static string? MakerFromVid(ushort vid)
    {
        foreach (var (v, maker) in VendorIds) if (v == vid) return maker;
        return null;
    }

    /// <summary>Fabricante pelo VID (quando o texto traz um) ou pelo nome. A ponte do SDK já diz o fabricante.</summary>
    public static string? DetectMaker(string backend, string name, string detail)
    {
        if (backend.Contains("Corsair", StringComparison.OrdinalIgnoreCase)) return "Corsair";
        if (backend.Contains("Razer", StringComparison.OrdinalIgnoreCase)) return "Razer";
        string text = name + " " + detail;
        var m = VidInText().Match(text);
        if (m.Success && MakerFromVid(Convert.ToUInt16(m.Groups[1].Value, 16)) is string byVid) return byVid;
        if (AsusName().IsMatch(text)) return "ASUS";
        if (GigabyteName().IsMatch(text)) return "Gigabyte";
        if (MsiName().IsMatch(text)) return "MSI";
        if (AsrockName().IsMatch(text)) return "ASRock";
        if (CorsairName().IsMatch(text)) return "Corsair";
        if (RazerName().IsMatch(text)) return "Razer";
        if (LogitechName().IsMatch(text)) return "Logitech";
        if (LianLiName().IsMatch(text)) return "LianLi";
        if (NzxtName().IsMatch(text)) return "NZXT";
        if (CoolerMasterName().IsMatch(text)) return "CoolerMaster";
        return null;
    }

    /// <summary>Ordem padrão de um aparelho: sempre RGB. O app não tem ponte que fale direto com o LED, sem SDK nem OpenRGB no meio.</summary>
    public static string Resolve(string backend, string name, string detail) => Rgb;
}
