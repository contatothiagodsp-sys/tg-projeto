using System.Text.RegularExpressions;

namespace ThgOptimization;

/// <summary>
/// Ordem de cores sugerida por fabricante. É uma heurística: a ordem real depende da fita ou do fan ligado no conector, por
/// isso a escolha manual do usuário sempre vence. Só os conectores endereçáveis de 5 V usam GRB; os de 12 V não têm essa
/// variação, então nunca trocam os canais.
/// </summary>
public static class ColorOrderAuto
{
    public const string Rgb = "RGB", Grb = "GRB";

    private static readonly (ushort Vid, string Maker)[] VendorIds =
    {
        (0x0B05, "ASUS"), (0x1462, "MSI"), (0x1044, "Gigabyte"), (0x1B1C, "Corsair"), (0x1532, "Razer"), (0x046D, "Logitech"),
    };

    private static readonly Regex VidInText = new(@"vid[_&=]?([0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Addressable = new(@"addressable|argb|d[_-]?led|jrainbow|\b5\s?v\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        var m = VidInText.Match(text);
        if (m.Success && MakerFromVid(Convert.ToUInt16(m.Groups[1].Value, 16)) is string byVid) return byVid;
        if (Regex.IsMatch(text, @"\b(ASUS|ASUSTeK|ROG|Aura)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "ASUS";
        if (Regex.IsMatch(text, @"\b(Gigabyte|Aorus|RGB Fusion)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Gigabyte";
        if (Regex.IsMatch(text, @"\b(MSI|Mystic Light|Micro-Star)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "MSI";
        if (Regex.IsMatch(text, @"\bCorsair\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Corsair";
        if (Regex.IsMatch(text, @"\bRazer\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Razer";
        if (Regex.IsMatch(text, @"\bLogitech\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Logitech";
        return null;
    }

    /// <summary>GRB só em conector endereçável de ASUS e Gigabyte. Todo o resto, inclusive fabricante desconhecido, fica em RGB.</summary>
    public static string Resolve(string backend, string name, string detail)
    {
        string? maker = DetectMaker(backend, name, detail);
        return maker is "ASUS" or "Gigabyte" && Addressable.IsMatch(name) ? Grb : Rgb;
    }
}
