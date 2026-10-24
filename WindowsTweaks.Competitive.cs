using System.IO;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ThgOptimization;

/// <summary>Um dispositivo PCI que pode ter as interrupções presas a um núcleo (placa de vídeo ou de rede).</summary>
public sealed record IrqDevice(string Name, string PnpId, string Kind)
{
    public string Label => $"{Kind}: {Name}";
}

/// <summary>
/// Página Competitivo: latência, rede/input, perfis por jogo e ajustes de baixo nível. Os ajustes novos guardam o valor
/// de antes com o prefixo "comp." (desfeitos pelo "Desfazer tudo" da própria página); os que já existiam na página
/// Ajustes aparecem aqui com o mesmo objeto, o mesmo estado e a mesma cópia.
/// </summary>
public static partial class WindowsTweaks
{
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string TcpIfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string MemMgmt = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string Layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

    /// <summary>Os quatro módulos da página, na ordem da tela. <paramref name="main"/> é a lista da página Ajustes.</summary>
    public static Dictionary<string, List<Tweak>> CompetitiveGroups(IReadOnlyList<Tweak> main, AppSettings s)
    {
        Tweak? M(string id) => main.FirstOrDefault(t => t.Id == id);
        var timerres = M("timerres");
        return new()
        {
            ["latency"] = new[] { GameTimerTweak(s, timerres), timerres, M("mpo"), M("windowed"), M("mmcss"), HidPowerTweak() }.OfType<Tweak>().ToList(),
            ["net"] = new List<Tweak> { NagleTweak() },
            ["games"] = new[] { ValorantTweak(), Cs2FsoTweak(), GtaTweak(M("gamemode"), M("hags")), LolTweak() }.ToList(),
            ["system"] = new[] { HpetTweak(), MemoryTweak(), M("gpumsi"), M("iomsi"), Avail(AmdUlpsTweak()), Avail(AmdShaderCacheTweak()) }.OfType<Tweak>().ToList(),
        };
    }

    private static Tweak? Avail(Tweak t)
    {
        try { return t.Available() ? t : null; } catch { return null; }
    }

    /// <summary>Desfaz tudo que a página Competitivo alterou (inclusive os ajustes compartilhados exibidos nela).</summary>
    public static void RestoreCompetitive(IEnumerable<Tweak> tweaks, AppSettings s)
    {
        var list = tweaks.ToList();
        foreach (var t in Enumerable.Reverse(list)) try { t.Restore(s); } catch { }
        try { IrqRestoreAll(s); } catch { }
        try { CompetitiveProfiles.RestoreAll(s); } catch { } // depois dos ajustes: só volta o que ainda é do perfil
        var ids = list.Select(t => t.Id).ToList();
        foreach (var k in s.TweakBackup.Keys.Where(k => k.StartsWith("comp.") || ids.Any(id => k == id || k.StartsWith(id + "."))).ToList())
            s.TweakBackup.Remove(k);
        s.Save();
    }

    // ------------------------------------------------------------------ Módulo 1 · latência

    /// <summary>
    /// 0,5 ms só enquanto o jogo roda: quem pede é o Modo Jogo do tHg (NtSetTimerResolution) e devolve ao fechar o jogo.
    /// No Windows 11 o pedido de um programa em segundo plano não vale para os outros sem GlobalTimerResolutionRequests,
    /// por isso o ajuste liga os dois.
    /// </summary>
    private static Tweak GameTimerTweak(AppSettings settings, Tweak? timerres) => new()
    {
        Id = "comp.timer", Weight = 4, NeedsRestart = IsWindows11 && timerres != null,
        Title = "Timer do Windows em 0,5 ms durante o jogo",
        Description = "Quando um jogo abre, o tHg pede ao Windows o timer de 0,5 ms (o padrão é 15,6 ms; é o que o programa TimerResolution faz) " +
                      "e devolve o valor normal assim que o jogo fecha. Liga o Modo Jogo automático do tHg" +
                      (IsWindows11 ? " e o ajuste logo abaixo, sem o qual o Windows 11 ignora o pedido." : "."),
        Effect = "Melhora a consistência dos frames (1% low) e reduz atrasos de jogos que usam Sleep para limitar o FPS. Sem custo fora do jogo.",
        Read = () => settings.AutoGameMode && settings.GameModeTimer && (!IsWindows11 || timerres?.Read() != false),
        Write = (on, s) =>
        {
            if (!s.TweakBackup.ContainsKey("comp.timer")) s.TweakBackup["comp.timer"] = $"{s.AutoGameMode}|{s.GameModeTimer}";
            if (on)
            {
                s.AutoGameMode = s.GameModeTimer = true;
                if (IsWindows11) timerres?.Write(true, s);
            }
            else s.GameModeTimer = false;
        },
        Restore = s =>
        {
            if (s.TweakBackup.GetValueOrDefault("comp.timer")?.Split('|') is not [var auto, var timer]) return;
            s.AutoGameMode = auto == bool.TrueString;
            s.GameModeTimer = timer == bool.TrueString;
        },
    };

    // ------------------------------------------------------------------ Módulo 1 · mouse e teclado USB

    private const string UsbEnum = @"SYSTEM\CurrentControlSet\Enum\USB";
    private static readonly string[] HidPowerValues = { "EnhancedPowerManagementEnabled", "AllowIdleIrpInD3" };

    /// <summary>
    /// Chaves "Device Parameters" dos aparelhos USB (o composto e cada interface MI_xx) que têm um mouse ou teclado HID
    /// conectado agora, pelo VID/PID do Windows (Win32_PnPEntity das classes Mouse e Keyboard).
    /// </summary>
    private static List<string> HidUsbParamKeys()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var q = new System.Management.ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Mouse' OR PNPClass = 'Keyboard'");
            using var res = q.Get();
            foreach (System.Management.ManagementObject o in res)
                using (o)
                    if (Regex.Match(o["DeviceID"]?.ToString() ?? "", @"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}", RegexOptions.IgnoreCase) is { Success: true } m)
                        ids.Add(m.Value);
        }
        catch { }
        var list = new List<string>();
        if (ids.Count == 0) return list;
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(UsbEnum);
            foreach (var dev in usb?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                if (!ids.Any(id => dev.StartsWith(id, StringComparison.OrdinalIgnoreCase))) continue;
                using var dk = usb!.OpenSubKey(dev);
                foreach (var inst in dk?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    string p = $@"{UsbEnum}\{dev}\{inst}\Device Parameters";
                    using var pk = Registry.LocalMachine.OpenSubKey(p);
                    if (pk != null) list.Add(p);
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Mouse e teclado USB sem economia de energia (EnhancedPowerManagementEnabled = 0 e AllowIdleIrpInD3 = 0 em cada
    /// aparelho e interface): o Windows para de suspender a porta entre um pacote e outro. O valor de antes de cada chave
    /// fica guardado ("comp.hidpm.").
    /// </summary>
    private static Tweak HidPowerTweak() => new()
    {
        Id = "comp.hidpm", Weight = 2, NeedsRestart = true,
        Title = "Mouse e teclado USB sem economia de energia",
        Description = "Desliga a economia de energia avançada (EnhancedPowerManagementEnabled) e a suspensão ociosa (AllowIdleIrpInD3) " +
                      "nos mouses e teclados USB conectados agora, em cada interface do aparelho.",
        Effect = "Evita microdesconexões e variação no tempo de resposta em mouses de 4.000/8.000 Hz e teclados magnéticos (Hall Effect / Rapid Trigger). " +
                 "Gasta um pouco mais de energia (notebook). Reconecte os aparelhos ou reinicie para valer; aparelhos novos precisam aplicar de novo.",
        Read = () => HidUsbParamKeys() is { Count: > 0 } ks && ks.All(p => HidPowerValues.All(n => ReadDword(Registry.LocalMachine, p, n) == 0)),
        Write = (on, s) =>
        {
            var keys = HidUsbParamKeys();
            if (keys.Count == 0) throw new InvalidOperationException("Nenhum mouse ou teclado USB foi encontrado.");
            int denied = 0;
            foreach (var p in keys)
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(p, writable: true);
                    if (k == null) continue;
                    foreach (var n in HidPowerValues)
                    {
                        string bk = $"comp.hidpm.{n}@{p}";
                        if (!s.TweakBackup.ContainsKey(bk)) { s.TweakBackup[bk] = EncodeValue(k, n); s.Save(); }
                        if (on) k.SetValue(n, 0, RegistryValueKind.DWord);
                        else PutValue(k, n, s.TweakBackup[bk] == "d:0" ? null : s.TweakBackup[bk]);
                    }
                }
                catch (UnauthorizedAccessException) { denied++; }
                catch (System.Security.SecurityException) { denied++; }
            if (denied == keys.Count) throw new InvalidOperationException("O Windows não deixou alterar as chaves dos aparelhos USB.");
        },
        Restore = RestoreHidPower,
    };

    private static void RestoreHidPower(AppSettings s)
    {
        foreach (var (bk, prev) in s.TweakBackup.Where(kv => kv.Key.StartsWith("comp.hidpm.")).ToList())
        {
            var parts = bk["comp.hidpm.".Length..].Split('@', 2);
            // O caminho vem da cópia: só "Device Parameters" de aparelhos USB.
            if (parts.Length != 2 || !HidPowerValues.Contains(parts[0]) || !parts[1].StartsWith(UsbEnum + @"\", StringComparison.OrdinalIgnoreCase)
                || !parts[1].EndsWith(@"\Device Parameters", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(parts[1], writable: true);
                if (k != null) PutValue(k, parts[0], prev);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Módulo 2 · rede

    private static List<string> ActiveTcpInterfaceKeys()
    {
        var list = new List<string>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (!Guid.TryParse(n.Id, out var g)) continue;
                string k = $@"{TcpIfaces}\{{{g}}}";
                using var key = Registry.LocalMachine.OpenSubKey(k);
                if (key != null) list.Add(k);
            }
        }
        catch { }
        return list;
    }

    private static readonly string[] NagleValues = { "TcpAckFrequency", "TCPNoDelay" };
    /// <summary>O que é gravado: o Windows atual ignora TCPNoDelay no registro (só o setsockopt de cada programa vale).
    /// Ele segue em NagleValues só para desfazer o que versões antigas do tHg gravaram.</summary>
    private static readonly string[] NagleWrite = { "TcpAckFrequency" };

    /// <summary>Nagle desligado em cada placa de rede conectada; o valor de antes de cada placa fica guardado.</summary>
    private static Tweak NagleTweak() => new()
    {
        Id = "comp.nagle", Weight = 2, NeedsRestart = true,
        Title = "Confirmação TCP imediata (TcpAckFrequency)",
        Description = "Faz o Windows confirmar e enviar cada pacote TCP na hora, em vez de esperar para juntar vários (vale para as placas de rede conectadas agora).",
        Effect = "Reduz o atraso de jogos e chats que usam TCP: LoL, WoW, MMOs, FiveM e lobbies. Valorant, CS2 e a maioria dos FPS usam UDP " +
                 "e não mudam. Não aumenta a velocidade da internet. Reinicie o PC (ou desconecte e reconecte a rede).",
        Read = () => ActiveTcpInterfaceKeys() is { Count: > 0 } ks && ks.All(k => NagleWrite.All(n => ReadDword(Registry.LocalMachine, k, n) == 1)),
        Write = (on, s) =>
        {
            var keys = ActiveTcpInterfaceKeys();
            if (keys.Count == 0) throw new InvalidOperationException("Nenhuma placa de rede conectada foi encontrada.");
            foreach (var k in keys)
                foreach (var n in on ? NagleWrite : NagleValues)
                {
                    string bk = $"comp.nagle.{n}@{k}";
                    if (!s.TweakBackup.ContainsKey(bk)) { s.TweakBackup[bk] = ReadDword(Registry.LocalMachine, k, n)?.ToString() ?? "<ausente>"; s.Save(); }
                    using var key = Registry.LocalMachine.CreateSubKey(k);
                    if (on) key.SetValue(n, 1, RegistryValueKind.DWord);
                    else if (int.TryParse(s.TweakBackup[bk], out int prev) && prev != 1) key.SetValue(n, prev, RegistryValueKind.DWord);
                    else key.DeleteValue(n, false);
                }
        },
        Restore = s =>
        {
            foreach (var (bk, prev) in s.TweakBackup.Where(kv => kv.Key.StartsWith("comp.nagle.")).ToList())
            {
                var parts = bk["comp.nagle.".Length..].Split('@', 2);
                // O caminho vem da cópia: só valores de Nagle dentro das interfaces TCP/IP.
                if (parts.Length != 2 || !NagleValues.Contains(parts[0]) || !parts[1].StartsWith(TcpIfaces + @"\", StringComparison.OrdinalIgnoreCase)) continue;
                using var key = Registry.LocalMachine.OpenSubKey(parts[1], writable: true);
                if (key == null) continue;
                if (int.TryParse(prev, out int v)) key.SetValue(parts[0], v, RegistryValueKind.DWord);
                else key.DeleteValue(parts[0], false);
            }
        },
    };

    // ------------------------------------------------------------------ Módulo 2 · afinidade de interrupções (IRQ)

    private static string AffinityKey(string pnpId) =>
        $@"SYSTEM\CurrentControlSet\Enum\{pnpId}\Device Parameters\Interrupt Management\Affinity Policy";

    /// <summary>Placas de vídeo e de rede PCI deste PC.</summary>
    public static List<IrqDevice> IrqDevices()
    {
        var list = new List<IrqDevice>();
        void Add(string wql, string kind)
        {
            try
            {
                using var q = new System.Management.ManagementObjectSearcher(wql);
                using var res = q.Get();
                foreach (System.Management.ManagementObject o in res)
                    using (o)
                    {
                        string id = o["PNPDeviceID"]?.ToString() ?? "";
                        if (id.StartsWith(@"PCI\VEN_", StringComparison.OrdinalIgnoreCase) && list.All(d => d.PnpId != id))
                            list.Add(new IrqDevice(o["Name"]?.ToString() ?? id, id, kind));
                    }
            }
            catch { }
        }
        Add("SELECT Name, PNPDeviceID FROM Win32_VideoController", "Vídeo");
        Add("SELECT Name, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE", "Rede");
        return list;
    }

    /// <summary>Núcleo lógico fixado para o dispositivo, ou null se o Windows escolhe sozinho (padrão).</summary>
    public static int? IrqCore(IrqDevice d)
    {
        using var k = Registry.LocalMachine.OpenSubKey(AffinityKey(d.PnpId));
        if (k?.GetValue("DevicePolicy") is not 4 || k.GetValue("AssignmentSetOverride") is not byte[] { Length: > 0 } b) return null;
        ulong mask = 0;
        for (int i = 0; i < Math.Min(b.Length, 8); i++) mask |= (ulong)b[i] << (8 * i);
        return mask != 0 && (mask & (mask - 1)) == 0 ? System.Numerics.BitOperations.TrailingZeroCount(mask) : null;
    }

    /// <summary>
    /// Prende as interrupções do dispositivo a um núcleo (IrqPolicySpecifiedProcessors, como o Interrupt Affinity Policy
    /// Tool da Microsoft). null volta ao padrão. Vale depois de reiniciar.
    /// </summary>
    public static void IrqSet(IrqDevice d, int? core, AppSettings s)
    {
        if (core is int c && (c < 0 || c >= Math.Min(Environment.ProcessorCount, 64))) throw new ArgumentOutOfRangeException(nameof(core));
        string path = AffinityKey(d.PnpId);
        string bk = $"comp.irq@{path}";
        using var k = Registry.LocalMachine.CreateSubKey(path);
        if (!s.TweakBackup.ContainsKey(bk))
            s.TweakBackup[bk] = $"{(k.GetValue("DevicePolicy") is int p ? p.ToString() : "-")}|{(k.GetValue("AssignmentSetOverride") is byte[] b ? Convert.ToHexString(b) : "-")}";
        s.Save();
        if (core is int cpu)
        {
            k.SetValue("DevicePolicy", 4, RegistryValueKind.DWord);
            k.SetValue("AssignmentSetOverride", BitConverter.GetBytes(1UL << cpu), RegistryValueKind.Binary);
        }
        else WriteAffinity(k, s.TweakBackup[bk], defaultOnly: true);
    }

    /// <summary>Volta cada dispositivo alterado ao que era antes do tHg.</summary>
    public static void IrqRestoreAll(AppSettings s)
    {
        foreach (var (bk, prev) in s.TweakBackup.Where(kv => kv.Key.StartsWith("comp.irq@")).ToList())
        {
            string path = bk["comp.irq@".Length..];
            if (!path.StartsWith(@"SYSTEM\CurrentControlSet\Enum\PCI\", StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(@"Interrupt Management\Affinity Policy", StringComparison.OrdinalIgnoreCase)) continue;
            using var k = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (k != null) WriteAffinity(k, prev, defaultOnly: false);
        }
    }

    /// <summary>Grava a cópia "política|máscara em hex" ("-" = não existia). defaultOnly: ignora uma cópia que já era fixada.</summary>
    private static void WriteAffinity(RegistryKey k, string? backup, bool defaultOnly)
    {
        var parts = (backup ?? "-|-").Split('|');
        bool hadFixed = parts.Length == 2 && parts[0] == "4";
        if (defaultOnly && hadFixed) parts = new[] { "-", "-" };
        if (parts.Length == 2 && int.TryParse(parts[0], out int pol)) k.SetValue("DevicePolicy", pol, RegistryValueKind.DWord);
        else k.DeleteValue("DevicePolicy", false);
        if (parts.Length == 2 && parts[1] != "-") k.SetValue("AssignmentSetOverride", Convert.FromHexString(parts[1]), RegistryValueKind.Binary);
        else k.DeleteValue("AssignmentSetOverride", false);
    }

    // ------------------------------------------------------------------ Módulo 3 · perfis por jogo

    private static Tweak ValorantTweak() => MultiRegTweak("comp.valorant", "Otimizar Valorant (prioridade Alta)",
        "Grava nas opções de execução do Windows (IFEO) que o VALORANT-Win64-Shipping.exe sempre abre com prioridade de CPU Alta, sem programa rodando junto.",
        "Menos engasgos quando o Discord, o navegador ou a gravação disputam a CPU com o jogo. É o mesmo ajuste do Gerenciador de Tarefas, " +
        "só que permanente; não mexe no jogo nem no Vanguard. Vale na próxima vez que o jogo abrir.",
        weight: 3, values: new[]
        {
            new RegValue(Registry.LocalMachine, $@"{Ifeo}\VALORANT-Win64-Shipping.exe\PerfOptions", "CpuPriorityClass", 3, null),
        });

    /// <summary>cs2.exe em cada biblioteca da Steam.</summary>
    public static List<string> Cs2Exes() => SteamGameFiles(@"Counter-Strike Global Offensive\game\bin\win64\cs2.exe");

    private static List<string> SteamGameFiles(params string[] relative)
    {
        var list = new List<string>();
        if (SteamInfo.SteamPath() is not string steam) return list;
        foreach (var lib in SteamInfo.LibraryFolders(steam))
            foreach (var r in relative)
            {
                string f = Path.Combine(lib, "steamapps", "common", r);
                if (File.Exists(f)) list.Add(f);
            }
        return list;
    }

    private const string FsoFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    private static Tweak Cs2FsoTweak() => new()
    {
        Id = "comp.cs2", Weight = 2,
        Title = "CS2: desligar as otimizações de tela cheia",
        Description = "Marca \"Desabilitar otimizações de tela cheia\" no cs2.exe (aba Compatibilidade do Windows), para o jogo usar a tela cheia exclusiva de verdade.",
        Effect = "Pode reduzir o input lag e evitar o Windows trocar o modo de apresentação no meio da partida. Use com o CS2 em \"Tela cheia\". " +
                 "Se o FPS cair ou o Alt+Tab piorar, desligue.",
        Read = () => Cs2Exes() is { Count: > 0 } ex && ex.All(e => (ReadString(Registry.CurrentUser, Layers, e) ?? "").Contains(FsoFlag)),
        Write = (on, s) =>
        {
            var exes = Cs2Exes();
            if (exes.Count == 0) throw new InvalidOperationException("O CS2 não foi encontrado nas bibliotecas da Steam deste PC.");
            foreach (var e in exes)
            {
                string bk = $"comp.cs2@{e}";
                if (!s.TweakBackup.ContainsKey(bk)) s.TweakBackup[bk] = ReadString(Registry.CurrentUser, Layers, e) ?? "<ausente>";
                GameLauncher.SetDisableFso(e, on);
            }
        },
        Restore = s =>
        {
            foreach (var (bk, prev) in s.TweakBackup.Where(kv => kv.Key.StartsWith("comp.cs2@")).ToList())
            {
                string exe = bk["comp.cs2@".Length..];
                if (!exe.EndsWith(@"\cs2.exe", StringComparison.OrdinalIgnoreCase)) continue;
                using var k = Registry.CurrentUser.CreateSubKey(Layers);
                if (prev is null or "<ausente>") k.DeleteValue(exe, false);
                else k.SetValue(exe, prev, RegistryValueKind.String);
            }
        },
    };

    /// <summary>Pastas do cache de shaders que a Steam baixa/gera para o CS2 (app 730), em todas as bibliotecas.</summary>
    public static List<string> Cs2ShaderFolders()
    {
        if (SteamInfo.SteamPath() is not string steam) return new();
        return SteamInfo.LibraryFolders(steam).Select(l => Path.Combine(l, "steamapps", "shadercache", "730")).Where(Directory.Exists).ToList();
    }

    /// <summary>GTA V (Legacy e Enhanced, Steam/Epic/Rockstar) e o processo do jogo que o FiveM cria.</summary>
    public static List<string> GtaExes()
    {
        var list = SteamGameFiles(@"Grand Theft Auto V\GTA5.exe", @"Grand Theft Auto V Enhanced\GTA5_Enhanced.exe");
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var f in new[]
                 {
                     Path.Combine(pf, @"Epic Games\GTAV\GTA5.exe"), Path.Combine(pf, @"Epic Games\GTAVEnhanced\GTA5_Enhanced.exe"),
                     Path.Combine(pf, @"Rockstar Games\Grand Theft Auto V\GTA5.exe"), Path.Combine(pf, @"Rockstar Games\Grand Theft Auto V Enhanced\GTA5_Enhanced.exe"),
                 })
            if (File.Exists(f)) list.Add(f);
        string fivem = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"FiveM\FiveM.app\data\cache\subprocess");
        try
        {
            if (Directory.Exists(fivem))
                list.AddRange(Directory.EnumerateFiles(fivem, "*GTAProcess.exe"));
        }
        catch { }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private const string GpuHigh = "GpuPreference=2;";

    /// <summary>GPU de alto desempenho para cada .exe do GTA/FiveM + Modo de Jogo e HAGS do Windows ligados.</summary>
    private static Tweak GtaTweak(Tweak? gameMode, Tweak? hags) => new()
    {
        Id = "comp.gta", Weight = 3, NeedsRestart = hags != null,
        Title = "GTA V / FiveM: placa de vídeo dedicada, Modo de Jogo e HAGS",
        Description = "Marca o GTA V e o processo do jogo do FiveM como \"Alto desempenho\" nas Configurações gráficas do Windows (a GPU mais forte sempre) " +
                      "e garante o Modo de Jogo do Windows e o Agendamento de GPU acelerado por hardware ligados.",
        Effect = "Em notebook com duas placas, evita o jogo abrir na integrada (FPS muito menor). No desktop, o Modo de Jogo dá prioridade ao jogo " +
                 "e o HAGS reduz a latência de CPU→GPU. Abra o FiveM uma vez antes, para o tHg achar o processo dele. HAGS vale depois de reiniciar.",
        Read = () => GtaExes() is { Count: > 0 } ex && ex.All(e => ReadString(Registry.CurrentUser, DxPath, e) == GpuHigh)
                     && gameMode?.Read() != false && hags?.Read() != false,
        Write = (on, s) =>
        {
            var exes = GtaExes();
            if (exes.Count == 0) throw new InvalidOperationException("O GTA V e o FiveM não foram encontrados. No FiveM, entre num servidor uma vez e tente de novo.");
            foreach (var e in exes)
            {
                string bk = $"comp.gta@{e}";
                if (!s.TweakBackup.ContainsKey(bk)) s.TweakBackup[bk] = ReadString(Registry.CurrentUser, DxPath, e) ?? "<ausente>";
                using var k = Registry.CurrentUser.CreateSubKey(DxPath);
                if (on) k.SetValue(e, GpuHigh, RegistryValueKind.String);
                else if (s.TweakBackup[bk] is string prev && prev != "<ausente>" && prev != GpuHigh) k.SetValue(e, prev, RegistryValueKind.String);
                else k.DeleteValue(e, false);
            }
            if (!on) return;
            if (gameMode?.Read() == false) gameMode.Write(true, s);
            if (hags?.Read() == false) hags.Write(true, s);
        },
        Restore = s =>
        {
            foreach (var (bk, prev) in s.TweakBackup.Where(kv => kv.Key.StartsWith("comp.gta@")).ToList())
            {
                string exe = bk["comp.gta@".Length..];
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                using var k = Registry.CurrentUser.CreateSubKey(DxPath);
                if (prev is null or "<ausente>") k.DeleteValue(exe, false);
                else k.SetValue(exe, prev, RegistryValueKind.String);
            }
            // Modo de Jogo e HAGS voltam pela cópia deles (a mesma da página Ajustes).
            gameMode?.Restore(s);
            hags?.Restore(s);
        },
    };

    private static Tweak LolTweak() => MultiRegTweak("comp.lol", "League of Legends: sem Game DVR",
        "Desliga a gravação em segundo plano do Xbox Game Bar para a sua conta e, por política, para o PC inteiro (GameDVR_Enabled, AppCaptureEnabled e AllowGameDVR).",
        "O LoL é leve e sensível a qualquer captura rodando junto: some a queda de FPS nas lutas e o uso de disco da gravação contínua. " +
        "Vale para todos os jogos. O botão de captura do Game Bar para de funcionar.",
        weight: 3, values: new[]
        {
            new RegValue(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
            new RegValue(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1),
            new RegValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, null),
        });

    // ------------------------------------------------------------------ Módulo 4 · baixo nível

    private static readonly string[] BcdNames = { "useplatformclock", "disabledynamictick" };

    /// <summary>Elementos da entrada de boot atual. Os nomes são sempre em inglês; os valores podem vir traduzidos (Sim/Não).</summary>
    private static Dictionary<string, string>? BcdCurrent()
    {
        var (code, output) = SecurityAudit.Run("bcdedit.exe", "/enum {current}", 15_000);
        if (code != 0) return null;
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(output, @"^(\w+)\s+(\S.*?)\s*$", RegexOptions.Multiline)) d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }

    private static string BcdState(Dictionary<string, string> d, string name) =>
        d.GetValueOrDefault(name) is not string v ? "<ausente>" : v.ToLowerInvariant() is "yes" or "sim" or "on" or "true" or "1" ? "yes" : "no";

    private static void Bcd(string args)
    {
        var (code, output) = SecurityAudit.Run("bcdedit.exe", args, 15_000);
        if (code != 0) throw new InvalidOperationException($"O Windows recusou a mudança na inicialização (bcdedit {args}): {output.Trim()}");
    }

    private static void BcdApply(string name, string state)
    {
        if (state == "<ausente>") SecurityAudit.Run("bcdedit.exe", $"/deletevalue {{current}} {name}", 15_000); // já ausente: sem erro
        else Bcd($"/set {{current}} {name} {(state == "yes" ? "yes" : "no")}");
    }

    private static Tweak HpetTweak() => new()
    {
        Id = "comp.hpet", Weight = 0, NeedsRestart = true, LaptopCaution = true,
        Title = "Timer da plataforma: sem HPET forçado e sem tick dinâmico",
        Description = "Remove o useplatformclock (que força o HPET, um relógio mais lento de ler) e liga o disabledynamictick (o Windows para de " +
                      "pular os ticks do timer para economizar energia). Feito na configuração de inicialização (bcdedit); o valor de antes fica guardado.",
        Effect = "Em alguns PCs reduz microengasgos e a latência de DPC; em outros não muda nada, então meça com o Teste de engasgos antes e depois. " +
                 "Gasta um pouco mais de energia em repouso. Se o BitLocker estiver ligado, tenha a chave de recuperação à mão. Precisa reiniciar.",
        Read = () => BcdCurrent() is { } d ? BcdState(d, "useplatformclock") != "yes" && BcdState(d, "disabledynamictick") == "yes" : null,
        Write = (on, s) =>
        {
            var d = BcdCurrent() ?? throw new InvalidOperationException("Não foi possível ler a configuração de inicialização (bcdedit).");
            foreach (var n in BcdNames)
                if (!s.TweakBackup.ContainsKey($"comp.hpet.{n}")) s.TweakBackup[$"comp.hpet.{n}"] = BcdState(d, n);
            s.Save();
            if (on)
            {
                BcdApply("useplatformclock", "<ausente>");
                Bcd("/set {current} disabledynamictick yes");
            }
            else
                foreach (var n in BcdNames)
                {
                    string prev = s.TweakBackup[$"comp.hpet.{n}"] ?? "<ausente>";
                    // Sem um valor de antes "diferente", desligar volta ao padrão do Windows (os dois ausentes).
                    BcdApply(n, n == "disabledynamictick" && prev == "yes" ? "<ausente>" : prev);
                }
        },
        Restore = s =>
        {
            foreach (var n in BcdNames)
                if (s.TweakBackup.GetValueOrDefault($"comp.hpet.{n}") is string prev && prev is "yes" or "no" or "<ausente>") BcdApply(n, prev);
        },
    };

    // ------------------------------------------------------------------ Módulo 4 · placa de vídeo AMD

    private const string GpuClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Subchaves da classe de vídeo (0000, 0001…) com o driver da AMD instalado (placas Radeon e gráficos dos Ryzen).</summary>
    private static List<string> AmdGpuKeys()
    {
        var list = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(GpuClass);
            if (cls == null) return list;
            foreach (var name in cls.GetSubKeyNames())
            {
                if (name.Length != 4 || !name.All(char.IsDigit)) continue; // "Properties" e afins
                try
                {
                    using var k = cls.OpenSubKey(name);
                    string id = k?.GetValue("MatchingDeviceId") as string ?? "";
                    string provider = k?.GetValue("ProviderName") as string ?? "";
                    if (id.StartsWith(@"pci\ven_1002", StringComparison.OrdinalIgnoreCase) || provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
                        list.Add($@"{GpuClass}\{name}");
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>Cópia de um valor do registro como texto: "&lt;ausente&gt;", "d:123" (DWORD), "b:3200" (binário) ou "s:texto".</summary>
    private static string EncodeValue(RegistryKey k, string name) => k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
    {
        null => "<ausente>",
        int i => $"d:{i}",
        byte[] b => $"b:{Convert.ToHexString(b)}",
        var o => $"s:{o}",
    };

    private static void PutValue(RegistryKey k, string name, string? backup)
    {
        switch (backup)
        {
            case ['d', ':', .. var d] when int.TryParse(d, out int v): k.SetValue(name, v, RegistryValueKind.DWord); break;
            case ['b', ':', .. var h]: k.SetValue(name, Convert.FromHexString(h), RegistryValueKind.Binary); break;
            case ['s', ':', .. var t]: k.SetValue(name, t, RegistryValueKind.String); break;
            default: k.DeleteValue(name, false); break;
        }
    }

    private static bool SameValue(object? a, object b) => (a, b) switch
    {
        (int x, int y) => x == y,
        (byte[] x, byte[] y) => x.AsSpan().SequenceEqual(y),
        _ => false,
    };

    /// <summary>
    /// Um valor nas chaves do driver de vídeo (classe {4d36e968…}). O valor de antes de cada placa fica guardado; desligar
    /// devolve esse valor, ou apaga (padrão do driver) se ele já era o mesmo que o tHg grava.
    /// </summary>
    private static Tweak GpuClassTweak(string id, string title, string desc, string effect, Func<List<string>> keys,
                                       string name, object on, RegistryValueKind kind, int weight, bool needsRestart, bool laptopCaution = false) => new()
    {
        Id = id, Weight = weight, NeedsRestart = needsRestart, LaptopCaution = laptopCaution, Available = () => keys().Count > 0,
        Title = title, Description = desc, Effect = effect,
        Read = () =>
        {
            var ks = keys();
            if (ks.Count == 0) return null;
            return ks.All(p => { using var k = Registry.LocalMachine.OpenSubKey(p); return SameValue(k?.GetValue(name), on); });
        },
        Write = (enable, s) =>
        {
            var ks = keys();
            if (ks.Count == 0) throw new InvalidOperationException("Nenhuma placa de vídeo AMD com o driver instalado foi encontrada.");
            foreach (var p in ks)
            {
                using var k = Registry.LocalMachine.OpenSubKey(p, writable: true);
                if (k == null) continue;
                string bk = $"{id}@{p}";
                if (!s.TweakBackup.ContainsKey(bk)) { s.TweakBackup[bk] = EncodeValue(k, name); s.Save(); }
                if (enable) k.SetValue(name, on, kind);
                else
                {
                    string prev = s.TweakBackup[bk] ?? "<ausente>";
                    bool wasSame = prev != "<ausente>" && SameValue(DecodeForCompare(prev), on);
                    PutValue(k, name, wasSame ? null : prev);
                }
            }
        },
        Restore = s =>
        {
            foreach (var (bk, prev) in s.TweakBackup.Where(kv => kv.Key.StartsWith(id + "@")).ToList())
            {
                string p = bk[(id.Length + 1)..];
                // O caminho vem da cópia: só chaves da classe de vídeo.
                if (!p.StartsWith(GpuClass + @"\", StringComparison.OrdinalIgnoreCase)) continue;
                using var k = Registry.LocalMachine.OpenSubKey(p, writable: true);
                if (k != null) PutValue(k, name, prev);
            }
        },
    };

    private static object? DecodeForCompare(string backup) => backup switch
    {
        ['d', ':', .. var d] when int.TryParse(d, out int v) => v,
        ['b', ':', .. var h] => Convert.FromHexString(h),
        _ => null,
    };

    private static Tweak AmdUlpsTweak() => GpuClassTweak("comp.ulps", "AMD: desligar o ULPS (economia profunda da placa)",
        "Grava EnableUlps = 0 na chave do driver AMD: a placa deixa de entrar no estado de energia ultrabaixa (Ultra Low Power State) quando fica ociosa.",
        "Evita quedas bruscas de clock e engasgos ao voltar do ocioso (Alt+Tab, telas de carregamento, segundo monitor). A placa gasta um pouco mais " +
        "parada. Em notebook, aumenta o consumo na bateria. O AMD Software pode religar ao reinstalar o driver. Precisa reiniciar.",
        AmdGpuKeys, "EnableUlps", 0, RegistryValueKind.DWord, weight: 0, needsRestart: true, laptopCaution: true);

    /// <summary>Chaves "UMD" (driver de modo usuário) das placas AMD, onde o AMD Software grava o modo do cache de shaders.</summary>
    private static List<string> AmdUmdKeys() => AmdGpuKeys().Select(k => k + @"\UMD").Where(k =>
    {
        try { using var u = Registry.LocalMachine.OpenSubKey(k); return u != null; } catch { return false; }
    }).ToList();

    private static Tweak AmdShaderCacheTweak() => GpuClassTweak("comp.amdshader", "AMD: cache de shaders sempre ligado",
        "Muda o Cache de Shaders do AMD Software de \"Otimizado pela AMD\" para \"Sempre ligado\" (ShaderCache = 32 00 na chave UMD do driver).",
        "Todos os jogos guardam os shaders compilados no disco, não só os que a AMD escolheu: menos engasgos de compilação na segunda vez " +
        "que uma área ou efeito aparece. Usa um pouco mais de espaço em disco. Vale nos jogos abertos depois.",
        AmdUmdKeys, "ShaderCache", new byte[] { 0x32, 0x00 }, RegistryValueKind.Binary, weight: 0, needsRestart: false);

    private static Tweak MemoryTweak() => MultiRegTweak("comp.memory", "Memória: núcleo do Windows sempre na RAM",
        "Liga o DisablePagingExecutive (os drivers e o núcleo do Windows não vão para o arquivo de paginação) e garante o LargeSystemCache em 0, " +
        "o modo de estação de trabalho (1 é o modo servidor, que reserva RAM para cache de arquivos e tira dos jogos).",
        "Evita engasgos quando o jogo volta de segundo plano ou a RAM está quase cheia, porque o Windows não precisa buscar o próprio código no disco. " +
        "Ganho maior com 16 GB ou mais; com 8 GB ou menos, pode sobrar menos RAM para o jogo. Precisa reiniciar.",
        weight: 1, needsRestart: true, values: new[]
        {
            new RegValue(Registry.LocalMachine, MemMgmt, "DisablePagingExecutive", 1, 0),
            new RegValue(Registry.LocalMachine, MemMgmt, "LargeSystemCache", 0, 0),
        });
}
