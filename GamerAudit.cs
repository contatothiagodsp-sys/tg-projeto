using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ThgOptimization;

public enum CheckLevel { Ok, Info, Warn, Bad }

/// <summary>Como resolver um item: o app corrige sozinho, abre uma tela do Windows, uma página do app ou mostra o guia da BIOS.</summary>
public enum FixKind { None, Auto, Settings, Page, Bios }

/// <summary>Um item do diagnóstico do TURBO.</summary>
public sealed class GamerCheck : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Area { get; init; } = "";      // HARDWARE · BIOS · WINDOWS · REDE
    public string Title { get; init; } = "";
    private string _found = "";
    /// <summary>O que foi encontrado neste PC.</summary>
    public string Found { get => _found; set { _found = value; Changed(nameof(Found)); } }
    public string Advice { get; set; } = "";     // por que importa / o que fazer
    /// <summary>Impacto em jogos (0 = só informativo, 10 = muito grande).</summary>
    public int Weight { get; init; } = 3;
    public FixKind Fix { get; set; }
    public string FixLabel { get; set; } = "";
    /// <summary>URI de configurações (ms-settings:…), Tag da página do app ou texto do guia da BIOS.</summary>
    public string? FixTarget { get; set; }
    /// <summary>Correção automática: devolve "" quando deu certo, ou o motivo da falha.</summary>
    internal Func<AppSettings, string>? AutoFix { get; set; }
    /// <summary>Por que o TURBO não aplica este item sozinho (notebook, CPU X3D…). Vazio = aplica.</summary>
    public string TurboSkip { get; set; } = "";
    public bool NeedsRestart { get; init; }

    private CheckLevel _level;
    public CheckLevel Level
    {
        get => _level;
        set { _level = value; Changed(nameof(Level)); Changed(nameof(LevelText)); Changed(nameof(Glyph)); Changed(nameof(ShowFix)); Changed(nameof(IsProblem)); }
    }
    public string LevelText => Level switch { CheckLevel.Ok => "OK", CheckLevel.Info => "INFO", CheckLevel.Warn => "ATENÇÃO", _ => "CRÍTICO" };
    public string Glyph => Level switch { CheckLevel.Ok => "\uE73E", CheckLevel.Info => "\uE946", CheckLevel.Warn => "\uE7BA", _ => "\uEA39" };
    public bool IsProblem => Level is CheckLevel.Warn or CheckLevel.Bad;
    public bool ShowFix => Fix != FixKind.None && (IsProblem || Level == CheckLevel.Info || Fix is FixKind.Settings or FixKind.Page);
    public bool InTurbo => Fix == FixKind.Auto && IsProblem && AutoFix != null && TurboSkip.Length == 0;

    private string _status = "";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }

    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record AuditScore(int Total, string Verdict, IReadOnlyDictionary<string, int> ByArea, int Problems, int AutoFixable);

/// <summary>
/// TURBO: diagnóstico de PC gamer. Olha hardware (RAM no perfil XMP/EXPO, dual channel, monitor na taxa máxima,
/// cabo na placa de vídeo, PCIe, Resizable BAR, SSD), Windows (ajustes do OTIMIZA, arquivo de paginação, VBS) e rede,
/// dá uma nota e diz como corrigir cada item: sozinho, por uma tela do Windows ou por um guia da BIOS da sua placa-mãe.
/// </summary>
public static class GamerAudit
{
    public sealed class Context
    {
        public List<GpuInfo> Gpus { get; init; } = new();
        public List<Tweak> Tweaks { get; init; } = new();
        public string CpuName { get; init; } = "";
        /// <summary>Perfis pro aplicados no driver NVIDIA pelo Modo Jogo (null = não é NVIDIA).</summary>
        public bool? NvProfilesApplied { get; init; }
        public bool AutoGameMode { get; init; }
        /// <summary>Último teste de engasgos (null = nunca testado).</summary>
        public LatencyResult? LastLatency { get; init; }
    }

    public static List<GamerCheck> Run(Context ctx)
    {
        var list = new List<GamerCheck>();
        var power = Power();
        bool laptop = power.HasBattery;
        static T Try<T>(Func<T> f, T fallback) { try { return f(); } catch { return fallback; } }
        // O nome da CPU decide ajustes (Ryzen X3D); se a tela ainda não o leu, busca aqui.
        string cpu = ctx.CpuName.Length > 0 ? ctx.CpuName : Try(() =>
        {
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (ManagementObject o in s.Get()) return o["Name"]?.ToString()?.Trim() ?? "";
            return "";
        }, "");

        void Add(string id, string area, string title, Func<GamerCheck?> build)
        {
            try { if (build() is { } c) list.Add(c); }
            catch (Exception ex)
            {
                list.Add(new GamerCheck { Id = id, Area = area, Title = title, Weight = 0, Level = CheckLevel.Info,
                                          Found = "Não foi possível verificar", Advice = ex.Message });
            }
        }

        // ---------------- HARDWARE ----------------
        var ram = Try(Memory, new List<Dimm>());
        Add("ram.xmp", "BIOS", "Memória no perfil XMP / EXPO", () => CheckXmp(ram, laptop));
        Add("ram.channel", "HARDWARE", "Memória em dual channel", () => CheckChannels(ram, laptop));
        Add("ram.size", "HARDWARE", "Quantidade de RAM", () => CheckRamSize(ram));

        var displays = Try(DisplayTuner.List, new List<DisplayState>());
        foreach (var d in displays) { var dd = d; Add("display." + dd.Device, "HARDWARE", $"Taxa de atualização · {dd.Label}", () => CheckRefresh(dd)); }
        Add("display.cable", "HARDWARE", "Monitor ligado na placa de vídeo", () => CheckCable(displays, ctx.Gpus, laptop));

        var gpu = ctx.Gpus.FirstOrDefault(g => g.Vendor is "NVIDIA" or "AMD") ?? ctx.Gpus.FirstOrDefault();
        if (gpu != null)
        {
            Add("gpu.pcie", "HARDWARE", "Placa de vídeo na velocidade total do slot", () => CheckPcie(gpu));
            Add("gpu.rebar", "BIOS", "Resizable BAR (Smart Access Memory)", () => CheckRebar(gpu));
            Add("gpu.driver", "WINDOWS", "Driver de vídeo atualizado", () => CheckDriverAge(gpu));
            if (gpu.Vendor == "AMD") Add("gpu.amdsettings", "WINDOWS", "AMD Software (Adrenalin) configurado para jogos", () => AmdRadeonGuide(gpu));
        }
        if (laptop) Add("display.mux", "HARDWARE", "Tela do notebook na placa de vídeo dedicada (MUX)", () => CheckMux(displays, ctx.Gpus));

        // ---------------- PROCESSADOR AMD (chipset e X3D) ----------------
        if (cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
        {
            var amd = Try(AmdDevices, new List<(string Name, string Version, DateTime? Date)>());
            Add("cpu.amdchipset", "WINDOWS", "Driver de chipset AMD", () => CheckAmdChipset(amd, cpu));
            if (WindowsTweaks.IsDualCcdX3D(cpu)) Add("cpu.x3dopt", "WINDOWS", "Otimizador do 3D V-Cache (Ryzen X3D)", () => CheckX3dOptimizer(amd, cpu));
            if (cpu.Contains("X3D", StringComparison.OrdinalIgnoreCase)) Add("cpu.x3dbios", "BIOS", "Ajustes de BIOS para Ryzen X3D", () => new GamerCheck
            {
                Id = "cpu.x3dbios", Area = "BIOS", Title = "Ajustes de BIOS para Ryzen X3D", Weight = 0, Level = CheckLevel.Info,
                Found = cpu,
                Advice = "Processadores X3D rendem mais com memória rápida e o limite de temperatura sob controle. O guia mostra os ajustes " +
                         "que a comunidade usa (EXPO, FCLK, Curve Optimizer) no menu da sua placa-mãe.",
                Fix = FixKind.Bios, FixLabel = "GUIA DA BIOS", FixTarget = BiosGuide("x3d:" + cpu),
            });
        }
        Add("lat.dpc", "WINDOWS", "Latência dos drivers (engasgos)", () => CheckLatency(ctx.LastLatency));
        Add("disk.system", "HARDWARE", "Windows instalado em SSD", CheckSystemDisk);
        Add("disk.free", "HARDWARE", "Espaço livre no disco do Windows", CheckFreeSpace);
        if (laptop) Add("power.ac", "HARDWARE", "Notebook na tomada", () => CheckAc(power));

        // ---------------- WINDOWS ----------------
        foreach (var t in ctx.Tweaks) { var tt = t; Add("tweak." + tt.Id, "WINDOWS", tt.Title, () => CheckTweak(tt, cpu, laptop)); }
        if (ctx.NvProfilesApplied is bool nv)
            Add("gpu.profiles", "WINDOWS", "Driver NVIDIA configurado para jogos", () => new GamerCheck
            {
                Id = "gpu.profiles", Area = "WINDOWS", Title = "Driver NVIDIA configurado para jogos", Weight = 6,
                Found = nv ? "Perfis pro do tHg aplicados" : "Configuração de fábrica do driver",
                Level = nv ? CheckLevel.Ok : CheckLevel.Warn,
                Advice = nv ? "Baixa latência, desempenho máximo e V-Sync/G-SYNC ajustados por jogo."
                            : "O driver vem configurado para economizar energia. Os perfis pro ligam a baixa latência, o desempenho máximo e a " +
                              "receita certa de V-Sync/G-SYNC em cada jogo competitivo ou de alta fidelidade instalado.",
                Fix = FixKind.Page, FixLabel = "CONFIGURAR", FixTarget = "PageGameMode",
            });
        Add("win.gamemode", "WINDOWS", "Modo Jogo automático do tHg", () => new GamerCheck
        {
            Id = "win.gamemode", Area = "WINDOWS", Title = "Modo Jogo automático do tHg", Weight = 3,
            Found = ctx.AutoGameMode ? "Ligado" : "Desligado",
            Level = ctx.AutoGameMode ? CheckLevel.Ok : CheckLevel.Warn,
            Advice = ctx.AutoGameMode ? "O PC se prepara sozinho quando um jogo abre e volta ao normal quando fecha."
                                      : "Prepara o Windows sozinho quando um jogo abre (prioridade, memória, plano de energia) e mede o FPS de cada sessão.",
            Fix = FixKind.Page, FixLabel = "CONFIGURAR", FixTarget = "PageGameMode",
        });
        Add("win.pagefile", "WINDOWS", "Arquivo de paginação ativo", CheckPagefile);
        Add("win.startup", "WINDOWS", "Programas abrindo com o Windows", CheckStartup);
        Add("win.vbs", "WINDOWS", "Isolamento de núcleo (Integridade de Memória)", CheckVbs);
        Add("win.version", "WINDOWS", "Versão do Windows", CheckWindowsVersion);

        // ---------------- REDE ----------------
        Add("net.link", "REDE", "Conexão de internet para jogar", CheckNetwork);

        return list.OrderBy(c => c.Level switch { CheckLevel.Bad => 0, CheckLevel.Warn => 1, CheckLevel.Info => 3, _ => 2 })
                   .ThenByDescending(c => c.Weight).ToList();
    }

    public static AuditScore Score(IEnumerable<GamerCheck> checks, bool security = false)
    {
        static double Penalty(GamerCheck c) => c.Level switch { CheckLevel.Bad => 1, CheckLevel.Warn => 0.5, _ => 0 };
        static int Of(IEnumerable<GamerCheck> cs)
        {
            var scored = cs.Where(c => c.Weight > 0 && c.Level != CheckLevel.Info).ToList();
            double total = scored.Sum(c => c.Weight);
            return total == 0 ? 100 : (int)Math.Round(100 * (1 - scored.Sum(c => c.Weight * Penalty(c)) / total));
        }
        var all = checks.ToList();
        int score = Of(all);
        string verdict = security
            ? (score >= 90 ? "BLINDADO" : score >= 75 ? "BEM PROTEGIDO, COM PONTOS ABERTOS" : score >= 50 ? "VULNERÁVEL EM VÁRIOS PONTOS" : "EM RISCO")
            : (score >= 90 ? "PRONTO PARA JOGAR" : score >= 75 ? "BOM, COM AJUSTES PENDENTES" : score >= 50 ? "DÁ PARA MELHORAR BASTANTE" : "PERDENDO DESEMPENHO");
        var areas = all.GroupBy(c => c.Area).ToDictionary(g => g.Key, g => Of(g));
        return new AuditScore(score, verdict, areas, all.Count(c => c.IsProblem), all.Count(c => c.InTurbo));
    }

    // ================================================================== memória

    private sealed record Dimm(long Bytes, int Configured, int Speed, int Type, string Part, string Slot);

    private static List<Dimm> Memory()
    {
        var list = new List<Dimm>();
        using var s = new ManagementObjectSearcher(
            "SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType, PartNumber, DeviceLocator, BankLabel FROM Win32_PhysicalMemory");
        foreach (ManagementObject o in s.Get())
        {
            list.Add(new Dimm(
                Convert.ToInt64(o["Capacity"] ?? 0L),
                Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0),
                Convert.ToInt32(o["Speed"] ?? 0),
                Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0),
                (o["PartNumber"]?.ToString() ?? "").Trim(),
                $"{o["BankLabel"]} {o["DeviceLocator"]}".Trim()));
        }
        return list;
    }

    private static string DdrName(int type) => type switch { 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 35 => "LPDDR5", 30 => "LPDDR4", _ => "DDR" };

    /// <summary>
    /// Velocidade da etiqueta lida do código do pente (Corsair CMK16GX4M2B3200C16, G.Skill F4-3600C16D,
    /// Kingston KF432C16BB, Crucial BL8G32C16U4B, XPG AX4U320016G16A, TeamGroup …3200HC16…). 0 = não reconhecido.
    /// </summary>
    internal static int RatedFromPart(string part)
    {
        var patterns = new (string Re, int Mult)[]
        {
            (@"(\d{4})C\d{2}", 1),                 // …3200C16 (Corsair, G.Skill F4/F5, Kingston KF3600C17)
            (@"F[45]-(\d{4})", 1),                 // G.Skill F5-6000J3038…
            (@"(\d{4})HC\d{2}", 1),                // TeamGroup …3200HC16
            (@"A[XD][45]U(\d{4})", 1),             // XPG / ADATA AX4U3200…
            (@"\b(?:KF|HX)[45]?(\d{2})C\d", 100),  // Kingston/HyperX KF432C16, KF556C40, HX432C16
            (@"\b(?:BL|CP)\d+G(\d{2})C", 100),     // Crucial Ballistix BL8G32C16, Crucial Pro CP16G60C36
        };
        int best = 0;
        foreach (var (re, mult) in patterns)
            foreach (Match m in Regex.Matches(part, re, RegexOptions.IgnoreCase))
            {
                int v = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * mult;
                if (v is >= 1600 and <= 10000) best = Math.Max(best, v);
            }
        return best;
    }

    private static GamerCheck? CheckXmp(List<Dimm> ram, bool laptop)
    {
        if (ram.Count == 0) return null;
        int configured = ram.Min(d => d.Configured > 0 ? d.Configured : d.Speed);
        int rated = ram.Select(d => RatedFromPart(d.Part)).DefaultIfEmpty(0).Max();
        // Algumas BIOS informam o clock real (metade): 1600 para DDR4-3200.
        if (rated > 0 && Math.Abs(configured * 2 - rated) <= rated * 0.03) configured *= 2;
        string ddr = DdrName(ram[0].Type);
        var c = new GamerCheck
        {
            Id = "ram.xmp", Area = "BIOS", Title = "Memória no perfil XMP / EXPO", Weight = 9,
            Found = $"{ddr} rodando a {configured} MT/s" + (rated > 0 ? $" · etiqueta do pente: {rated} MT/s" : ""),
        };

        bool jedecDefault = ddr == "DDR4" ? configured <= 2666 : ddr == "DDR5" ? configured <= 4800 : false;
        if (rated > configured + 150)
        {
            c.Level = laptop ? CheckLevel.Info : CheckLevel.Bad;
            c.Advice = $"A memória aguenta {rated} MT/s mas está no modo padrão de segurança ({configured}). Ligar o XMP/EXPO na BIOS " +
                       "costuma dar 10–25% de FPS em jogos que dependem da CPU e melhora muito o 1% low. É o maior ganho de graça de um PC gamer.";
        }
        else if (jedecDefault && !laptop)
        {
            c.Level = CheckLevel.Warn;
            c.Advice = $"{configured} MT/s é a velocidade padrão de fábrica do {ddr}. Se o seu pente for de maior velocidade " +
                       "(veja a etiqueta), ative o XMP/EXPO na BIOS. Se ele for mesmo dessa velocidade, pode ignorar.";
        }
        else
        {
            c.Level = CheckLevel.Ok;
            c.Advice = laptop && jedecDefault ? "Notebooks normalmente não permitem mudar a velocidade da memória." : "A memória está na velocidade de etiqueta.";
        }
        if (c.IsProblem || c.Level == CheckLevel.Info && rated > configured + 150)
        {
            c.Fix = FixKind.Bios; c.FixLabel = "GUIA DA BIOS"; c.FixTarget = BiosGuide("xmp");
        }
        return c;
    }

    private static GamerCheck? CheckChannels(List<Dimm> ram, bool laptop)
    {
        if (ram.Count == 0) return null;
        var c = new GamerCheck { Id = "ram.channel", Area = "HARDWARE", Title = "Memória em dual channel", Weight = 8 };
        string ddr = DdrName(ram[0].Type);
        if (ram.Count == 1)
        {
            c.Found = $"1 pente de {ram[0].Bytes >> 30} GB";
            // DDR5 tem dois subcanais por pente, mas ainda perde bastante banda com um pente só.
            c.Level = laptop ? CheckLevel.Warn : CheckLevel.Bad;
            c.Advice = "Com um pente só a CPU tem metade da largura de banda da memória. Dois pentes iguais (ex.: 2×8 GB em vez de 1×16 GB) " +
                       "dão de 10% a 30% mais FPS em jogos que dependem da CPU e em vídeo integrado." +
                       (laptop ? " Em notebooks, veja se há um segundo slot livre ou memória soldada." : "");
            return c;
        }

        // Canal pelo nome do slot (ChannelA-DIMM0, DIMM_A2, P0 CHANNEL B…). Sem letra em todos, não dá para afirmar nada.
        var letters = ram.Select(d => Regex.Match(d.Slot, @"CHANNEL\s*([A-H])\b|DIMM[_ ]?([A-H])\d|_([A-H])\d\b", RegexOptions.IgnoreCase))
                         .Select(m => m.Success ? (m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value).ToUpperInvariant() : "")
                         .ToList();
        c.Found = $"{ram.Count} pentes: " + string.Join(" + ", ram.Select(d => $"{d.Bytes >> 30} GB"));
        if (letters.All(l => l.Length == 1) && letters.Distinct().Count() == 1)
        {
            c.Level = CheckLevel.Bad;
            c.Found += $" · todos no canal {letters[0]}";
            c.Advice = "Os pentes estão no mesmo canal, então a memória funciona em single channel. Em placas de 4 slots, use os slots " +
                       "A2 e B2 (o 2º e o 4º a partir da CPU), como mostra o manual da placa-mãe.";
            c.Fix = FixKind.Bios; c.FixLabel = "COMO FAZER";
            c.FixTarget = "Desligue o PC e tire da tomada. Mude um dos pentes para o slot do outro canal: em quase todas as placas de 4 slots " +
                          "o par certo é A2 + B2 (2º e 4º slot contando a partir do processador). Confira no manual (\"recommended memory configuration\").";
            return c;
        }
        bool mixed = ram.Select(d => d.Bytes).Distinct().Count() > 1;
        c.Level = mixed ? CheckLevel.Info : CheckLevel.Ok;
        c.Advice = mixed ? "Pentes de tamanhos diferentes: só a parte igual roda em dual channel (modo flex). Funciona, mas um kit igual é melhor."
                         : $"{ddr} em pares: largura de banda completa.";
        return c;
    }

    private static GamerCheck? CheckRamSize(List<Dimm> ram)
    {
        if (ram.Count == 0) return null;
        double gb = ram.Sum(d => d.Bytes) / 1073741824.0;
        return new GamerCheck
        {
            Id = "ram.size", Area = "HARDWARE", Title = "Quantidade de RAM", Weight = 6, Found = $"{gb:0} GB",
            Level = gb < 7.5 ? CheckLevel.Bad : gb < 15.5 ? CheckLevel.Warn : CheckLevel.Ok,
            Advice = gb < 7.5 ? "Abaixo de 8 GB a maioria dos jogos atuais trava ou nem abre. Prioridade número 1 de upgrade."
                   : gb < 15.5 ? "16 GB é o mínimo confortável hoje: com 8 GB o Windows usa o disco como memória e aparecem engasgos."
                   : gb < 31.5 ? "16 GB atende a quase todos os jogos. 32 GB ajuda quem joga com Discord, navegador e live abertos."
                   : "Sobra memória para jogo, Discord, navegador e live ao mesmo tempo.",
        };
    }

    // ================================================================== monitor e vídeo

    private static GamerCheck CheckRefresh(DisplayState d)
    {
        var c = new GamerCheck
        {
            Id = "display." + d.Device, Area = "HARDWARE", Title = $"Taxa de atualização · {d.Label}", Weight = 10,
            Found = d.BelowMax ? $"Rodando a {d.CurrentHz} Hz · o monitor aceita {d.MaxHz} Hz" : $"{d.CurrentHz} Hz (máximo disponível)",
        };
        if (d.BelowMax)
        {
            c.Level = CheckLevel.Bad;
            c.Advice = $"O monitor está mostrando só {d.CurrentHz} quadros por segundo, mesmo que o jogo rode a mais. Em {d.MaxHz} Hz a imagem fica " +
                       "muito mais fluida e a latência cai. É um dos erros mais comuns em PC gamer novo.";
            c.Fix = FixKind.Auto; c.FixLabel = $"MUDAR PARA {d.MaxHz} Hz";
            c.AutoFix = s => DisplayTuner.SetMaxRefresh(d, s);
        }
        else
        {
            c.Level = d.CurrentHz < 100 ? CheckLevel.Info : CheckLevel.Ok;
            c.Advice = d.CurrentHz < 100
                ? "Este monitor (ou o cabo) não oferece mais que isso nesta resolução. Se o monitor for de 144 Hz ou mais, use cabo DisplayPort " +
                  "ou HDMI 2.0+ e confira se o recurso de alta taxa está ligado no menu do próprio monitor."
                : "Já está na taxa máxima.";
            if (d.CurrentHz < 100) { c.Fix = FixKind.Settings; c.FixLabel = "CONFIGURAÇÕES DE VÍDEO"; c.FixTarget = "ms-settings:display-advanced"; }
        }
        return c;
    }

    private static bool IsDiscrete(string name) =>
        (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && !name.Contains("Tegra", StringComparison.OrdinalIgnoreCase))
        || Regex.IsMatch(name, @"Radeon.*\b(RX|R9|Pro W)\b", RegexOptions.IgnoreCase)
        || Regex.IsMatch(name, @"Intel.*\bArc\b.*\b[AB]\d{3}\b", RegexOptions.IgnoreCase);

    private static GamerCheck? CheckCable(List<DisplayState> displays, List<GpuInfo> gpus, bool laptop)
    {
        // Notebook com Optimus/MUX: a tela interna sempre sai do vídeo integrado, é normal.
        if (laptop || displays.Count == 0 || !gpus.Any(g => IsDiscrete(g.Name))) return null;
        var main = displays.FirstOrDefault(d => d.Primary) ?? displays[0];
        bool onDiscrete = IsDiscrete(main.Adapter);
        return new GamerCheck
        {
            Id = "display.cable", Area = "HARDWARE", Title = "Monitor ligado na placa de vídeo", Weight = 10,
            Found = $"Monitor principal saindo de: {main.Adapter}",
            Level = onDiscrete ? CheckLevel.Ok : CheckLevel.Bad,
            Advice = onDiscrete
                ? "O cabo está na placa de vídeo dedicada."
                : "O cabo do monitor está na saída da placa-mãe (vídeo integrado). Os jogos podem rodar na GPU fraca ou passar a imagem por " +
                  "ela, perdendo muito FPS. Ligue o cabo nas saídas da placa de vídeo (as horizontais, mais abaixo no gabinete).",
            Fix = onDiscrete ? FixKind.None : FixKind.Bios, FixLabel = "COMO FAZER",
            FixTarget = "Desligue o monitor, tire o cabo da saída de vídeo da placa-mãe (perto das portas USB, na vertical) e ligue numa saída " +
                        "da placa de vídeo (fileira horizontal mais abaixo). Prefira DisplayPort para monitores de 144 Hz ou mais.",
        };
    }

    private static string? GpuValue(GpuInfo g, string section, string header) =>
        g.Sections.FirstOrDefault(s => s.Header == section)?.Children.FirstOrDefault(n => n.Header == header)?.Value;

    private static GamerCheck? CheckPcie(GpuInfo g)
    {
        string? max = GpuValue(g, "BARRAMENTO", "Interface"), cur = GpuValue(g, "BARRAMENTO", "Em uso agora");
        var mw = Regex.Match(max ?? "", @"x(\d+)"); var cw = Regex.Match(cur ?? "", @"x(\d+)");
        if (!mw.Success || !cw.Success) return null;
        int maxW = int.Parse(mw.Groups[1].Value), curW = int.Parse(cw.Groups[1].Value);
        bool narrow = curW < maxW;
        return new GamerCheck
        {
            Id = "gpu.pcie", Area = "HARDWARE", Title = "Placa de vídeo na velocidade total do slot", Weight = 6,
            Found = $"Placa: {max} · usando agora: {cur}",
            Level = narrow ? CheckLevel.Warn : CheckLevel.Ok,
            Advice = narrow
                ? $"A placa está com x{curW} linhas em vez de x{maxW}. Causas comuns: está no slot de baixo (que só tem x4/x8), um riser/cabo de " +
                  "extensão ruim, ou um SSD M.2 dividindo as linhas com o slot. Use o slot PCIe de cima, o mais perto da CPU."
                : "Largura total (a geração PCIe pode cair em repouso para economizar energia; é normal).",
            Fix = narrow ? FixKind.Bios : FixKind.None, FixLabel = "COMO FAZER",
            FixTarget = "Desligue o PC, tire da tomada e coloque a placa de vídeo no primeiro slot PCIe x16 (o mais perto do processador). " +
                        "Se usa riser (montagem vertical), teste sem ele. Se tiver SSD M.2 em slots que dividem linhas com a placa, veja no manual " +
                        "da placa-mãe a tabela de compartilhamento de linhas (\"lane sharing\").",
        };
    }

    private static GamerCheck? CheckRebar(GpuInfo g)
    {
        string? v = GpuValue(g, "MEMÓRIA", "Resizable BAR");
        v ??= RebarFromWmi(g.Name); // NVIDIA: NVML; AMD/Intel: tamanho da janela de memória da placa (WMI)
        if (v == null) return null;
        bool on = v.StartsWith("Ativado", StringComparison.OrdinalIgnoreCase);
        return new GamerCheck
        {
            Id = "gpu.rebar", Area = "BIOS", Title = "Resizable BAR (Smart Access Memory)", Weight = 4, Found = v,
            Level = on ? CheckLevel.Ok : CheckLevel.Warn,
            Advice = on ? "A CPU acessa toda a memória da placa de vídeo de uma vez."
                        : "Com o Resizable BAR a CPU enxerga toda a VRAM de uma vez. Dá de 0 a 10% de FPS conforme o jogo (bem mais nas Intel Arc). " +
                          "Liga-se na BIOS e exige Windows em modo UEFI.",
            Fix = on ? FixKind.None : FixKind.Bios, FixLabel = "GUIA DA BIOS", FixTarget = on ? null : BiosGuide("rebar"),
        };
    }

    private static GamerCheck? CheckDriverAge(GpuInfo g)
    {
        if (!DateTime.TryParseExact(GpuValue(g, "DRIVER E BIOS", "Data do driver"), "dd/MM/yyyy", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var date)) return null;
        int days = (int)(DateTime.Now - date).TotalDays;
        return new GamerCheck
        {
            Id = "gpu.driver", Area = "WINDOWS", Title = "Driver de vídeo atualizado", Weight = 5,
            Found = $"{g.Name} · driver de {date:dd/MM/yyyy} ({days} dias)",
            Level = days > 365 ? CheckLevel.Bad : days > 150 ? CheckLevel.Warn : CheckLevel.Ok,
            Advice = days > 150
                ? "Drivers novos trazem otimizações para jogos lançados recentemente, correções de travamentos e recursos como DLSS/FSR mais novos."
                : "Driver recente.",
            Fix = days > 150 ? FixKind.Page : FixKind.None, FixLabel = "ABRIR DRIVER", FixTarget = "PageDriver",
        };
    }

    // ================================================================== discos

    private static GamerCheck? CheckSystemDisk()
    {
        char letter = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:")[0];
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
        uint? number = null;
        using (var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'")))
            foreach (ManagementObject o in s.Get()) number = Convert.ToUInt32(o["DiskNumber"]);
        if (number == null) return null;

        int media = 0, bus = 0; string model = "";
        using (var s = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk WHERE DeviceId = '{number}'")))
            foreach (ManagementObject o in s.Get())
            {
                media = Convert.ToInt32(o["MediaType"] ?? 0); bus = Convert.ToInt32(o["BusType"] ?? 0);
                model = o["FriendlyName"]?.ToString() ?? "";
            }
        bool nvme = bus == 17;
        string kind = nvme ? "SSD NVMe" : media == 4 ? "SSD SATA" : media == 3 ? "HD (disco mecânico)" : "tipo não informado";
        return new GamerCheck
        {
            Id = "disk.system", Area = "HARDWARE", Title = "Windows instalado em SSD", Weight = 8,
            Found = $"{letter}: em {model} · {kind}",
            Level = media == 3 ? CheckLevel.Bad : media == 0 && !nvme ? CheckLevel.Info : CheckLevel.Ok,
            Advice = media == 3
                ? "Windows e jogos em HD mecânico deixam tudo lento: carregamento de mapas, texturas aparecendo atrasadas e engasgos em jogos " +
                  "de mundo aberto. Um SSD é o upgrade que mais muda a sensação de uso do PC."
                : nvme ? "O mais rápido disponível. Jogos com DirectStorage aproveitam." : "SSD: carregamentos rápidos.",
        };
    }

    private static GamerCheck CheckFreeSpace()
    {
        var d = new DriveInfo(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:");
        double free = d.AvailableFreeSpace / 1073741824.0, pct = (double)d.AvailableFreeSpace / d.TotalSize;
        return new GamerCheck
        {
            Id = "disk.free", Area = "HARDWARE", Title = "Espaço livre no disco do Windows", Weight = 4,
            Found = $"{free:0} GB livres ({pct:P0})",
            Level = free < 15 ? CheckLevel.Bad : pct < 0.10 ? CheckLevel.Warn : CheckLevel.Ok,
            Advice = free < 15 || pct < 0.10
                ? "SSD quase cheio fica mais lento para gravar, e jogos e o Windows Update precisam de espaço para atualizar e para o arquivo de paginação."
                : "Espaço suficiente.",
            Fix = free < 15 || pct < 0.10 ? FixKind.Settings : FixKind.None, FixLabel = "LIMPAR ESPAÇO", FixTarget = "ms-settings:storagesense",
        };
    }

    // ================================================================== energia

    public sealed record PowerState(bool HasBattery, bool OnBattery, int Percent);

    public static PowerState Power()
    {
        if (!GetSystemPowerStatus(out var p)) return new PowerState(false, false, 0);
        bool battery = (p.BatteryFlag & 128) == 0 && p.BatteryFlag != 255; // 128 = sem bateria, 255 = desconhecido
        return new PowerState(battery, battery && p.ACLineStatus == 0, p.BatteryLifePercent);
    }

    private static GamerCheck CheckAc(PowerState p) => new()
    {
        Id = "power.ac", Area = "HARDWARE", Title = "Notebook na tomada", Weight = 7,
        Found = p.OnBattery ? $"Na bateria ({p.Percent}%)" : "Na tomada",
        Level = p.OnBattery ? CheckLevel.Bad : CheckLevel.Ok,
        Advice = p.OnBattery ? "Na bateria a placa de vídeo e a CPU ficam limitadas a uma fração da potência. Para jogar, ligue na tomada."
                             : "A GPU e a CPU podem usar a potência total.",
    };

    // ================================================================== Windows

    /// <summary>Ajustes cuja falta pesa mais (vermelho em vez de amarelo).</summary>
    private static readonly HashSet<string> TweakLevelBad = new() { "gamedvr", "power" };

    private static GamerCheck CheckTweak(Tweak t, string cpu, bool laptop)
    {
        bool on = t.Read() ?? false;
        string? avoid = t.AvoidFor(cpu);
        var c = new GamerCheck
        {
            Id = "tweak." + t.Id, Area = "WINDOWS", Title = t.Title, Weight = avoid != null ? 0 : t.Weight, NeedsRestart = t.NeedsRestart,
            Found = on ? "Aplicado" : "Não aplicado",
            Advice = avoid ?? (t.Description + " " + t.Effect),
            Level = avoid != null ? CheckLevel.Info : on ? CheckLevel.Ok : TweakLevelBad.Contains(t.Id) ? CheckLevel.Bad : CheckLevel.Warn,
        };
        if (!on && avoid == null)
        {
            c.Fix = FixKind.Auto; c.FixLabel = "APLICAR";
            c.AutoFix = s =>
            {
                t.Write(true, s);
                s.Save();
                return t.Read() == true ? "" : "O Windows não aceitou a mudança (precisa do app como administrador).";
            };
            if (t.Weight == 0) c.TurboSkip = "Ajuste avançado: só vale para quem tem o problema que ele resolve. Aplique à mão, se precisar.";
            else if (laptop && t.LaptopCaution) c.TurboSkip = "Gasta mais bateria: em notebook, aplique à mão se for jogar na tomada.";
        }
        return c;
    }

    private static GamerCheck? CheckPagefile()
    {
        bool auto = false; int files = 0;
        using (var s = new ManagementObjectSearcher("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem"))
            foreach (ManagementObject o in s.Get()) auto = o["AutomaticManagedPagefile"] is true;
        using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage"))
            files = s.Get().Count;
        bool ok = auto || files > 0;
        return new GamerCheck
        {
            Id = "win.pagefile", Area = "WINDOWS", Title = "Arquivo de paginação ativo", Weight = 5,
            Found = auto ? "Gerenciado pelo Windows" : files > 0 ? $"Tamanho manual ({files} arquivo{(files > 1 ? "s" : "")})" : "Desativado",
            Level = ok ? CheckLevel.Ok : CheckLevel.Bad,
            Advice = ok ? "Configuração segura." :
                "Sem arquivo de paginação, jogos que reservam muita memória (mesmo sem usar) fecham sozinhos com \"memória insuficiente\". " +
                "Desligar não dá FPS. Volte para \"Gerenciar automaticamente\".",
            Fix = ok ? FixKind.None : FixKind.Settings, FixLabel = "ABRIR CONFIGURAÇÃO", FixTarget = "SystemPropertiesPerformance.exe",
        };
    }

    private static GamerCheck CheckStartup()
    {
        int enabled = StartupManager.List().Count(e => e.Enabled && !e.FileMissing);
        return new GamerCheck
        {
            Id = "win.startup", Area = "WINDOWS", Title = "Programas abrindo com o Windows", Weight = 3,
            Found = $"{enabled} programas ativados",
            Level = enabled > 15 ? CheckLevel.Bad : enabled > 8 ? CheckLevel.Warn : CheckLevel.Ok,
            Advice = enabled > 8 ? "Cada programa na inicialização fica rodando em segundo plano, usando RAM e CPU durante o jogo. Desative os que você não usa todo dia."
                                 : "Poucos programas em segundo plano.",
            Fix = FixKind.Page, FixLabel = "REVISAR", FixTarget = "PageStartup",
        };
    }

    private static GamerCheck? CheckVbs()
    {
        int vbs = 0; bool hvci = false;
        var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\DeviceGuard");
        using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard")))
            foreach (ManagementObject o in s.Get())
            {
                vbs = Convert.ToInt32(o["VirtualizationBasedSecurityStatus"] ?? 0);
                hvci = o["SecurityServicesRunning"] is uint[] running && running.Contains(2u);
            }
        // Não mexemos em segurança: só informamos, e a decisão fica com o usuário.
        return new GamerCheck
        {
            Id = "win.vbs", Area = "WINDOWS", Title = "Isolamento de núcleo (Integridade de Memória)", Weight = 0, Level = CheckLevel.Info,
            Found = hvci ? "Integridade de Memória ligada" : vbs == 2 ? "VBS ligado, Integridade de Memória desligada" : "Desligado",
            Advice = hvci
                ? "É uma proteção contra drivers maliciosos. Em alguns jogos custa de 0 a 10% de FPS, principalmente em CPUs mais antigas " +
                  "(antes do Intel 11ª geração e Ryzen 2000). O tHg Optimization não desliga proteções: se quiser trocar segurança por FPS, a decisão é sua."
                : "Sem custo de desempenho da virtualização de segurança.",
            Fix = hvci ? FixKind.Settings : FixKind.None, FixLabel = "VER NO WINDOWS", FixTarget = "windowsdefender://coreisolation",
        };
    }

    private static GamerCheck CheckWindowsVersion()
    {
        int build = Environment.OSVersion.Version.Build;
        bool win11 = build >= 22000;
        return new GamerCheck
        {
            Id = "win.version", Area = "WINDOWS", Title = "Versão do Windows", Weight = 0, Level = win11 ? CheckLevel.Ok : CheckLevel.Info,
            Found = $"{(win11 ? "Windows 11" : "Windows 10")} (build {build})",
            Advice = win11 ? "Tem os recursos de jogo mais novos (otimizações em janela, Auto HDR, DirectStorage completo)."
                           : "O Windows 10 está fora do suporte geral. O Windows 11 tem otimizações para jogos em janela, Auto HDR e agendamento melhor para CPUs Intel híbridas.",
        };
    }

    // ================================================================== rede

    private static GamerCheck? CheckNetwork()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                        && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)))
            .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
            .FirstOrDefault();
        if (nic == null) return null;
        bool wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        long mbps = nic.Speed / 1_000_000;
        var c = new GamerCheck
        {
            Id = "net.link", Area = "REDE", Title = "Conexão de internet para jogar", Weight = 5,
            Found = $"{(wifi ? "Wi-Fi" : "Cabo")} · {nic.Description} · {mbps:N0} Mbps de link",
            Fix = FixKind.Page, FixLabel = "TESTAR PING", FixTarget = "PageNet",
        };
        if (wifi)
        {
            c.Level = CheckLevel.Warn;
            c.Advice = "Wi-Fi tem variação de ping (jitter) e perda de pacotes que causam \"teleporte\" em jogos online. Para competitivo, use cabo; " +
                       "se não der, Wi-Fi 5 GHz/6 GHz perto do roteador.";
        }
        else if (mbps is > 0 and <= 100)
        {
            c.Level = CheckLevel.Warn;
            c.Advice = "A placa de rede conectou a 100 Mbps. Quase sempre é cabo danificado, de baixa qualidade, ou porta antiga no roteador. " +
                       "Troque por um cabo CAT5e/CAT6 para ter 1 Gbps.";
        }
        else
        {
            c.Level = CheckLevel.Ok;
            c.Advice = "Conexão por cabo: a melhor opção para ping estável.";
        }
        return c;
    }

    // ================================================================== BIOS

    public static bool IsUefi => GetFirmwareType(out int t) && t == 2;

    // ================================================================== AMD, notebooks e latência

    /// <summary>
    /// Resizable BAR em qualquer placa: sem ele, a janela de memória da placa vista pela CPU é de 256 MB; com ele, é do tamanho
    /// da VRAM. Lido dos recursos do dispositivo (o mesmo que o Gerenciador de Dispositivos mostra em "Recursos").
    /// </summary>
    private static string? RebarFromWmi(string gpuName)
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT PNPDeviceID, Name FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
            {
                if (!string.Equals(o["Name"]?.ToString()?.Trim(), gpuName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                string id = o["PNPDeviceID"]?.ToString() ?? "";
                if (!id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) return null;
                ulong biggest = 0;
                using var a = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_PnPEntity.DeviceID='{id.Replace("\\", "\\\\")}'}} WHERE ResultClass = Win32_DeviceMemoryAddress");
                foreach (ManagementObject m in a.Get())
                {
                    ulong st = Convert.ToUInt64(m["StartingAddress"] ?? 0UL), en = Convert.ToUInt64(m["EndingAddress"] ?? 0UL);
                    if (en > st) biggest = Math.Max(biggest, en - st + 1);
                }
                if (biggest > 300UL << 20) return $"Ativado (a CPU enxerga {biggest >> 20:N0} MB da placa)";
                if (biggest >= 250UL << 20) return "Desativado (a CPU enxerga só 256 MB da placa)";
                return null;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Dispositivos de driver da AMD (chipset, PPM, otimizador do 3D V-Cache) com versão e data.</summary>
    private static List<(string Name, string Version, DateTime? Date)> AmdDevices()
    {
        var list = new List<(string, string, DateTime?)>();
        using var s = new ManagementObjectSearcher(
            "SELECT DeviceName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceName LIKE 'AMD%'");
        foreach (ManagementObject o in s.Get())
        {
            DateTime? date = null;
            if (o["DriverDate"]?.ToString() is { Length: >= 8 } d &&
                DateTime.TryParseExact(d[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) date = dt;
            list.Add((o["DeviceName"]?.ToString() ?? "", o["DriverVersion"]?.ToString() ?? "", date));
        }
        return list;
    }

    private const string AmdChipsetUrl = "https://www.amd.com/pt/support/download/drivers.html";

    private static GamerCheck? CheckAmdChipset(List<(string Name, string Version, DateTime? Date)> amd, string cpu)
    {
        bool x3d = cpu.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        // Instalado: pela lista de programas (AMD Chipset Software) ou pelo dispositivo PPM que só ele instala.
        Version? cur = Try2(AmdChipset.Installed);
        var ppm = amd.FirstOrDefault(d => d.Name.Contains("PPM Provisioning", StringComparison.OrdinalIgnoreCase));
        bool installed = cur != null || ppm.Name != null;
        AmdChipset.Latest? latest = null;
        try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); latest = AmdChipset.LatestAsync(cpu, cts.Token).GetAwaiter().GetResult(); }
        catch { }
        bool old = installed && (latest != null && cur != null ? cur < latest.Version
                                 : ppm.Date is DateTime dt && (DateTime.Now - dt).TotalDays > (x3d ? 365 : 730));
        string verText = cur is { Major: > 0 } ? cur.ToString() : ppm.Version;
        var c = new GamerCheck
        {
            Id = "cpu.amdchipset", Area = "WINDOWS", Title = "Driver de chipset AMD", Weight = x3d ? 6 : 4, NeedsRestart = true,
            Found = !installed ? "Não instalado (o Windows está usando o driver genérico)"
                  : $"Instalado · {verText}" + (latest != null ? (old ? $" · nova versão {latest.Version}" : " · versão mais nova") : ""),
            Level = !installed ? (x3d ? CheckLevel.Bad : CheckLevel.Warn) : old ? CheckLevel.Warn : CheckLevel.Ok,
            Advice = !installed
                ? "O driver de chipset da AMD traz as regras de energia e de escolha de núcleos feitas para o Ryzen. Sem ele, o Windows trata " +
                  "o processador como genérico: núcleos acordam devagar e, nos X3D de dois CCDs, o jogo pode rodar nos núcleos sem 3D V-Cache " +
                  "(10 a 20% menos FPS). O tHg baixa do site da AMD, confere a assinatura e instala sozinho."
                : old ? "Há uma versão mais nova do driver de chipset. As novas melhoram o agendamento de núcleos (principalmente nos X3D). O tHg atualiza sozinho."
                : "Driver de chipset da AMD em dia.",
            Fix = installed && !old ? FixKind.None : FixKind.Auto,
            FixLabel = installed ? "ATUALIZAR" : "INSTALAR",
        };
        if (c.Fix == FixKind.Auto) c.AutoFix = _ => AmdChipset.InstallLatest(cpu, t => c.Status = t);
        return c;
    }

    private static GamerCheck? CheckX3dOptimizer(List<(string Name, string Version, DateTime? Date)> amd, string cpu)
    {
        bool has = amd.Any(d => d.Name.Contains("V-Cache", StringComparison.OrdinalIgnoreCase));
        string? plan = Try2(PowerPlan.Active);
        bool balanced = plan == "381b4222-f694-41f0-9685-ff5bb260df2e";
        var level = !has ? CheckLevel.Bad : balanced ? CheckLevel.Ok : CheckLevel.Warn;
        var c = new GamerCheck
        {
            Id = "cpu.x3dopt", Area = "WINDOWS", Title = "Otimizador do 3D V-Cache (Ryzen X3D)", Weight = 8,
            Found = (has ? "AMD 3D V-Cache Performance Optimizer instalado" : "Otimizador do 3D V-Cache não encontrado") +
                    (balanced ? " · plano Equilibrado" : " · plano de energia diferente do Equilibrado"),
            Level = level,
            Advice = !has
                ? $"No {cpu} só metade dos núcleos tem o 3D V-Cache. Quem manda o jogo para eles é o \"AMD 3D V-Cache Performance Optimizer\", " +
                  "que vem no driver de chipset da AMD. Sem ele o jogo se espalha pelos dois CCDs e perde FPS. O tHg instala o chipset AMD sozinho; depois, reinicie. " +
                  "Enquanto isso, o Modo Jogo do tHg prende o jogo nos núcleos com V-Cache."
                : !balanced
                ? "O otimizador da AMD só estaciona o CCD sem V-Cache no plano Equilibrado. Volte para o plano Equilibrado (o tHg não troca o plano neste processador)."
                : "Tudo certo: o driver da AMD manda os jogos para os núcleos com 3D V-Cache. Mantenha o Xbox Game Bar instalado e atualizado " +
                  "(é por ele que o Windows reconhece que um jogo abriu) e, se um jogo não for reconhecido, abra o Game Bar (Win+G) e marque \"Lembrar que isto é um jogo\".",
            Fix = !has || !balanced ? FixKind.Auto : FixKind.None, NeedsRestart = !has,
            FixLabel = !has ? "INSTALAR CHIPSET AMD" : "USAR O EQUILIBRADO",
        };
        if (!has) c.AutoFix = _ => AmdChipset.InstallLatest(cpu, t => c.Status = t);
        else if (!balanced) c.AutoFix = _ => SecurityAudit.Run("powercfg.exe", $"/setactive {PowerPlan.Balanced}", 10_000).Code == 0 ? "" : "O Windows recusou a troca do plano.";
        return c;
    }

    private static T? Try2<T>(Func<T> f) { try { return f(); } catch { return default; } }

    private static GamerCheck? AmdRadeonGuide(GpuInfo g)
    {
        bool rdna4 = Regex.IsMatch(g.Name, @"RX\s*90\d0", RegexOptions.IgnoreCase);
        var lines = new List<string>
        {
            $"Placa: {g.Name}",
            "",
            "Abra o AMD Software (botão direito na área de trabalho → AMD Software: Adrenalin Edition) → Jogos → Gráficos (global):",
            "",
            "• HYPR-RX: Desligado em jogos competitivos (ele liga o Boost e o RSR, que mudam a resolução).",
            "• Radeon Anti-Lag: Ligado. Nos jogos com Anti-Lag 2 (CS2 e outros), ligue também a opção dentro do jogo.",
            "• Radeon Chill e Radeon Boost: Desligados (seguram ou derrubam a resolução e o FPS).",
            "• Enhanced Sync: Desligado (causa engasgos com FreeSync).",
            "• Aguardar atualização vertical (V-Sync): com FreeSync, \"Sempre ligado\" junto com o limite de FPS abaixo; sem FreeSync, Desligado.",
            "• Qualidade da filtragem de textura: Desempenho · Otimização de formato de superfície: Ligado.",
            "• Modo de tesselação: Otimizado pela AMD.",
            "• Cache de shader: Otimizado pela AMD.",
            "",
            "Monitor (aba Tela): AMD FreeSync Ligado. Para a menor latência com FreeSync, limite o FPS 3 abaixo da taxa do monitor " +
            "(ex.: 141 em 144 Hz) pelo próprio jogo ou pelo FRTC.",
            "Sistema → Smart Access Memory: confira que aparece como Ligado (precisa do Resizable BAR na BIOS).",
        };
        if (rdna4)
        {
            lines.Add("");
            lines.Add("Radeon RX 9000: ligue \"FSR 4\" (atualização do FSR) em Jogos → Gráficos: jogos com FSR 3.1 passam a usar o FSR 4, com imagem bem melhor.");
        }
        lines.Add("");
        lines.Add("Nada disso precisa reiniciar. Os perfis por jogo (aba de cada jogo) têm prioridade sobre os globais.");
        lines.Add("A receita separada para jogos competitivos e de alta fidelidade, com o limite de FPS calculado para o seu monitor, fica em GAME → Modo Jogo.");
        return new GamerCheck
        {
            Id = "gpu.amdsettings", Area = "WINDOWS", Title = "AMD Software (Adrenalin) configurado para jogos", Weight = 0, Level = CheckLevel.Info,
            Found = "Configuração recomendada para placas Radeon",
            Advice = "A AMD não permite que outros programas mudem o painel dela com segurança, então o tHg mostra o passo a passo do que os jogadores competitivos usam.",
            Fix = FixKind.Bios, FixLabel = "COMO CONFIGURAR", FixTarget = string.Join("\n", lines),
        };
    }

    /// <summary>Notebook com placa dedicada: a tela interna passa pelo vídeo integrado, a não ser que o MUX esteja no modo dedicado.</summary>
    private static GamerCheck? CheckMux(List<DisplayState> displays, List<GpuInfo> gpus)
    {
        if (displays.Count == 0 || !gpus.Any(g => IsDiscrete(g.Name))) return null;
        var main = displays.FirstOrDefault(d => d.Primary) ?? displays[0];
        bool onDiscrete = IsDiscrete(main.Adapter);
        string maker = Try2(() =>
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_ComputerSystem");
            foreach (ManagementObject o in s.Get()) return o["Manufacturer"]?.ToString() ?? "";
            return "";
        }) ?? "";
        string m = maker.ToUpperInvariant();
        string how = m.Contains("ASUS") ? "Armoury Crate → Modo de GPU (GPU Mode) → \"Ultimate\" (ou \"Somente dGPU\") e reinicie."
                   : m.Contains("LENOVO") ? "Lenovo Vantage ou Legion Space → Modo de GPU/\"Hybrid Mode\" → Somente GPU dedicada (dGPU) e reinicie."
                   : m.Contains("MICRO-STAR") || m.Contains("MSI") ? "MSI Center → Recursos → GPU Switch → \"Discrete Graphics Mode\" e reinicie."
                   : m.Contains("ACER") ? "NitroSense ou PredatorSense → GPU Mode / MUX → modo dedicado (só modelos com MUX) e reinicie."
                   : m.Contains("DELL") || m.Contains("ALIENWARE") ? "Alienware Command Center (ou a BIOS) → \"Hybrid Graphics\" ou \"Advanced Optimus\": desligue o modo híbrido e reinicie."
                   : m.Contains("HP") ? "OMEN Gaming Hub → Modo gráfico → \"Discreto\" (ou na BIOS: Graphics → Discrete) e reinicie."
                   : m.Contains("GIGABYTE") || m.Contains("AORUS") ? "Gigabyte Control Center → GPU Switch → \"dGPU only\" e reinicie."
                   : "No app do fabricante do notebook, procure \"MUX\", \"Modo de GPU\", \"dGPU\" ou \"Discrete\" e escolha o modo da placa dedicada.";
        return new GamerCheck
        {
            Id = "display.mux", Area = "HARDWARE", Title = "Tela do notebook na placa de vídeo dedicada (MUX)", Weight = 5,
            Found = $"Tela principal saindo de: {main.Adapter}",
            Level = onDiscrete ? CheckLevel.Ok : CheckLevel.Warn,
            Advice = onDiscrete
                ? "A imagem sai direto da placa dedicada: sem perda pelo vídeo integrado."
                : "A placa dedicada desenha o jogo, mas a imagem passa pelo vídeo integrado até a tela. Isso custa de 5 a 15% de FPS e " +
                  "aumenta a latência. Notebooks gamer com chave MUX podem ligar a tela direto na placa dedicada.",
            Fix = onDiscrete ? FixKind.None : FixKind.Bios, FixLabel = "COMO FAZER",
            FixTarget = how + "\n\nSe o notebook não tem MUX: um monitor externo na saída HDMI ou USB-C/DisplayPort ligada à placa dedicada " +
                        "(normalmente a traseira) também evita o vídeo integrado.\n\nNo modo dedicado a bateria dura bem menos: use na tomada.",
        };
    }

    private static GamerCheck CheckLatency(LatencyResult? l)
    {
        if (l == null || l.Error != null)
            return new GamerCheck
            {
                Id = "lat.dpc", Area = "WINDOWS", Title = "Latência dos drivers (engasgos)", Weight = 0, Level = CheckLevel.Info,
                Found = "Ainda não testado",
                Advice = "O teste de engasgos mede, em 60 segundos, se algum driver (rede, Wi-Fi, áudio, USB) segura o processador e causa microengasgos.",
                Fix = FixKind.Page, FixLabel = "TESTAR", FixTarget = "PageLatency",
            };
        var worst = l.Drivers.FirstOrDefault();
        return new GamerCheck
        {
            Id = "lat.dpc", Area = "WINDOWS", Title = "Latência dos drivers (engasgos)", Weight = 4,
            Found = $"{l.MaxUs:N0} µs no pior caso" + (worst != null ? $" · {worst.What} ({worst.File})" : "") + $" · testado em {l.When:dd/MM}",
            Level = l.MaxUs >= 2000 ? CheckLevel.Bad : l.MaxUs >= 500 ? CheckLevel.Warn : CheckLevel.Ok,
            Advice = l.MaxUs >= 500 && worst != null ? worst.Fix : "Os drivers não estão segurando o processador.",
            Fix = FixKind.Page, FixLabel = l.MaxUs >= 500 ? "VER DETALHES" : "TESTAR DE NOVO", FixTarget = "PageLatency",
        };
    }

    /// <summary>Passo a passo na BIOS conforme o fabricante da placa-mãe (lido pelo WMI).</summary>
    public static string BiosGuide(string what)
    {
        var (maker, model) = OcCheck.Motherboard();
        bool giga = maker.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase);
        bool asus = maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase);
        bool msi = maker.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || maker.Contains("MSI", StringComparison.OrdinalIgnoreCase);
        bool asrock = maker.Contains("ASRock", StringComparison.OrdinalIgnoreCase);
        string board = $"{(giga ? "Gigabyte" : maker.Split(' ').FirstOrDefault() ?? "")} {model}".Trim();
        var lines = new List<string>
        {
            $"Placa-mãe: {(board.Length > 0 ? board : "não identificada")}",
            "",
            "1. Entrar na BIOS: use o botão \"Reiniciar na BIOS\" abaixo, ou reinicie e aperte Delete (notebooks: F2) várias vezes.",
            "   Em placas com modo fácil (EZ Mode), aperte F7 para o modo avançado.",
        };
        if (what == "xmp")
        {
            lines.Add("2. Ativar o perfil da memória:");
            lines.Add("   " + (giga ? "Tweaker → Extreme Memory Profile (X.M.P.) → Profile 1 (ou EXPO I)"
                               : asus ? "Ai Tweaker → Ai Overclock Tuner → XMP I (ou EXPO I)"
                               : msi ? "OC → Extreme Memory Profile (XMP) / A-XMP / EXPO → Profile 1 (no EZ Mode há um botão XMP no topo)"
                               : asrock ? "OC Tweaker → DRAM Configuration → Load XMP Setting / EXPO → Profile 1"
                               : "procure XMP, EXPO, DOCP ou \"Memory Profile\" no menu de memória/overclock"));
            lines.Add("3. F10 para salvar e reiniciar. O primeiro boot pode demorar (treino de memória): espere até 2–3 minutos.");
            lines.Add("4. Volte aqui e rode o diagnóstico de novo. Depois, rode o Stress test \"CPU + RAM\" por 15 minutos para conferir a estabilidade.");
            lines.Add("");
            lines.Add("Se não ligar: espere 3 tentativas (a placa volta ao padrão sozinha) ou limpe a CMOS. Tente o Profile 2 ou um degrau abaixo.");
        }
        else if (what == "rebar")
        {
            if (!IsUefi)
            {
                lines.Add("ATENÇÃO: este Windows foi instalado em modo legado (MBR/CSM). Desligar o CSM sem converter o disco impede o Windows de ligar.");
                lines.Add("Converta antes com o MBR2GPT da Microsoft (mbr2gpt /validate e depois /convert, no Prompt como administrador) e faça backup.");
                lines.Add("");
            }
            lines.Add("2. Ativar Above 4G Decoding e Re-Size BAR:");
            lines.Add("   " + (giga ? "Settings → IO Ports → Above 4G Decoding: Enabled · Re-Size BAR Support: Auto"
                               : asus ? "Advanced → PCI Subsystem Settings → Above 4G Decoding: Enabled · Re-Size BAR Support: Auto"
                               : msi ? "Settings → Advanced → PCI Subsystem Settings → Above 4G memory: Enabled · Re-Size BAR Support: Enabled"
                               : asrock ? "Advanced → Chipset Configuration → Above 4G Decoding: Enabled · C.A.M. Clever Access Memory: Enabled"
                               : "procure Above 4G Decoding e Re-Size BAR (ou Smart Access Memory) nos menus de PCI/Chipset"));
            lines.Add("3. Desligar o CSM (só se o Windows está em UEFI): " + (giga ? "Boot → CSM Support: Disabled" : "Boot → CSM / Launch CSM: Disabled"));
            lines.Add("4. F10 para salvar. Mantenha o driver de vídeo atualizado: ele decide em quais jogos usar o recurso.");
        }
        else if (what.StartsWith("x3d:"))
        {
            string cpu = what[4..];
            bool zen5 = Regex.IsMatch(cpu, @"9\d{3}X3D", RegexOptions.IgnoreCase);
            bool am5 = zen5 || Regex.IsMatch(cpu, @"7\d{3}X3D", RegexOptions.IgnoreCase);
            bool dual = WindowsTweaks.IsDualCcdX3D(cpu);
            lines.Add("2. Memória:");
            lines.Add(am5 ? "   Ligue o EXPO (" + (giga ? "Tweaker → Extreme Memory Profile" : asus ? "Ai Tweaker → Ai Overclock Tuner → EXPO I" : msi ? "OC → A-XMP/EXPO" : "menu de memória") +
                            "). O ponto ideal do Ryzen 7000/9000 é DDR5-6000 CL30 com FCLK em 2000 MHz e UCLK = MEMCLK (modo 1:1)."
                          : "   Ligue o XMP/DOCP. O ponto ideal do Ryzen 5000 é DDR4-3600 CL16 com FCLK 1800 MHz (1:1).");
            lines.Add("3. Curve Optimizer (menos calor = mais clock por mais tempo):");
            lines.Add("   " + (giga ? "Tweaker → Precision Boost Overdrive → Curve Optimizer" : asus ? "Ai Tweaker → Precision Boost Overdrive → Curve Optimizer"
                              : msi ? "OC → Advanced CPU Configuration → AMD Overclocking → Precision Boost Overdrive → Curve Optimizer"
                              : "AMD Overclocking → Precision Boost Overdrive → Curve Optimizer") + ": All Cores, Negative.");
            lines.Add("   Comece em -15, rode o Stress test do tHg por 30 min; se estável, tente -20/-25. Travou ou reiniciou: volte 5 pontos.");
            if (dual) lines.Add("   Dois CCDs: dá para ajustar por núcleo (Per Core); o CCD com V-Cache costuma aceitar menos que o outro.");
            if (zen5) lines.Add("4. Ryzen 9000X3D aceita PBO completo: Precision Boost Overdrive → Advanced → PBO Limits: Motherboard. Ganho pequeno em jogos, maior em tarefas pesadas.");
            else if (am5) lines.Add("4. Ryzen 7000X3D: o PBO fica limitado pela AMD; use só o Curve Optimizer. Não mexa na voltagem do SoC acima de 1,3 V.");
            if (dual && zen5) lines.Add("   Opcional: \"X3D Turbo Mode\"/\"Gaming Mode\" (algumas placas) desliga o CCD sem V-Cache: um pouco mais de FPS em alguns jogos, bem menos desempenho em tarefas pesadas.");
            lines.Add("5. F10 para salvar. Mantenha a BIOS atualizada: as versões novas do AGESA corrigem e aceleram os X3D.");
        }
        return string.Join("\n", lines);
    }

    /// <summary>Reinicia direto na tela da BIOS/UEFI (shutdown /r /fw). Só funciona em PCs UEFI.</summary>
    public static string RebootToFirmware()
    {
        if (!IsUefi) return "Este PC não inicia em modo UEFI: reinicie e aperte Delete (ou F2) durante o logo.";
        var p = Process.Start(new ProcessStartInfo("shutdown.exe", "/r /fw /t 5") { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(5000);
        return p?.ExitCode == 0 ? "" : $"O Windows recusou (código {p?.ExitCode}). Abra o app como administrador.";
    }

    // ================================================================== ponto de restauração

    /// <summary>
    /// Cria um ponto de restauração do sistema antes do TURBO. O Windows só cria um a cada 24 h e exige a Proteção do
    /// Sistema ligada no disco C:; nesses casos devolve o aviso e segue (os ajustes têm o próprio "Desfazer").
    /// </summary>
    public static async Task<string> CreateRestorePointAsync(string description = "tHg Optimization", int timeoutSeconds = 180)
    {
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"$ProgressPreference='SilentlyContinue'; try {{ Checkpoint-Computer -Description '{description}' -RestorePointType MODIFY_SETTINGS " +
            "-ErrorAction Stop -WarningAction Stop; 'OK' }} catch {{ $_.Exception.Message }}\"")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var read = p.StandardOutput.ReadToEndAsync();
        // ConfigureAwait(false): segue em qualquer thread, então quem chamar com .GetResult() não trava.
        var done = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false);
        if (done != read) { try { p.Kill(true); } catch { } return "o Windows demorou demais para criar o ponto de restauração"; }
        string o = (await read.ConfigureAwait(false)).Trim();
        return o.EndsWith("OK") ? "" : o.Length > 0 ? o : "não foi possível criar o ponto de restauração";
    }

    // ================================================================== Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("kernel32.dll")] private static extern bool GetFirmwareType(out int type);
}
