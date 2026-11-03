using System.Text.RegularExpressions;

namespace ThgOptimization;

/// <summary>
/// Identifica a ordem dos canais de cada aparelho pelo contrato da ponte, pelos metadados e por regras comprovadas. O OpenRGB e os SDKs
/// recebem RGB e convertem na ponta do driver, então adivinhar GRB pelo fabricante trocaria os canais uma segunda vez.
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

    /// <summary>Ordem padrão de um aparelho quando só se conhece o texto dele. As cinco pontes do app recebem RGB normalizado.</summary>
    public static string Resolve(string backend, string name, string detail) => Rgb;

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
    /// Ordem de um aparelho, nesta precedência: escolha manual; regra com evidência; contrato da ponte; RGB como reserva segura.
    /// Roda ao conectar ou ao mudar a configuração, nunca por quadro.
    /// </summary>
    /// <param name="rules">Regras comprovadas. Por padrão nenhuma: fabricante da placa-mãe não diz o protocolo elétrico do conector.</param>
    /// <param name="manual">Valor salvo em LightColorOrder para este aparelho, se houver. Texto inválido é ignorado.</param>
    public static LightOrderResolution ResolveOrder(LightDeviceMetadata m, IReadOnlyList<LightOrderRule>? rules = null, string? manual = null)
    {
        if (TryParseOrder(manual, out var chosen))
            return new(chosen, DetectionConfidence.High, "Escolhida à mão no modo Avançado.", m.Backend);

        if (rules != null)
            foreach (var rule in rules)
            {
                if (!rule.Matches(m)) continue;
                // Permutar por cima de uma ponte que já normaliza é a troca dupla: a regra precisa dizer que o contrato não vale ali.
                if (m.Contract == LightBackendContract.NormalizedRgb && !rule.OverridesNormalizedContract) continue;
                return new(rule.Order, DetectionConfidence.High, rule.Evidence, m.Backend);
            }

        return m.Contract switch
        {
            LightBackendContract.NormalizedRgb when IsExternalHeader(m) => new(LightChannelOrder.RGB, DetectionConfidence.Medium,
                $"{m.Backend} recebe RGB e converte no driver, mas a fita ligada neste conector é de terceiros e pode ter outra ordem física.", m.Backend),
            LightBackendContract.NormalizedRgb => new(LightChannelOrder.RGB, DetectionConfidence.High,
                $"{m.Backend} recebe RGB e converte na ponta do driver: nenhuma permutação é aplicada aqui.", m.Backend),
            LightBackendContract.RawChannels => new(LightChannelOrder.RGB, DetectionConfidence.Low,
                $"{m.Backend} grava os canais direto e não há regra comprovada para este aparelho. Confira as cores no modo Avançado.", m.Backend),
            _ => new(LightChannelOrder.RGB, DetectionConfidence.Unknown,
                "Sem informação suficiente sobre esta ponte: RGB é só a reserva segura.", m.Backend),
        };
    }

    /// <summary>Conector onde o usuário liga a fita que quiser. Aparelho integrado (teclado, RAM, mouse) tem a ordem resolvida pelo driver.</summary>
    private static bool IsExternalHeader(LightDeviceMetadata m)
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

public sealed record LightOrderResolution(LightChannelOrder Order, DetectionConfidence Confidence, string Reason, string Backend);

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
/// Ordem comprovada para um aparelho. Todo critério preenchido precisa bater, e pelo menos um é exigido.
/// Evidence diz de onde veio a informação e aparece para o usuário.
/// </summary>
public sealed record LightOrderRule(LightChannelOrder Order, string Evidence, string? Backend = null, ushort? VendorId = null, ushort? ProductId = null,
                                    string? DeviceNameContains = null, string? ZoneNameContains = null, bool OverridesNormalizedContract = false)
{
    public bool Matches(LightDeviceMetadata m)
    {
        if (Backend == null && VendorId == null && ProductId == null && DeviceNameContains == null && ZoneNameContains == null) return false;
        if (Backend != null && !Backend.Equals(m.Backend, StringComparison.OrdinalIgnoreCase)) return false;
        if (VendorId != null && m.VidPid?.Vid != VendorId) return false;
        if (ProductId != null && m.VidPid?.Pid != ProductId) return false;
        if (DeviceNameContains != null && !m.DeviceName.Contains(DeviceNameContains, StringComparison.OrdinalIgnoreCase)) return false;
        if (ZoneNameContains != null && !m.ZoneName.Contains(ZoneNameContains, StringComparison.OrdinalIgnoreCase)) return false;
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
