namespace ThgOptimization;

/// <summary>Especificações de referência que o hardware não reporta (como o banco de dados do GPU-Z).</summary>
public record GpuSpec(
    string Chip, string Arch, string Process, int DieMm2, double TransistorsBi,
    int Shaders, int Rops, int Tmus, string MemType, int BaseMHz, int BoostMHz, int TdpW,
    string Release, string? NameMatch = null);

public static class GpuSpecDb
{
    /// <summary>Chave: "VVVV:DDDD" (vendor:device PCI). Mesma ID com variantes → NameMatch desempata.</summary>
    private static readonly Dictionary<string, GpuSpec[]> Db = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---- NVIDIA Blackwell (RTX 50) ----
        ["10DE:2B85"] = new[] { new GpuSpec("GB202", "Blackwell", "TSMC 4N (5 nm)", 750, 92.2, 21760, 176, 680, "GDDR7", 2017, 2407, 575, "30/01/2025") },
        ["10DE:2C02"] = new[] { new GpuSpec("GB203", "Blackwell", "TSMC 4N (5 nm)", 378, 45.6, 10752, 112, 336, "GDDR7", 2295, 2617, 360, "30/01/2025") },
        ["10DE:2C05"] = new[] { new GpuSpec("GB203", "Blackwell", "TSMC 4N (5 nm)", 378, 45.6, 8960, 96, 280, "GDDR7", 2295, 2452, 300, "20/02/2025") },
        ["10DE:2F04"] = new[] { new GpuSpec("GB205", "Blackwell", "TSMC 4N (5 nm)", 263, 31.1, 6144, 80, 192, "GDDR7", 2325, 2512, 250, "05/03/2025") },
        ["10DE:2D04"] = new[] { new GpuSpec("GB206", "Blackwell", "TSMC 4N (5 nm)", 181, 21.9, 4608, 48, 144, "GDDR7", 2407, 2572, 180, "16/04/2025") },
        ["10DE:2D05"] = new[] { new GpuSpec("GB206", "Blackwell", "TSMC 4N (5 nm)", 181, 21.9, 3840, 48, 120, "GDDR7", 2280, 2497, 145, "19/05/2025") },

        // ---- NVIDIA Ada Lovelace (RTX 40) ----
        ["10DE:2684"] = new[] { new GpuSpec("AD102", "Ada Lovelace", "TSMC 4N (5 nm)", 609, 76.3, 16384, 176, 512, "GDDR6X", 2235, 2520, 450, "12/10/2022") },
        ["10DE:2702"] = new[] { new GpuSpec("AD103", "Ada Lovelace", "TSMC 4N (5 nm)", 379, 45.9, 10240, 112, 320, "GDDR6X", 2295, 2550, 320, "31/01/2024") },
        ["10DE:2704"] = new[] { new GpuSpec("AD103", "Ada Lovelace", "TSMC 4N (5 nm)", 379, 45.9, 9728, 112, 304, "GDDR6X", 2205, 2505, 320, "16/11/2022") },
        ["10DE:2705"] = new[] { new GpuSpec("AD103", "Ada Lovelace", "TSMC 4N (5 nm)", 379, 45.9, 8448, 96, 264, "GDDR6X", 2340, 2610, 285, "24/01/2024") },
        ["10DE:2782"] = new[] { new GpuSpec("AD104", "Ada Lovelace", "TSMC 4N (5 nm)", 295, 35.8, 7680, 80, 240, "GDDR6X", 2310, 2610, 285, "05/01/2023") },
        ["10DE:2783"] = new[] { new GpuSpec("AD104", "Ada Lovelace", "TSMC 4N (5 nm)", 295, 35.8, 7168, 80, 224, "GDDR6X", 1980, 2475, 220, "17/01/2024") },
        ["10DE:2786"] = new[] { new GpuSpec("AD104", "Ada Lovelace", "TSMC 4N (5 nm)", 295, 35.8, 5888, 64, 184, "GDDR6X", 1920, 2475, 200, "13/04/2023") },
        ["10DE:2803"] = new[] { new GpuSpec("AD106", "Ada Lovelace", "TSMC 4N (5 nm)", 190, 22.9, 4352, 48, 136, "GDDR6", 2310, 2535, 160, "24/05/2023") },
        ["10DE:2805"] = new[] { new GpuSpec("AD106", "Ada Lovelace", "TSMC 4N (5 nm)", 190, 22.9, 4352, 48, 136, "GDDR6", 2310, 2535, 165, "18/07/2023") },
        ["10DE:2882"] = new[] { new GpuSpec("AD107", "Ada Lovelace", "TSMC 4N (5 nm)", 159, 18.9, 3072, 48, 96, "GDDR6", 1830, 2460, 115, "29/06/2023") },

        // ---- NVIDIA Ampere (RTX 30) ----
        ["10DE:2204"] = new[] { new GpuSpec("GA102", "Ampere", "Samsung 8 nm", 628, 28.3, 10496, 112, 328, "GDDR6X", 1395, 1695, 350, "24/09/2020") },
        ["10DE:2206"] = new[] { new GpuSpec("GA102", "Ampere", "Samsung 8 nm", 628, 28.3, 8704, 96, 272, "GDDR6X", 1440, 1710, 320, "17/09/2020") },
        ["10DE:2484"] = new[] { new GpuSpec("GA104", "Ampere", "Samsung 8 nm", 392, 17.4, 5888, 96, 184, "GDDR6", 1500, 1725, 220, "29/10/2020") },
        ["10DE:2486"] = new[] { new GpuSpec("GA104", "Ampere", "Samsung 8 nm", 392, 17.4, 4864, 80, 152, "GDDR6", 1410, 1665, 200, "02/12/2020") },
        ["10DE:2503"] = new[] { new GpuSpec("GA106", "Ampere", "Samsung 8 nm", 276, 12.0, 3584, 48, 112, "GDDR6", 1320, 1777, 170, "25/02/2021") },
        ["10DE:2504"] = new[] { new GpuSpec("GA106", "Ampere", "Samsung 8 nm", 276, 12.0, 3584, 48, 112, "GDDR6", 1320, 1777, 170, "25/02/2021") },

        // ---- AMD RDNA 4 / 3 / 2 ----
        ["1002:7550"] = new[]
        {
            new GpuSpec("Navi 48", "RDNA 4", "TSMC N4P (4 nm)", 357, 53.9, 4096, 128, 256, "GDDR6", 2400, 2970, 304, "06/03/2025", "9070 XT"),
            new GpuSpec("Navi 48", "RDNA 4", "TSMC N4P (4 nm)", 357, 53.9, 3584, 128, 224, "GDDR6", 2070, 2520, 220, "06/03/2025", "9070"),
        },
        ["1002:744C"] = new[]
        {
            new GpuSpec("Navi 31", "RDNA 3", "TSMC 5 nm + 6 nm", 529, 57.7, 6144, 192, 384, "GDDR6", 1855, 2499, 355, "13/12/2022", "XTX"),
            new GpuSpec("Navi 31", "RDNA 3", "TSMC 5 nm + 6 nm", 529, 57.7, 5376, 192, 336, "GDDR6", 1500, 2394, 315, "13/12/2022", "7900 XT"),
        },
        ["1002:747E"] = new[]
        {
            new GpuSpec("Navi 32", "RDNA 3", "TSMC 5 nm + 6 nm", 346, 28.1, 3840, 96, 240, "GDDR6", 1295, 2430, 263, "06/09/2023", "7800"),
            new GpuSpec("Navi 32", "RDNA 3", "TSMC 5 nm + 6 nm", 346, 28.1, 3456, 96, 216, "GDDR6", 1435, 2544, 245, "06/09/2023", "7700"),
        },
        ["1002:7480"] = new[] { new GpuSpec("Navi 33", "RDNA 3", "TSMC 6 nm", 204, 13.3, 2048, 64, 128, "GDDR6", 1720, 2655, 165, "25/05/2023") },
        ["1002:164E"] = new[] { new GpuSpec("Raphael (iGPU)", "RDNA 2", "TSMC 6 nm", 122, 3.4, 128, 4, 8, "Memória do sistema (DDR5)", 400, 2200, 0, "27/09/2022") },
    };

    /// <summary>Recursos por arquitetura, para a linha "Tecnologias".</summary>
    public static readonly Dictionary<string, string> ArchFeatures = new()
    {
        ["Blackwell"] = "DLSS 4 (Multi Frame Generation) · Ray Tracing 4ª ger. · Tensor 5ª ger. · Reflex 2 · NVENC 9ª ger.",
        ["Ada Lovelace"] = "DLSS 3 (Frame Generation) · Ray Tracing 3ª ger. · Tensor 4ª ger. · Reflex · NVENC AV1",
        ["Ampere"] = "DLSS 2 · Ray Tracing 2ª ger. · Tensor 3ª ger. · Reflex · Resizable BAR",
        ["RDNA 4"] = "FSR 4 · Ray Accelerators 3ª ger. · AI Accelerators · Anti-Lag 2 · AV1",
        ["RDNA 3"] = "FSR 3 · Ray Accelerators 2ª ger. · AI Accelerators · AV1",
        ["RDNA 2"] = "FSR · Ray Accelerators 1ª ger.",
    };

    public static GpuSpec? Find(string vendorId, string deviceId, string name)
    {
        if (!Db.TryGetValue($"{vendorId}:{deviceId}", out var specs)) return null;
        return specs.FirstOrDefault(s => s.NameMatch != null && name.Contains(s.NameMatch, StringComparison.OrdinalIgnoreCase))
               ?? specs[0];
    }

    /// <summary>
    /// Placa pelo ID PCI, para quando ela está sem driver (o Windows a chama de "Adaptador de Vídeo Básico da Microsoft",
    /// por exemplo logo depois da DDU). Display = como mostrar; Lookup = modelo usado para achar o driver no site do
    /// fabricante. Um mesmo chip cobre vários modelos (ex.: Navi 10 = RX 5600 XT, 5700, 5700 XT) e o driver é o mesmo para
    /// todos da série, então quando a revisão não desempata o nome mostra a família e o Lookup usa um modelo dela.
    /// </summary>
    public static (string Display, string Lookup)? ByPciId(string vendorId, string deviceId, string rev = "")
    {
        string key = $"{vendorId}:{deviceId}".ToUpperInvariant();
        rev = rev.ToUpperInvariant();
        if (key == "1002:7550")
            return rev == "C0" ? ("AMD Radeon RX 9070 XT", "AMD Radeon RX 9070 XT") : ("AMD Radeon RX 9070 / 9070 GRE", "AMD Radeon RX 9070");
        return PciNames.TryGetValue(key, out var n) ? n : null;
    }

    private static readonly Dictionary<string, (string Display, string Lookup)> PciNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---- AMD RDNA 4
        ["1002:7590"] = ("AMD Radeon RX 9060 XT", "AMD Radeon RX 9060 XT"),
        // ---- AMD RDNA 3
        ["1002:744C"] = ("AMD Radeon RX 7900 XTX / XT / GRE", "AMD Radeon RX 7900 XTX"),
        ["1002:747E"] = ("AMD Radeon RX 7800 XT / 7700 XT", "AMD Radeon RX 7800 XT"),
        ["1002:7480"] = ("AMD Radeon RX 7600 / 7600 XT", "AMD Radeon RX 7600"),
        ["1002:7483"] = ("AMD Radeon RX 7600M", "AMD Radeon RX 7600"),
        // ---- AMD RDNA 2
        ["1002:73A5"] = ("AMD Radeon RX 6950 XT", "AMD Radeon RX 6950 XT"),
        ["1002:73AF"] = ("AMD Radeon RX 6900 XT", "AMD Radeon RX 6900 XT"),
        ["1002:73BF"] = ("AMD Radeon RX 6800 / 6800 XT / 6900 XT", "AMD Radeon RX 6800 XT"),
        ["1002:73DF"] = ("AMD Radeon RX 6700 XT / 6750 XT / 6700", "AMD Radeon RX 6700 XT"),
        ["1002:73EF"] = ("AMD Radeon RX 6650 XT / 6600 XT", "AMD Radeon RX 6650 XT"),
        ["1002:73FF"] = ("AMD Radeon RX 6600 / 6600 XT", "AMD Radeon RX 6600"),
        ["1002:743F"] = ("AMD Radeon RX 6500 XT / 6400", "AMD Radeon RX 6500 XT"),
        // ---- AMD RDNA 1
        ["1002:731F"] = ("AMD Radeon RX 5700 XT / 5700 / 5600 XT", "AMD Radeon RX 5700 XT"),
        ["1002:7340"] = ("AMD Radeon RX 5500 XT / 5500", "AMD Radeon RX 5500 XT"),
        // ---- AMD Polaris / Vega (ainda comuns)
        ["1002:67DF"] = ("AMD Radeon RX 580 / 570 / 480 / 470", "AMD Radeon RX 580"),
        ["1002:6FDF"] = ("AMD Radeon RX 580 2048SP", "AMD Radeon RX 580"),
        ["1002:699F"] = ("AMD Radeon RX 550 / 560", "AMD Radeon RX 550"),
        ["1002:687F"] = ("AMD Radeon RX Vega 56 / 64", "AMD Radeon RX Vega 64"),
        // ---- AMD vídeo integrado (Ryzen)
        ["1002:164E"] = ("AMD Radeon Graphics (vídeo integrado Ryzen 7000/9000)", "AMD Radeon Graphics"),
        ["1002:13C0"] = ("AMD Radeon Graphics (vídeo integrado Ryzen 9000)", "AMD Radeon Graphics"),
        ["1002:1638"] = ("AMD Radeon Graphics (vídeo integrado Ryzen 5000G)", "AMD Radeon Graphics"),
        ["1002:15D8"] = ("AMD Radeon Vega (vídeo integrado Ryzen 3000G/4000)", "AMD Radeon Graphics"),
        // ---- NVIDIA Blackwell (RTX 50)
        ["10DE:2B85"] = ("NVIDIA GeForce RTX 5090", "GeForce RTX 5090"), ["10DE:2C02"] = ("NVIDIA GeForce RTX 5080", "GeForce RTX 5080"),
        ["10DE:2C05"] = ("NVIDIA GeForce RTX 5070 Ti", "GeForce RTX 5070 Ti"), ["10DE:2F04"] = ("NVIDIA GeForce RTX 5070", "GeForce RTX 5070"),
        ["10DE:2D04"] = ("NVIDIA GeForce RTX 5060 Ti", "GeForce RTX 5060 Ti"), ["10DE:2D05"] = ("NVIDIA GeForce RTX 5060", "GeForce RTX 5060"),
        // ---- NVIDIA Ada (RTX 40)
        ["10DE:2684"] = ("NVIDIA GeForce RTX 4090", "GeForce RTX 4090"), ["10DE:2702"] = ("NVIDIA GeForce RTX 4080 SUPER", "GeForce RTX 4080 SUPER"),
        ["10DE:2704"] = ("NVIDIA GeForce RTX 4080", "GeForce RTX 4080"), ["10DE:2705"] = ("NVIDIA GeForce RTX 4070 Ti SUPER", "GeForce RTX 4070 Ti SUPER"),
        ["10DE:2782"] = ("NVIDIA GeForce RTX 4070 Ti", "GeForce RTX 4070 Ti"), ["10DE:2783"] = ("NVIDIA GeForce RTX 4070 SUPER", "GeForce RTX 4070 SUPER"),
        ["10DE:2786"] = ("NVIDIA GeForce RTX 4070", "GeForce RTX 4070"), ["10DE:2709"] = ("NVIDIA GeForce RTX 4070", "GeForce RTX 4070"),
        ["10DE:2803"] = ("NVIDIA GeForce RTX 4060 Ti", "GeForce RTX 4060 Ti"), ["10DE:2805"] = ("NVIDIA GeForce RTX 4060 Ti", "GeForce RTX 4060 Ti"),
        ["10DE:2882"] = ("NVIDIA GeForce RTX 4060", "GeForce RTX 4060"),
        // ---- NVIDIA Ampere (RTX 30)
        ["10DE:2204"] = ("NVIDIA GeForce RTX 3090", "GeForce RTX 3090"), ["10DE:2203"] = ("NVIDIA GeForce RTX 3090 Ti", "GeForce RTX 3090 Ti"),
        ["10DE:2208"] = ("NVIDIA GeForce RTX 3080 Ti", "GeForce RTX 3080 Ti"), ["10DE:2206"] = ("NVIDIA GeForce RTX 3080", "GeForce RTX 3080"),
        ["10DE:2216"] = ("NVIDIA GeForce RTX 3080", "GeForce RTX 3080"), ["10DE:2482"] = ("NVIDIA GeForce RTX 3070 Ti", "GeForce RTX 3070 Ti"),
        ["10DE:2484"] = ("NVIDIA GeForce RTX 3070", "GeForce RTX 3070"), ["10DE:2488"] = ("NVIDIA GeForce RTX 3070", "GeForce RTX 3070"),
        ["10DE:2486"] = ("NVIDIA GeForce RTX 3060 Ti", "GeForce RTX 3060 Ti"), ["10DE:2489"] = ("NVIDIA GeForce RTX 3060 Ti", "GeForce RTX 3060 Ti"),
        ["10DE:2503"] = ("NVIDIA GeForce RTX 3060", "GeForce RTX 3060"), ["10DE:2504"] = ("NVIDIA GeForce RTX 3060", "GeForce RTX 3060"),
        ["10DE:2487"] = ("NVIDIA GeForce RTX 3060", "GeForce RTX 3060"), ["10DE:2507"] = ("NVIDIA GeForce RTX 3050", "GeForce RTX 3050"),
        ["10DE:2582"] = ("NVIDIA GeForce RTX 3050", "GeForce RTX 3050"),
        // ---- NVIDIA Turing (RTX 20 / GTX 16)
        ["10DE:1E04"] = ("NVIDIA GeForce RTX 2080 Ti", "GeForce RTX 2080 Ti"), ["10DE:1E07"] = ("NVIDIA GeForce RTX 2080 Ti", "GeForce RTX 2080 Ti"),
        ["10DE:1E81"] = ("NVIDIA GeForce RTX 2080 SUPER", "GeForce RTX 2080 SUPER"), ["10DE:1E82"] = ("NVIDIA GeForce RTX 2080", "GeForce RTX 2080"),
        ["10DE:1E87"] = ("NVIDIA GeForce RTX 2080", "GeForce RTX 2080"), ["10DE:1E84"] = ("NVIDIA GeForce RTX 2070 SUPER", "GeForce RTX 2070 SUPER"),
        ["10DE:1F02"] = ("NVIDIA GeForce RTX 2070", "GeForce RTX 2070"), ["10DE:1F07"] = ("NVIDIA GeForce RTX 2070", "GeForce RTX 2070"),
        ["10DE:1F06"] = ("NVIDIA GeForce RTX 2060 SUPER", "GeForce RTX 2060 SUPER"), ["10DE:1F08"] = ("NVIDIA GeForce RTX 2060", "GeForce RTX 2060"),
        ["10DE:1F03"] = ("NVIDIA GeForce RTX 2060", "GeForce RTX 2060"), ["10DE:2182"] = ("NVIDIA GeForce GTX 1660 Ti", "GeForce GTX 1660 Ti"),
        ["10DE:21C4"] = ("NVIDIA GeForce GTX 1660 SUPER", "GeForce GTX 1660 SUPER"), ["10DE:2184"] = ("NVIDIA GeForce GTX 1660", "GeForce GTX 1660"),
        ["10DE:2187"] = ("NVIDIA GeForce GTX 1650 SUPER", "GeForce GTX 1650 SUPER"), ["10DE:1F82"] = ("NVIDIA GeForce GTX 1650", "GeForce GTX 1650"),
        ["10DE:1F91"] = ("NVIDIA GeForce GTX 1650", "GeForce GTX 1650"), ["10DE:2188"] = ("NVIDIA GeForce GTX 1650", "GeForce GTX 1650"),
        // ---- NVIDIA Pascal (GTX 10)
        ["10DE:1B06"] = ("NVIDIA GeForce GTX 1080 Ti", "GeForce GTX 1080 Ti"), ["10DE:1B80"] = ("NVIDIA GeForce GTX 1080", "GeForce GTX 1080"),
        ["10DE:1B81"] = ("NVIDIA GeForce GTX 1070", "GeForce GTX 1070"), ["10DE:1B82"] = ("NVIDIA GeForce GTX 1070 Ti", "GeForce GTX 1070 Ti"),
        ["10DE:1C03"] = ("NVIDIA GeForce GTX 1060 6GB", "GeForce GTX 1060"), ["10DE:1C02"] = ("NVIDIA GeForce GTX 1060 3GB", "GeForce GTX 1060"),
        ["10DE:1C82"] = ("NVIDIA GeForce GTX 1050 Ti", "GeForce GTX 1050 Ti"), ["10DE:1C81"] = ("NVIDIA GeForce GTX 1050", "GeForce GTX 1050"),
    };

    /// <summary>Compatibilidade: só o nome para mostrar.</summary>
    public static string? MarketingName(string vendorId, string deviceId, string rev = "") => ByPciId(vendorId, deviceId, rev)?.Display;

    public static string SubvendorName(string id) => id.ToUpperInvariant() switch
    {
        "1043" => "ASUS", "1458" => "Gigabyte", "1462" => "MSI", "3842" => "EVGA", "19DA" => "Zotac",
        "196E" => "PNY", "10DE" => "NVIDIA", "1002" => "AMD", "1DA2" => "Sapphire", "1682" => "XFX",
        "148C" => "PowerColor", "1849" => "ASRock", "7377" => "Colorful", "1569" => "Palit", "10B0" => "Gainward",
        "1B4C" => "Galax / KFA2", "8086" => "Intel", "1028" => "Dell", "103C" => "HP", "17AA" => "Lenovo",
        "1025" => "Acer", "144D" => "Samsung", "1D05" => "Tongfang",
        _ => $"Desconhecido ({id})"
    };
}
