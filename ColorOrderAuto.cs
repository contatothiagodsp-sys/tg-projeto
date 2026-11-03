using System.Text.RegularExpressions;

namespace ThgOptimization;

/// <summary>
/// Identifica a ordem dos canais de cada aparelho pelo contrato da ponte, pelos metadados e pelas Regras. O OpenRGB e os SDKs recebem RGB e
/// convertem na ponta do driver, então uma regra de GRB por cima deles pode trocar os canais uma segunda vez. Por isso as regras de GRB valem só
/// nos conectores endereçáveis, onde a fita é de terceiros, entram com confiança média e sempre oferecem o "Testar cores".
/// </summary>
public static partial class ColorOrderAuto
{
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

    private const string CheckIt = "Confirme com o \"Testar cores\": a ordem física depende da fita ligada no conector.";

    /// <summary>Regras por fabricante. A primeira que bater vale. As de GRB não são medição neste aparelho: daí a confiança média.</summary>
    public static readonly IReadOnlyList<LightOrderRule> Regras = new[]
    {
        new LightOrderRule(LightChannelOrder.GRB, "Conector endereçável ASUS Aura: GRB por padrão. " + CheckIt, Maker: "ASUS", RequiresExternalHeader: true,
                           OverridesNormalizedContract: true, Confidence: DetectionConfidence.Medium, Origin: "regra ASUS"),
        new LightOrderRule(LightChannelOrder.GRB, "Conector endereçável Gigabyte RGB Fusion: GRB por padrão. " + CheckIt, Maker: "Gigabyte", RequiresExternalHeader: true,
                           OverridesNormalizedContract: true, Confidence: DetectionConfidence.Medium, Origin: "regra Gigabyte"),
        new LightOrderRule(LightChannelOrder.GRB, "Conector endereçável ASRock Polychrome: GRB por padrão. " + CheckIt, Maker: "ASRock", RequiresExternalHeader: true,
                           OverridesNormalizedContract: true, Confidence: DetectionConfidence.Medium, Origin: "regra ASRock"),
        new LightOrderRule(LightChannelOrder.RGB, "MSI Mystic Light: RGB.", Maker: "MSI", Origin: "regra MSI"),
        new LightOrderRule(LightChannelOrder.RGB, "Corsair iCUE: RGB, convertido pelo SDK.", Maker: "Corsair", Origin: "regra Corsair"),
        new LightOrderRule(LightChannelOrder.RGB, "Razer Chroma: RGB, convertido pelo SDK.", Maker: "Razer", Origin: "regra Razer"),
        new LightOrderRule(LightChannelOrder.RGB, "Logitech G HUB: RGB, convertido pelo SDK.", Maker: "Logitech", Origin: "regra Logitech"),
    };

    /// <summary>A decisão com a configuração do app: LightAutoOrder desligado = sem regras de fabricante.</summary>
    public static LightOrderResolution Effective(LightDeviceMetadata m, bool autoOrder, string? manual = null) =>
        ResolveOrder(m, autoOrder ? Regras : null, manual);

    /// <summary>Abaixo de alta, a ordem não foi comprovada neste aparelho: o usuário pode conferir com os olhos.</summary>
    public static bool NeedsColorTest(DetectionConfidence confidence) => confidence < DetectionConfidence.High;

    /// <summary>O que o "Testar cores" acende, 1 s cada, na ordem que o aparelho deve mostrar.</summary>
    public static readonly IReadOnlyList<(string Name, (byte R, byte G, byte B) Color)> TestSequence = new[]
    {
        ("vermelho", ((byte)255, (byte)0, (byte)0)),
        ("verde", ((byte)0, (byte)255, (byte)0)),
        ("azul", ((byte)0, (byte)0, (byte)255)),
    };

    [GeneratedRegex(@"vid[_&=]?([0-9a-f]{4})[^0-9a-z]*pid[_&=]?([0-9a-f]{4})", Options)] private static partial Regex VidPidInText();
    [GeneratedRegex(@"\b(ARGB|A-RGB|Addressable|D_LED\d*|JRAINBOW\d*|JRGB\d*|LED Strip|Header)\b", Options)] private static partial Regex ExternalHeaderName();

    /// <summary>VID e PID do caminho do aparelho, quando a ponte informa. Null = sem identificador real: nada é inventado.</summary>
    public static (ushort Vid, ushort Pid)? ParseVidPid(string? location)
    {
        if (string.IsNullOrEmpty(location)) return null;
        var m = VidPidInText().Match(location);
        return m.Success ? (Convert.ToUInt16(m.Groups[1].Value, 16), Convert.ToUInt16(m.Groups[2].Value, 16)) : null;
    }

    /// <summary>
    /// Ordem de um aparelho, nesta precedência: escolha manual; regra; contrato da ponte; RGB como reserva segura.
    /// Roda ao conectar ou ao mudar a configuração, nunca por quadro.
    /// </summary>
    /// <param name="rules">Regras a considerar. Por padrão nenhuma; o app passa <see cref="Regras"/> quando LightAutoOrder está ligado.</param>
    /// <param name="manual">Valor salvo em LightColorOrder para este aparelho, se houver. Texto inválido é ignorado.</param>
    public static LightOrderResolution ResolveOrder(LightDeviceMetadata m, IReadOnlyList<LightOrderRule>? rules = null, string? manual = null)
    {
        if (TryParseOrder(manual, out var chosen))
            return new(chosen, DetectionConfidence.High, "Escolhida à mão no modo Avançado.", m.Backend, "manual");

        if (rules != null)
            foreach (var rule in rules)
            {
                if (!rule.Matches(m)) continue;
                // Permutar por cima de uma ponte que já normaliza é a troca dupla: a regra precisa dizer que o contrato não vale ali.
                if (rule.Order != LightChannelOrder.RGB && m.Contract == LightBackendContract.NormalizedRgb && !rule.OverridesNormalizedContract) continue;
                return new(rule.Order, rule.Confidence, rule.Evidence, m.Backend, rule.Origin);
            }

        return m.Contract switch
        {
            LightBackendContract.NormalizedRgb when IsExternalHeader(m) => new(LightChannelOrder.RGB, DetectionConfidence.Medium,
                $"{m.Backend} recebe RGB e converte no driver, mas a fita ligada neste conector é de terceiros e pode ter outra ordem física.", m.Backend, "metadados"),
            LightBackendContract.NormalizedRgb => new(LightChannelOrder.RGB, DetectionConfidence.High,
                $"{m.Backend} recebe RGB e converte na ponta do driver: nenhuma permutação é aplicada aqui.", m.Backend, "metadados"),
            LightBackendContract.RawChannels => new(LightChannelOrder.RGB, DetectionConfidence.Low,
                $"{m.Backend} grava os canais direto e não há regra comprovada para este aparelho. Confira as cores no modo Avançado.", m.Backend, "fallback RGB"),
            _ => new(LightChannelOrder.RGB, DetectionConfidence.Unknown,
                "Sem informação suficiente sobre esta ponte: RGB é só a reserva segura.", m.Backend, "fallback RGB"),
        };
    }

    /// <summary>Conector onde o usuário liga a fita que quiser. Aparelho integrado (teclado, RAM, mouse) tem a ordem resolvida pelo driver.</summary>
    internal static bool IsExternalHeader(LightDeviceMetadata m)
    {
        if (m.ControllerType is "motherboard" or "ledstrip") return m.ZoneKind is LightZoneKind.Linear or LightZoneKind.Unknown;
        return m.ControllerType.Length == 0 && m.ZoneKind == LightZoneKind.Linear && ExternalHeaderName().IsMatch(m.ZoneName + " " + m.DeviceName);
    }

    public static bool TryParseOrder(string? text, out LightChannelOrder order)
    {
        order = LightChannelOrder.RGB;
        if (string.IsNullOrWhiteSpace(text)) return false;
        switch (text.Trim().ToUpperInvariant())
        {
            case "RGB": order = LightChannelOrder.RGB; return true;
            case "RBG": order = LightChannelOrder.RBG; return true;
            case "GRB": order = LightChannelOrder.GRB; return true;
            case "GBR": order = LightChannelOrder.GBR; return true;
            case "BRG": order = LightChannelOrder.BRG; return true;
            case "BGR": order = LightChannelOrder.BGR; return true;
            default: return false;
        }
    }
}

public enum LightChannelOrder { RGB, RBG, GRB, GBR, BRG, BGR }

public enum DetectionConfidence { Unknown, Low, Medium, High }

/// <param name="Source">De onde veio a decisão, como aparece na tela: "metadados", "regra ASUS", "manual" ou "fallback RGB".</param>
public sealed record LightOrderResolution(LightChannelOrder Order, DetectionConfidence Confidence, string Reason, string Backend, string Source = "metadados");

/// <summary>O que a ponte espera receber em Send. É dela a responsabilidade de converter para o formato físico.</summary>
public enum LightBackendContract
{
    /// <summary>A ponte não declarou.</summary>
    Unknown,
    /// <summary>Recebe R, G e B de verdade: a própria ponte ou o driver dela reordena. Aqui não se permuta.</summary>
    NormalizedRgb,
    /// <summary>Os bytes vão para o LED como estão: a ordem física precisa ser aplicada antes de enviar.</summary>
    RawChannels,
}

public enum LightZoneKind { Unknown, Single, Linear, Matrix }

/// <summary>O que se sabe de um aparelho ou zona. Campos ausentes ficam vazios, e nenhum deles é deduzido do nome comercial.</summary>
public sealed record LightDeviceMetadata(string Backend, LightBackendContract Contract, string DeviceName = "", string DeviceVendor = "",
                                         string ZoneName = "", LightZoneKind ZoneKind = LightZoneKind.Unknown, string ControllerType = "", string Location = "")
{
    public (ushort Vid, ushort Pid)? VidPid { get; } = ColorOrderAuto.ParseVidPid(Location);
}

/// <summary>
/// Ordem para um aparelho. Todo critério preenchido precisa bater, e pelo menos um dos que identificam é exigido.
/// Evidence diz de onde veio a informação e aparece para o usuário.
/// </summary>
/// <param name="Maker">Fabricante segundo ColorOrderAuto.DetectMaker.</param>
/// <param name="RequiresExternalHeader">Só em conector onde o usuário liga a fita que quiser. Teclado, RAM e mouse têm a ordem resolvida pelo driver.</param>
public sealed record LightOrderRule(LightChannelOrder Order, string Evidence, string? Backend = null, ushort? VendorId = null, ushort? ProductId = null,
                                    string? DeviceNameContains = null, string? ZoneNameContains = null, bool OverridesNormalizedContract = false,
                                    string? Maker = null, bool RequiresExternalHeader = false, DetectionConfidence Confidence = DetectionConfidence.High, string Origin = "regra")
{
    public bool Matches(LightDeviceMetadata m)
    {
        if (Backend == null && VendorId == null && ProductId == null && DeviceNameContains == null && ZoneNameContains == null && Maker == null) return false;
        if (Backend != null && !Backend.Equals(m.Backend, StringComparison.OrdinalIgnoreCase)) return false;
        if (VendorId != null && m.VidPid?.Vid != VendorId) return false;
        if (ProductId != null && m.VidPid?.Pid != ProductId) return false;
        if (DeviceNameContains != null && !m.DeviceName.Contains(DeviceNameContains, StringComparison.OrdinalIgnoreCase)) return false;
        if (ZoneNameContains != null && !m.ZoneName.Contains(ZoneNameContains, StringComparison.OrdinalIgnoreCase)) return false;
        if (RequiresExternalHeader && !ColorOrderAuto.IsExternalHeader(m)) return false;
        if (Maker != null && !Maker.Equals(ColorOrderAuto.DetectMaker(m.Backend, m.DeviceName + " " + m.DeviceVendor, m.Location), StringComparison.Ordinal)) return false;
        return true;
    }
}

/// <summary>Reordena os canais. Um delegate estático por ordem: nada é criado por aparelho, por quadro ou por LED.</summary>
public static class LightChannelMapper
{
    private static readonly Func<(byte R, byte G, byte B), (byte, byte, byte)>
        MapRgb = static c => c,
        MapRbg = static c => (c.R, c.B, c.G),
        MapGrb = static c => (c.G, c.R, c.B),
        MapGbr = static c => (c.G, c.B, c.R),
        MapBrg = static c => (c.B, c.R, c.G),
        MapBgr = static c => (c.B, c.G, c.R);

    public static Func<(byte R, byte G, byte B), (byte, byte, byte)> For(LightChannelOrder order) => order switch
    {
        LightChannelOrder.RBG => MapRbg,
        LightChannelOrder.GRB => MapGrb,
        LightChannelOrder.GBR => MapGbr,
        LightChannelOrder.BRG => MapBrg,
        LightChannelOrder.BGR => MapBgr,
        _ => MapRgb,
    };
}
