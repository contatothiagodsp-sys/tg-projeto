using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ThgOptimization;

/// <summary>
/// SHIELD: diagnóstico de segurança do Windows para quem joga. Antivírus e assinaturas, firewall, SmartScreen,
/// UAC, Windows Update, Secure Boot e TPM (exigidos por anti-cheats como Vanguard e Javelin), criptografia do disco,
/// Integridade de Memória, proteção do LSA, SMBv1, acesso remoto, AutoRun de pendrive, conta Convidado, Wi-Fi aberto,
/// pastas compartilhadas e sinais de malware comuns entre gamers (cheats e "crackers" em Downloads, programas
/// iniciando da pasta Temp). Cada correção automática guarda o valor anterior em TweakBackup com o prefixo "sec.",
/// e "Desfazer" volta tudo.
/// </summary>
public static class SecurityAudit
{
    private const string Area1 = "PROTEÇÃO", Area2 = "SISTEMA", Area3 = "REDE", Area4 = "CONTAS E JOGOS";

    public static List<GamerCheck> Run()
    {
        var list = new List<GamerCheck>();
        bool laptop = GamerAudit.Power().HasBattery;

        void Add(string id, string area, string title, Func<GamerCheck?> build)
        {
            try { if (build() is { } c) list.Add(c); }
            catch (Exception ex)
            {
                list.Add(new GamerCheck { Id = id, Area = area, Title = title, Weight = 0, Level = CheckLevel.Info,
                                          Found = "Não foi possível verificar", Advice = ex.Message });
            }
        }

        var av = SecurityCenter("AntiVirusProduct");
        var defender = DefenderStatus();
        bool defenderIsAv = defender is { AntivirusEnabled: true };

        // ---------------- PROTEÇÃO ----------------
        Add("sec.av", Area1, "Antivírus ativo", () => CheckAntivirus(av, defender));
        if (defenderIsAv)
        {
            Add("sec.sigs", Area1, "Assinaturas de vírus atualizadas", () => CheckSignatures(defender!));
            Add("sec.tamper", Area1, "Proteção contra adulteração", () => CheckTamper(defender!));
            Add("sec.pua", Area1, "Bloqueio de apps potencialmente indesejados", CheckPua);
            Add("sec.cfa", Area1, "Proteção contra ransomware (pastas controladas)", CheckControlledFolders);
        }
        Add("sec.smartscreen", Area1, "SmartScreen para apps e arquivos", CheckSmartScreen);
        Add("sec.downloads", Area4, "Cheats e programas de risco em Downloads", CheckDownloads);
        Add("sec.startup", Area4, "Programas iniciando de pastas suspeitas", CheckSuspiciousStartup);

        // ---------------- SISTEMA ----------------
        Add("sec.firewall", Area2, "Firewall ligado em todas as redes", CheckFirewall);
        Add("sec.uac", Area2, "Controle de Conta de Usuário (UAC)", CheckUac);
        Add("sec.update", Area2, "Windows Update em dia", CheckWindowsUpdate);
        Add("sec.secureboot", Area2, "Secure Boot", CheckSecureBoot);
        Add("sec.tpm", Area2, "TPM 2.0", CheckTpm);
        Add("sec.hvci", Area2, "Integridade de Memória", CheckHvci);
        Add("sec.lsa", Area2, "Proteção das senhas do Windows (LSA)", CheckLsa);
        Add("sec.bitlocker", Area2, "Criptografia do disco", () => CheckBitLocker(laptop));
        Add("sec.restore", Area2, "Proteção do sistema (pontos de restauração)", CheckSystemRestore);
        Add("sec.autorun", Area2, "AutoRun de pendrives e discos", CheckAutorun);

        // ---------------- REDE ----------------
        Add("sec.smb1", Area3, "Protocolo SMBv1 (usado pelo WannaCry)", CheckSmb1);
        Add("sec.rdp", Area3, "Área de Trabalho Remota", CheckRdp);
        Add("sec.assist", Area3, "Assistência Remota", CheckRemoteAssist);
        Add("sec.wifi", Area3, "Segurança do Wi-Fi conectado", CheckWifi);
        Add("sec.shares", Area3, "Pastas compartilhadas na rede", CheckShares);

        // ---------------- CONTAS ----------------
        Add("sec.guest", Area4, "Conta Convidado", CheckGuest);
        Add("sec.2fa", Area4, "Contas de jogos com verificação em duas etapas", Check2fa);

        return list.OrderBy(c => c.Level switch { CheckLevel.Bad => 0, CheckLevel.Warn => 1, CheckLevel.Info => 3, _ => 2 })
                   .ThenByDescending(c => c.Weight).ToList();
    }

    // ================================================================== antivírus

    private sealed record AvProduct(string Name, bool On, bool UpToDate);

    /// <summary>Produtos registrados na Central de Segurança (productState: dígitos 3-4 = ligado, 5-6 = atualizado).</summary>
    private static List<AvProduct> SecurityCenter(string cls)
    {
        var list = new List<AvProduct>();
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\SecurityCenter2"),
                                                       new ObjectQuery($"SELECT displayName, productState FROM {cls}"));
            foreach (ManagementObject o in s.Get())
            {
                string hex = Convert.ToUInt32(o["productState"] ?? 0u).ToString("X6");
                list.Add(new AvProduct(o["displayName"]?.ToString() ?? "?", hex.Substring(2, 2) is "10" or "11", hex.Substring(4, 2) == "00"));
            }
        }
        catch { }
        return list;
    }

    private sealed record Defender(bool AntivirusEnabled, bool RealTime, int SignatureAge, bool Tamper);

    private static Defender? DefenderStatus()
    {
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\Microsoft\Windows\Defender"),
                new ObjectQuery("SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge, IsTamperProtected FROM MSFT_MpComputerStatus"));
            foreach (ManagementObject o in s.Get())
                return new Defender(o["AntivirusEnabled"] is true, o["RealTimeProtectionEnabled"] is true,
                                    Convert.ToInt32(o["AntivirusSignatureAge"] ?? 0), o["IsTamperProtected"] is true);
        }
        catch { }
        return null;
    }

    private static GamerCheck CheckAntivirus(List<AvProduct> av, Defender? d)
    {
        var active = av.Where(a => a.On).ToList();
        bool realTimeOff = d is { AntivirusEnabled: true, RealTime: false };
        var c = new GamerCheck
        {
            Id = "sec.av", Area = Area1, Title = "Antivírus ativo", Weight = 10,
            Found = active.Count > 0 ? string.Join(", ", active.Select(a => a.Name)) + (realTimeOff ? " · proteção em tempo real DESLIGADA" : "")
                  : av.Count > 0 ? $"{string.Join(", ", av.Select(a => a.Name))} instalado, mas desligado" : "Nenhum antivírus encontrado",
        };
        if (active.Count == 0 || realTimeOff)
        {
            c.Level = CheckLevel.Bad;
            c.Advice = "Sem proteção em tempo real, qualquer \"cheat\", mod ou instalador pirata roda livre. Os ladrões de conta de Steam e " +
                       "Discord se espalham exatamente assim. O Microsoft Defender é gratuito, leve e suficiente para a maioria.";
            if (realTimeOff)
            {
                c.Fix = FixKind.Auto; c.FixLabel = "LIGAR PROTEÇÃO";
                c.AutoFix = _ => Ps("Set-MpPreference -DisableRealtimeMonitoring $false") is var e && e.Length > 0 ? e : "";
            }
            else { c.Fix = FixKind.Settings; c.FixLabel = "ABRIR SEGURANÇA"; c.FixTarget = "windowsdefender:"; }
        }
        else
        {
            c.Level = CheckLevel.Ok;
            c.Advice = active.Count > 1 ? "Mais de um antivírus ativo pode deixar o PC lento e causar conflitos. Mantenha só um." : "Protegido em tempo real.";
            if (active.Count > 1) c.Level = CheckLevel.Warn;
        }
        return c;
    }

    private static GamerCheck CheckSignatures(Defender d) => new()
    {
        Id = "sec.sigs", Area = Area1, Title = "Assinaturas de vírus atualizadas", Weight = 6,
        Found = d.SignatureAge == 0 ? "Atualizadas hoje" : $"Última atualização há {d.SignatureAge} dia(s)",
        Level = d.SignatureAge > 7 ? CheckLevel.Bad : d.SignatureAge > 2 ? CheckLevel.Warn : CheckLevel.Ok,
        Advice = d.SignatureAge > 2 ? "O Defender só reconhece ameaças que já estão na lista. Com a lista velha, malware novo passa direto."
                                    : "O Defender conhece as ameaças mais recentes.",
        Fix = d.SignatureAge > 2 ? FixKind.Auto : FixKind.None, FixLabel = "ATUALIZAR AGORA",
        AutoFix = _ => Ps("Update-MpSignature -ErrorAction Stop", 180_000),
    };

    private static GamerCheck CheckTamper(Defender d) => new()
    {
        Id = "sec.tamper", Area = Area1, Title = "Proteção contra adulteração", Weight = 5,
        Found = d.Tamper ? "Ligada" : "Desligada",
        Level = d.Tamper ? CheckLevel.Ok : CheckLevel.Warn,
        Advice = d.Tamper ? "Malware não consegue desligar o Defender sozinho."
                          : "Sem ela, um programa malicioso pode desligar o Defender antes de agir. Só é ativada pela tela do Windows (por segurança, nenhum app consegue).",
        Fix = d.Tamper ? FixKind.None : FixKind.Settings, FixLabel = "ABRIR CONFIGURAÇÃO", FixTarget = "windowsdefender://threatsettings",
    };

    private static GamerCheck CheckPua()
    {
        int pua = MpPreference("PUAProtection");
        return new GamerCheck
        {
            Id = "sec.pua", Area = Area1, Title = "Bloqueio de apps potencialmente indesejados", Weight = 5,
            Found = pua == 1 ? "Bloqueando" : pua == 2 ? "Só auditando" : "Desligado",
            Level = pua == 1 ? CheckLevel.Ok : CheckLevel.Warn,
            Advice = pua == 1 ? "Mineradores, adwares e instaladores com \"brindes\" são bloqueados."
                              : "Bloqueia mineradores de criptomoeda escondidos (comuns em cheats e jogos piratas, e que roubam FPS), adware e instaladores com programas extras.",
            Fix = pua == 1 ? FixKind.None : FixKind.Auto, FixLabel = "LIGAR",
            AutoFix = s =>
            {
                s.TweakBackup.TryAdd("sec.pua", pua.ToString());
                return Ps("Set-MpPreference -PUAProtection Enabled");
            },
        };
    }

    private static GamerCheck CheckControlledFolders()
    {
        int cfa = MpPreference("EnableControlledFolderAccess");
        return new GamerCheck
        {
            Id = "sec.cfa", Area = Area1, Title = "Proteção contra ransomware (pastas controladas)", Weight = 0,
            Level = CheckLevel.Info, Found = cfa == 1 ? "Ligada" : "Desligada",
            Advice = "Impede programas desconhecidos de alterar Documentos, Imagens e Área de Trabalho. Protege muito contra ransomware, " +
                     "mas alguns jogos salvam em Documentos e são bloqueados até você liberar cada um. Por isso fica a seu critério.",
            Fix = FixKind.Settings, FixLabel = "VER NO WINDOWS", FixTarget = "windowsdefender://ransomwareprotection",
        };
    }

    private static int MpPreference(string prop)
    {
        using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\Microsoft\Windows\Defender"),
                                                   new ObjectQuery($"SELECT {prop} FROM MSFT_MpPreference"));
        foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o[prop] ?? 0);
        return 0;
    }

    private static GamerCheck CheckSmartScreen()
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer";
        string? v = Registry.LocalMachine.OpenSubKey(path)?.GetValue("SmartScreenEnabled") as string;
        bool off = string.Equals(v, "Off", StringComparison.OrdinalIgnoreCase);
        return new GamerCheck
        {
            Id = "sec.smartscreen", Area = Area1, Title = "SmartScreen para apps e arquivos", Weight = 6,
            Found = off ? "Desligado" : "Ligado",
            Level = off ? CheckLevel.Bad : CheckLevel.Ok,
            Advice = off ? "O SmartScreen avisa antes de abrir programas baixados que pouca gente usa ou que já foram denunciados como golpe."
                         : "Programas baixados de origem desconhecida pedem confirmação antes de abrir.",
            Fix = off ? FixKind.Auto : FixKind.None, FixLabel = "LIGAR",
            AutoFix = s => SetReg(s, "sec.smartscreen", Registry.LocalMachine, path, "SmartScreenEnabled", "Warn"),
        };
    }

    /// <summary>Nomes típicos de cheats, crackers e iscas de golpe que circulam entre gamers.</summary>
    private static readonly Regex RiskyName = new(
        @"cheat|hack|aimbot|wallhack|esp[_ -]?menu|injector|inject|spoofer|unlocker|crack|keygen|activator|kms[_ -]?pico|loader|executor|" +
        @"free[_ -]?(robux|vbucks|v-bucks|nitro|skins)|robux|nitro[_ -]?gen|skin[_ -]?changer|mod[_ -]?menu|trainer",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static GamerCheck CheckDownloads()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe", ".scr", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".dll", ".zip", ".rar", ".7z" };
        var found = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                       .Where(f => exts.Contains(Path.GetExtension(f)) && RiskyName.IsMatch(Path.GetFileNameWithoutExtension(f)))
                       .Take(30).ToList()
            : new List<string>();
        var c = new GamerCheck
        {
            Id = "sec.downloads", Area = Area4, Title = "Cheats e programas de risco em Downloads", Weight = 6,
            Found = found.Count == 0 ? "Nada suspeito pelo nome" : $"{found.Count} arquivo(s): " + string.Join(", ", found.Take(5).Select(Path.GetFileName)) +
                                                                  (found.Count > 5 ? "…" : ""),
            Level = found.Count == 0 ? CheckLevel.Ok : CheckLevel.Warn,
            Advice = found.Count == 0
                ? "Nenhum arquivo com nome típico de cheat, crack ou gerador de itens."
                : "Cheats, \"geradores\" de Robux/Nitro/skins, spoofers e cracks são a principal porta de entrada de ladrões de conta (Steam, Discord, " +
                  "navegador) entre gamers, e cheats ainda podem dar banimento. Verifique com o Defender e apague o que não reconhecer.",
            Fix = found.Count == 0 ? FixKind.None : FixKind.Auto, FixLabel = "VERIFICAR COM O DEFENDER",
            TurboSkip = "Verificação demorada: rode pelo botão do item.",
        };
        c.AutoFix = _ =>
        {
            string mp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
            if (!File.Exists(mp)) return "Microsoft Defender não encontrado (outro antivírus em uso: verifique a pasta Downloads com ele).";
            var (code, output) = Run(mp, $"-Scan -ScanType 3 -File \"{dir}\" -DisableRemediation", 600_000);
            // 0 = nada encontrado, 2 = ameaças encontradas (com -DisableRemediation nada é apagado: o usuário decide no Defender)
            return code switch
            {
                0 => "",
                2 => "O Defender encontrou ameaças. Abra Segurança do Windows → Proteção contra vírus e ameaças para ver e remover.",
                _ => $"A verificação terminou com o código {code}. {Tail(output)}",
            };
        };
        return c;
    }

    private static GamerCheck CheckSuspiciousStartup()
    {
        string temp = Path.GetTempPath().TrimEnd('\\');
        var bad = StartupManager.List()
            .Where(e => e.Enabled && (e.Command.Contains(temp, StringComparison.OrdinalIgnoreCase)
                                      || Regex.IsMatch(e.Command, @"\\(Temp|AppData\\Local\\Temp)\\", RegexOptions.IgnoreCase)
                                      || Regex.IsMatch(e.Command, @"(powershell|pwsh|mshta|wscript|cscript).*(-enc|-e |http|frombase64)", RegexOptions.IgnoreCase)))
            .ToList();
        return new GamerCheck
        {
            Id = "sec.startup", Area = Area4, Title = "Programas iniciando de pastas suspeitas", Weight = 7,
            Found = bad.Count == 0 ? "Nenhum" : string.Join(", ", bad.Select(e => e.Name)),
            Level = bad.Count == 0 ? CheckLevel.Ok : CheckLevel.Bad,
            Advice = bad.Count == 0 ? "Nada abrindo com o Windows a partir da pasta Temp ou de scripts disfarçados."
                : "Programas legítimos não abrem com o Windows a partir da pasta Temp nem por PowerShell codificado: é o comportamento típico de " +
                  "ladrões de senha e mineradores. Desative na página Inicialização e verifique o PC com o antivírus.",
            Fix = bad.Count == 0 ? FixKind.None : FixKind.Page, FixLabel = "REVISAR", FixTarget = "PageStartup",
        };
    }

    // ================================================================== sistema

    private static readonly (string Key, string Netsh, string Label)[] FirewallProfiles =
    {
        ("DomainProfile", "domainprofile", "Domínio"), ("StandardProfile", "privateprofile", "Privada"), ("PublicProfile", "publicprofile", "Pública"),
    };

    private static GamerCheck CheckFirewall()
    {
        const string root = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\";
        var off = FirewallProfiles.Where(p => Registry.LocalMachine.OpenSubKey(root + p.Key)?.GetValue("EnableFirewall") is 0).ToList();
        var third = SecurityCenter("FirewallProduct").Where(f => f.On).ToList();
        bool ok = off.Count == 0 || third.Count > 0;
        return new GamerCheck
        {
            Id = "sec.firewall", Area = Area2, Title = "Firewall ligado em todas as redes", Weight = 9,
            Found = third.Count > 0 ? $"{string.Join(", ", third.Select(f => f.Name))} ativo"
                  : off.Count == 0 ? "Ligado (Domínio, Privada e Pública)" : $"Desligado em: {string.Join(", ", off.Select(p => p.Label))}",
            Level = ok ? CheckLevel.Ok : CheckLevel.Bad,
            Advice = ok ? "Conexões não pedidas vindas da rede são bloqueadas. Jogos pedem liberação na primeira vez e funcionam normalmente."
                        : "Desligar o firewall não melhora ping nem FPS. Ele só bloqueia conexões que ninguém pediu, que é exatamente o que um invasor tenta.",
            Fix = ok ? FixKind.None : FixKind.Auto, FixLabel = "LIGAR FIREWALL",
            AutoFix = s =>
            {
                foreach (var p in off) s.TweakBackup.TryAdd($"sec.firewall.{p.Netsh}", "off");
                var (code, output) = Run("netsh.exe", "advfirewall set allprofiles state on", 30_000);
                return code == 0 ? "" : Tail(output);
            },
        };
    }

    private static GamerCheck CheckUac()
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        using var k = Registry.LocalMachine.OpenSubKey(path);
        int lua = k?.GetValue("EnableLUA") is int l ? l : 1;
        int prompt = k?.GetValue("ConsentPromptBehaviorAdmin") is int p ? p : 5;
        bool ok = lua == 1 && prompt != 0;
        return new GamerCheck
        {
            Id = "sec.uac", Area = Area2, Title = "Controle de Conta de Usuário (UAC)", Weight = 8, NeedsRestart = lua == 0,
            Found = lua == 0 ? "Desativado" : prompt == 0 ? "Nunca notificar" : "Ativo",
            Level = lua == 0 ? CheckLevel.Bad : prompt == 0 ? CheckLevel.Warn : CheckLevel.Ok,
            Advice = ok ? "Programas precisam da sua confirmação para mexer no sistema."
                        : "Sem o aviso do UAC, qualquer programa que você abre ganha poder de administrador em silêncio, inclusive para instalar " +
                          "drivers maliciosos. Não melhora desempenho.",
            Fix = ok ? FixKind.None : FixKind.Auto, FixLabel = "ATIVAR",
            AutoFix = s =>
            {
                string e = SetReg(s, "sec.uac.lua", Registry.LocalMachine, path, "EnableLUA", 1);
                return e.Length > 0 ? e : SetReg(s, "sec.uac.prompt", Registry.LocalMachine, path, "ConsentPromptBehaviorAdmin", 5);
            },
        };
    }

    private static GamerCheck CheckWindowsUpdate()
    {
        using var ux = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");
        bool paused = ux?.GetValue("PauseUpdatesExpiryTime") is string exp
                      && DateTime.TryParse(exp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var until) && until > DateTime.UtcNow;
        string last = LastUpdateInstall() ?? Ps("$d=(New-Object -ComObject Microsoft.Update.AutoUpdate).Results.LastInstallationSuccessDate; " +
                                                "if ($d) { $d.ToString('yyyy-MM-dd') }", 30_000, returnOutput: true).Trim();
        int days = DateTime.TryParse(last, out var dt) && dt.Year > 2000 ? (int)(DateTime.Now - dt).TotalDays : -1;
        bool rebootPending = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") != null;
        var level = paused || days > 60 ? CheckLevel.Bad : days > 35 || rebootPending ? CheckLevel.Warn : days < 0 ? CheckLevel.Info : CheckLevel.Ok;
        return new GamerCheck
        {
            Id = "sec.update", Area = Area2, Title = "Windows Update em dia", Weight = 8,
            Found = (days >= 0 ? $"Última atualização instalada há {days} dia(s)" : "Data da última atualização não informada") +
                    (paused ? " · atualizações PAUSADAS" : "") + (rebootPending ? " · reinício pendente" : ""),
            Level = level,
            Advice = level == CheckLevel.Ok ? "Correções de segurança recentes instaladas."
                : "As atualizações mensais fecham falhas que já estão sendo exploradas. O Modo de Jogo já impede que o Windows atualize ou reinicie " +
                  "no meio da partida, então não é preciso pausar." + (rebootPending ? " Há uma atualização esperando o PC reiniciar." : ""),
            Fix = level == CheckLevel.Ok ? FixKind.None : FixKind.Settings, FixLabel = "ABRIR WINDOWS UPDATE", FixTarget = "ms-settings:windowsupdate",
        };
    }

    /// <summary>Data da última instalação do Windows Update pelo mesmo objeto COM, sem abrir o PowerShell. Null = COM indisponível.</summary>
    private static string? LastUpdateInstall()
    {
        try
        {
            if (Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate") is not Type t || Activator.CreateInstance(t) is not object au) return null;
            try
            {
                object? d = ((dynamic)au).Results.LastInstallationSuccessDate;
                return d is DateTime dt ? dt.ToString("yyyy-MM-dd") : "";
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(au); }
        }
        catch { return null; }
    }

    private static GamerCheck? CheckSecureBoot()
    {
        if (!GamerAudit.IsUefi)
            return new GamerCheck
            {
                Id = "sec.secureboot", Area = Area2, Title = "Secure Boot", Weight = 6, Level = CheckLevel.Warn,
                Found = "Windows iniciado em modo legado (BIOS/CSM)",
                Advice = "Sem UEFI não há Secure Boot. Além da proteção contra rootkits, jogos como Valorant (no Windows 11) e Battlefield 6 exigem Secure Boot " +
                         "para o anti-cheat. Converter exige o MBR2GPT da Microsoft e backup antes.",
                Fix = FixKind.Bios, FixLabel = "COMO FAZER",
                FixTarget = "1. Faça backup.\n2. No Prompt de Comando como administrador: mbr2gpt /validate /allowFullOS e, se passar, mbr2gpt /convert /allowFullOS.\n" +
                            "3. Na BIOS: Boot → CSM: Disabled, Boot Mode: UEFI, e então Secure Boot: Enabled (Windows UEFI mode).",
            };
        bool on = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State")?.GetValue("UEFISecureBootEnabled") is 1;
        return new GamerCheck
        {
            Id = "sec.secureboot", Area = Area2, Title = "Secure Boot", Weight = 6,
            Found = on ? "Ligado" : "Desligado", Level = on ? CheckLevel.Ok : CheckLevel.Warn,
            Advice = on ? "Só carregadores de boot assinados ligam o PC: protege contra rootkits e atende aos anti-cheats que exigem."
                        : "Impede rootkits de carregar antes do Windows. Também é exigido por anti-cheats como o Vanguard (Valorant, no Windows 11) " +
                          "e o Javelin (Battlefield 6): sem ele, esses jogos nem abrem.",
            Fix = on ? FixKind.None : FixKind.Bios, FixLabel = "GUIA DA BIOS", FixTarget = on ? null : BiosSecurityGuide("secureboot"),
        };
    }

    private static GamerCheck? CheckTpm()
    {
        bool present = false, enabled = false; string spec = "";
        using (var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm"),
                                                    new ObjectQuery("SELECT IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm")))
            foreach (ManagementObject o in s.Get())
            {
                present = true; enabled = o["IsEnabled_InitialValue"] is true;
                spec = (o["SpecVersion"]?.ToString() ?? "").Split(',')[0].Trim();
            }
        bool v2 = spec.StartsWith("2");
        bool ok = present && enabled && v2;
        return new GamerCheck
        {
            Id = "sec.tpm", Area = Area2, Title = "TPM 2.0", Weight = 4,
            Found = !present ? "Não encontrado (desligado na BIOS?)" : $"TPM {spec}{(enabled ? "" : " desativado")}",
            Level = ok ? CheckLevel.Ok : CheckLevel.Warn,
            Advice = ok ? "Guarda as chaves de criptografia e do Windows Hello em hardware; requisito do Windows 11 e de alguns anti-cheats."
                        : "Quase todo PC desde 2017 tem TPM embutido no processador (AMD fTPM / Intel PTT), mas ele pode vir desligado na BIOS. " +
                          "É requisito do Windows 11, do BitLocker e de anti-cheats como o Vanguard.",
            Fix = ok ? FixKind.None : FixKind.Bios, FixLabel = "GUIA DA BIOS", FixTarget = ok ? null : BiosSecurityGuide("tpm"),
        };
    }

    private static GamerCheck? CheckHvci()
    {
        bool hvci = false;
        using (var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\Microsoft\Windows\DeviceGuard"),
                                                    new ObjectQuery("SELECT SecurityServicesRunning FROM Win32_DeviceGuard")))
            foreach (ManagementObject o in s.Get()) hvci = o["SecurityServicesRunning"] is uint[] r && r.Contains(2u);
        return new GamerCheck
        {
            Id = "sec.hvci", Area = Area2, Title = "Integridade de Memória", Weight = 4,
            Found = hvci ? "Ligada" : "Desligada", Level = hvci ? CheckLevel.Ok : CheckLevel.Warn,
            Advice = hvci ? "Drivers maliciosos ou vulneráveis não conseguem injetar código no núcleo do Windows."
                          : "Bloqueia drivers maliciosos ou vulneráveis, uma técnica usada por ransomware e por cheats de kernel. Em CPUs a partir do " +
                            "Intel 8ª geração / Ryzen 2000 custa pouco FPS. É a troca entre segurança e desempenho que o TURBO também mostra: a decisão é sua.",
            Fix = hvci ? FixKind.None : FixKind.Settings, FixLabel = "ABRIR CONFIGURAÇÃO", FixTarget = "windowsdefender://coreisolation",
        };
    }

    // ================================================================== anti-cheats de kernel × VBS/HVCI

    /// <summary>
    /// O que os anti-cheats de kernel conferem: Secure Boot, TPM 2.0 e o estado do VBS (segurança baseada em
    /// virtualização) e da Integridade de Memória (HVCI). VbsStatus: 0 desligado, 1 ligado sem rodar, 2 rodando.
    /// </summary>
    public sealed record AntiCheatStatus(bool Uefi, bool SecureBoot, bool TpmPresent, bool TpmEnabled, string TpmSpec, int VbsStatus, bool Hvci)
    {
        public bool Tpm20 => TpmPresent && TpmEnabled && TpmSpec.StartsWith("2");
        /// <summary>Atende ao que Vanguard (Valorant no Windows 11), Javelin (Battlefield 6) e afins exigem para abrir.</summary>
        public bool KernelAcReady => SecureBoot && Tpm20;
    }

    public static AntiCheatStatus AntiCheat()
    {
        bool uefi = GamerAudit.IsUefi;
        bool sb = false;
        try { sb = uefi && Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State")?.GetValue("UEFISecureBootEnabled") is 1; } catch { }
        bool tpm = false, tpmOn = false; string spec = "";
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm"),
                                                       new ObjectQuery("SELECT IsEnabled_InitialValue, SpecVersion FROM Win32_Tpm"));
            foreach (ManagementObject o in s.Get())
            {
                tpm = true; tpmOn = o["IsEnabled_InitialValue"] is true;
                spec = (o["SpecVersion"]?.ToString() ?? "").Split(',')[0].Trim();
            }
        }
        catch { }
        int vbs = 0; bool hvci = false;
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\Microsoft\Windows\DeviceGuard"),
                                                       new ObjectQuery("SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard"));
            foreach (ManagementObject o in s.Get())
            {
                vbs = o["VirtualizationBasedSecurityStatus"] is uint v ? (int)v : 0;
                hvci = o["SecurityServicesRunning"] is uint[] r && r.Contains(2u);
            }
        }
        catch { }
        return new AntiCheatStatus(uefi, sb, tpm, tpmOn, spec, vbs, hvci);
    }

    private static GamerCheck CheckLsa()
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\Lsa";
        bool on = Registry.LocalMachine.OpenSubKey(path)?.GetValue("RunAsPPL") is 1 or 2;
        return new GamerCheck
        {
            Id = "sec.lsa", Area = Area2, Title = "Proteção das senhas do Windows (LSA)", Weight = 4, NeedsRestart = true,
            Found = on ? "Ligada" : "Desligada", Level = on ? CheckLevel.Ok : CheckLevel.Warn,
            Advice = on ? "O processo que guarda as credenciais do Windows fica protegido contra leitura por outros programas."
                        : "Impede que programas leiam as senhas e tokens guardados na memória do Windows (técnica de ferramentas como o Mimikatz). " +
                          "Já vem ligada nas instalações novas do Windows 11. Vale depois de reiniciar.",
            Fix = on ? FixKind.None : FixKind.Auto, FixLabel = "LIGAR",
            AutoFix = s => SetReg(s, "sec.lsa", Registry.LocalMachine, path, "RunAsPPL", 1),
        };
    }

    private static GamerCheck? CheckBitLocker(bool laptop)
    {
        string drive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        int status = -1;
        using (var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption"),
                   new ObjectQuery($"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{drive}'")))
            foreach (ManagementObject o in s.Get()) status = Convert.ToInt32(o["ProtectionStatus"] ?? 0);
        bool on = status == 1;
        return new GamerCheck
        {
            Id = "sec.bitlocker", Area = Area2, Title = "Criptografia do disco", Weight = laptop ? 6 : 0,
            Found = status < 0 ? "Indisponível nesta edição do Windows" : on ? "BitLocker ligado" : "Desligada",
            Level = on ? CheckLevel.Ok : laptop ? CheckLevel.Warn : CheckLevel.Info,
            Advice = on ? "Se o PC ou o SSD for roubado, ninguém lê seus arquivos sem a sua conta ou a chave de recuperação."
                : laptop ? "Em notebook, criptografia é a proteção contra roubo: sem ela qualquer um tira o SSD e lê tudo, inclusive sessões salvas do navegador. " +
                           "Guarde a chave de recuperação na sua conta Microsoft."
                         : "Em desktop que fica em casa é opcional. Em SSDs modernos o impacto em jogos é desprezível.",
            Fix = on ? FixKind.None : FixKind.Settings, FixLabel = "ABRIR CONFIGURAÇÃO", FixTarget = "ms-settings:deviceencryption",
        };
    }

    private static GamerCheck CheckSystemRestore()
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
        bool off = Registry.LocalMachine.OpenSubKey(path)?.GetValue("RPSessionInterval") is 0
                   || Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore")?.GetValue("DisableSR") is 1;
        return new GamerCheck
        {
            Id = "sec.restore", Area = Area2, Title = "Proteção do sistema (pontos de restauração)", Weight = 4,
            Found = off ? "Desligada" : "Ligada", Level = off ? CheckLevel.Warn : CheckLevel.Ok,
            Advice = off ? "Sem pontos de restauração, um driver ou atualização ruim só se resolve formatando. Ocupa poucos GB."
                         : "Dá para voltar o Windows a um ponto anterior se algo der errado.",
            Fix = off ? FixKind.Auto : FixKind.None, FixLabel = "LIGAR",
            AutoFix = _ => Ps($"Enable-ComputerRestore -Drive '{Environment.GetEnvironmentVariable("SystemDrive") ?? "C:"}\\' -ErrorAction Stop"),
        };
    }

    private static GamerCheck CheckAutorun()
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        bool on = Registry.LocalMachine.OpenSubKey(path)?.GetValue("NoDriveTypeAutoRun") is 255;
        return new GamerCheck
        {
            Id = "sec.autorun", Area = Area2, Title = "AutoRun de pendrives e discos", Weight = 3,
            Found = on ? "Bloqueado em todas as unidades" : "Permitido em algumas unidades",
            Level = on ? CheckLevel.Ok : CheckLevel.Warn,
            Advice = on ? "Pendrives e discos não executam nada sozinhos ao conectar."
                        : "Bloqueia a execução automática ao plugar pendrives, HDs externos e discos: uma forma antiga, mas ainda usada, de espalhar vírus.",
            Fix = on ? FixKind.None : FixKind.Auto, FixLabel = "BLOQUEAR",
            AutoFix = s => SetReg(s, "sec.autorun", Registry.LocalMachine, path, "NoDriveTypeAutoRun", 255),
        };
    }

    // ================================================================== rede

    private static GamerCheck CheckSmb1()
    {
        // Cliente SMBv1 = serviço mrxsmb10 existe e não está desativado (Start 4).
        int? start = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\mrxsmb10")?.GetValue("Start") as int?;
        int? server = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters")?.GetValue("SMB1") as int?;
        bool on = (start is int st && st != 4) || server == 1;
        return new GamerCheck
        {
            Id = "sec.smb1", Area = Area3, Title = "Protocolo SMBv1 (usado pelo WannaCry)", Weight = 7, NeedsRestart = true,
            Found = on ? "Instalado" : "Desativado", Level = on ? CheckLevel.Bad : CheckLevel.Ok,
            Advice = on ? "Protocolo de compartilhamento de 1984, sem criptografia, explorado pelo WannaCry. Só é necessário para NAS ou impressoras muito antigos."
                        : "Protocolo antigo e inseguro desativado.",
            Fix = on ? FixKind.Auto : FixKind.None, FixLabel = "DESATIVAR",
            AutoFix = s =>
            {
                s.TweakBackup.TryAdd("sec.smb1", "on");
                return Ps("Disable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart -ErrorAction Stop | Out-Null", 300_000);
            },
        };
    }

    private static GamerCheck CheckRdp()
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
        bool on = Registry.LocalMachine.OpenSubKey(path)?.GetValue("fDenyTSConnections") is 0;
        bool nla = Registry.LocalMachine.OpenSubKey(path + @"\WinStations\RDP-Tcp")?.GetValue("UserAuthentication") is 1;
        return new GamerCheck
        {
            Id = "sec.rdp", Area = Area3, Title = "Área de Trabalho Remota", Weight = 6,
            Found = on ? $"Ligada{(nla ? "" : " e SEM autenticação em nível de rede")}" : "Desligada",
            Level = !on ? CheckLevel.Ok : nla ? CheckLevel.Warn : CheckLevel.Bad,
            Advice = on ? "Deixa o PC aceitando login remoto na porta 3389, alvo de ataques de força bruta. Desligue se você não acessa este PC de outro lugar."
                        : "O PC não aceita login remoto.",
            Fix = on ? FixKind.Auto : FixKind.None, FixLabel = "DESLIGAR",
            AutoFix = s => SetReg(s, "sec.rdp", Registry.LocalMachine, path, "fDenyTSConnections", 1),
        };
    }

    private static GamerCheck CheckRemoteAssist()
    {
        const string path = @"SYSTEM\CurrentControlSet\Control\Remote Assistance";
        bool on = Registry.LocalMachine.OpenSubKey(path)?.GetValue("fAllowToGetHelp") is 1;
        return new GamerCheck
        {
            Id = "sec.assist", Area = Area3, Title = "Assistência Remota", Weight = 3,
            Found = on ? "Permitida" : "Bloqueada", Level = on ? CheckLevel.Warn : CheckLevel.Ok,
            Advice = on ? "Golpes de \"suporte técnico\" e de falsos administradores de jogos pedem para você abrir uma conexão remota. Desligue se não usa."
                        : "Ninguém pode pedir controle remoto deste PC pela Assistência Remota.",
            Fix = on ? FixKind.Auto : FixKind.None, FixLabel = "BLOQUEAR",
            AutoFix = s => SetReg(s, "sec.assist", Registry.LocalMachine, path, "fAllowToGetHelp", 0),
        };
    }

    private static GamerCheck? CheckWifi()
    {
        var (code, output) = Run("netsh.exe", "wlan show interfaces", 15_000);
        if (code != 0 || !Regex.IsMatch(output, @"SSID", RegexOptions.IgnoreCase)) return null; // sem Wi-Fi conectado
        string ssid = Regex.Match(output, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        // "Authentication" / "Autenticação": a saída do netsh é traduzida
        string auth = Regex.Match(output, @"^\s*(?:Authentication|Autentica\S*)\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
        if (ssid.Length == 0) return null;
        bool open = Regex.IsMatch(auth, @"^(Open|Aberto|Aberta)$", RegexOptions.IgnoreCase);
        bool wep = auth.Contains("WEP", StringComparison.OrdinalIgnoreCase);
        bool wpa3 = auth.Contains("WPA3", StringComparison.OrdinalIgnoreCase);
        return new GamerCheck
        {
            Id = "sec.wifi", Area = Area3, Title = "Segurança do Wi-Fi conectado", Weight = 6,
            Found = $"{ssid} · {(auth.Length > 0 ? auth : "autenticação não informada")}",
            Level = open || wep ? CheckLevel.Bad : CheckLevel.Ok,
            Advice = open ? "Rede aberta: qualquer pessoa próxima vê seu tráfego que não for criptografado. Evite logar em contas sem VPN."
                   : wep ? "WEP é quebrado em minutos. Troque para WPA2 ou WPA3 nas configurações do roteador."
                   : wpa3 ? "WPA3: o padrão mais seguro." : "Rede protegida por senha (WPA2). Se o roteador oferecer, WPA3 é ainda mais seguro.",
        };
    }

    private static GamerCheck CheckShares()
    {
        var shares = new List<string>();
        using (var s = new ManagementObjectSearcher("SELECT Name, Path, Type FROM Win32_Share"))
            foreach (ManagementObject o in s.Get())
            {
                string name = o["Name"]?.ToString() ?? "";
                if (!name.EndsWith("$") && Convert.ToUInt32(o["Type"] ?? 0u) == 0) shares.Add($"{name} ({o["Path"]})");
            }
        return new GamerCheck
        {
            Id = "sec.shares", Area = Area3, Title = "Pastas compartilhadas na rede", Weight = 0,
            Found = shares.Count == 0 ? "Nenhuma" : string.Join(", ", shares),
            Level = shares.Count == 0 ? CheckLevel.Ok : CheckLevel.Info,
            Advice = shares.Count == 0 ? "Nenhuma pasta deste PC está aberta para a rede."
                : "Essas pastas podem ser abertas por outros aparelhos da rede. Em redes públicas (faculdade, LAN house, hotel) isso expõe seus arquivos. " +
                  "Remova o que não usa: botão direito na pasta → Propriedades → Compartilhamento.",
        };
    }

    // ================================================================== contas

    private static GamerCheck? CheckGuest()
    {
        string? name = null; bool disabled = true;
        using (var s = new ManagementObjectSearcher("SELECT Name, Disabled, SID FROM Win32_UserAccount WHERE LocalAccount = TRUE"))
            foreach (ManagementObject o in s.Get())
                if (o["SID"]?.ToString()?.EndsWith("-501") == true) { name = o["Name"]?.ToString(); disabled = o["Disabled"] is true; }
        if (name == null) return null;
        return new GamerCheck
        {
            Id = "sec.guest", Area = Area4, Title = "Conta Convidado", Weight = 5,
            Found = disabled ? "Desativada" : $"\"{name}\" ATIVA", Level = disabled ? CheckLevel.Ok : CheckLevel.Bad,
            Advice = disabled ? "Ninguém entra no PC sem conta." : "A conta Convidado não tem senha: qualquer um entra no PC e acessa a rede por ela.",
            Fix = disabled ? FixKind.None : FixKind.Auto, FixLabel = "DESATIVAR",
            AutoFix = s =>
            {
                s.TweakBackup.TryAdd("sec.guest", name);
                var (code, output) = Run("net.exe", $"user \"{name}\" /active:no", 15_000);
                return code == 0 ? "" : Tail(output);
            },
        };
    }

    private static GamerCheck Check2fa() => new()
    {
        Id = "sec.2fa", Area = Area4, Title = "Contas de jogos com verificação em duas etapas", Weight = 0, Level = CheckLevel.Info,
        Found = "Verifique em cada plataforma",
        Advice = "O app não consegue ver suas contas, mas é a proteção que mais evita perder inventário e skins: ative o Steam Guard no celular, " +
                 "a autenticação de dois fatores no Discord, Epic, Riot, Battle.net, EA e Xbox, e use senhas diferentes em cada uma. " +
                 "Desconfie de \"ganhe skins\", \"teste meu jogo\" e links de \"denúncia\" no Discord: são os golpes mais comuns de roubo de conta.",
        Fix = FixKind.Settings, FixLabel = "STEAM GUARD", FixTarget = "https://store.steampowered.com/twofactor/manage",
    };

    // ================================================================== BIOS

    private static string BiosSecurityGuide(string what)
    {
        var (maker, model) = OcCheck.Motherboard();
        bool giga = maker.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase);
        bool asus = maker.Contains("ASUS", StringComparison.OrdinalIgnoreCase);
        bool msi = maker.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || maker.Contains("MSI", StringComparison.OrdinalIgnoreCase);
        var l = new List<string> { $"Placa-mãe: {(maker + " " + model).Trim()}", "", "1. Entre na BIOS (botão \"Reiniciar na BIOS\" abaixo, ou Delete/F2 ao ligar). F7 abre o modo avançado." };
        if (what == "secureboot")
        {
            l.Add("2. " + (asus ? "Boot → Secure Boot → OS Type: Windows UEFI mode"
                         : msi ? "Settings → Security → Secure Boot → Secure Boot: Enabled"
                         : giga ? "Boot → CSM Support: Disabled e Secure Boot: Enabled"
                         : "Boot ou Security → Secure Boot: Enabled (com CSM desligado)"));
            l.Add("3. Se pedir, escolha \"Install default Secure Boot keys\" / \"Restore Factory Keys\".");
            l.Add("4. F10 para salvar. O Windows precisa estar instalado em modo UEFI (o diagnóstico avisa se não estiver).");
        }
        else
        {
            l.Add("2. " + (asus ? "Advanced → PCH-FW Configuration → PTT: Enable (Intel) ou Advanced → AMD fTPM configuration → Firmware TPM (AMD)"
                         : msi ? "Settings → Security → Trusted Computing → Security Device Support: Enable · AMD fTPM switch / Intel PTT"
                         : giga ? "Settings → Miscellaneous → Intel Platform Trust Technology (PTT) / AMD CPU fTPM: Enabled"
                         : "procure TPM, fTPM, PTT ou \"Security Device\" nos menus Security/Advanced"));
            l.Add("3. F10 para salvar.");
        }
        return string.Join("\n", l);
    }

    // ================================================================== desfazer

    /// <summary>Volta tudo que o SHIELD mudou (as cópias "sec." em TweakBackup).</summary>
    public static void RestoreAll(AppSettings s)
    {
        foreach (var (key, prev) in s.TweakBackup.Where(kv => kv.Key.StartsWith("sec.")).ToList())
        {
            try
            {
                // Os valores vêm do settings.json: só aceita o formato esperado, nunca texto livre num comando.
                string profile = key.StartsWith("sec.firewall.") ? key["sec.firewall.".Length..] : "";
                if (profile.Length > 0)
                {
                    if (profile is "domainprofile" or "privateprofile" or "publicprofile")
                        Run("netsh.exe", $"advfirewall set {profile} state off", 30_000);
                }
                else if (key == "sec.pua") { if (int.TryParse(prev, out int pua) && pua is >= 0 and <= 2) Ps($"Set-MpPreference -PUAProtection {pua}"); }
                else if (key == "sec.smb1") Ps("Enable-WindowsOptionalFeature -Online -FeatureName SMB1Protocol -NoRestart -All | Out-Null", 300_000);
                else if (key == "sec.guest" && prev != null && System.Text.RegularExpressions.Regex.IsMatch(prev, @"^[\p{L}\p{N} ._-]{1,20}$"))
                    Run("net.exe", $"user \"{prev}\" /active:yes", 15_000);
                else if (RegBackups.TryGetValue(key, out var r)) RestoreReg(r.Hive, r.Path, r.Name, prev);
            }
            catch { }
            s.TweakBackup.Remove(key);
        }
        s.Save();
    }

    // ================================================================== auxiliares

    private static readonly Dictionary<string, (RegistryKey Hive, string Path, string Name)> RegBackups = new();

    private static string SetReg(AppSettings s, string key, RegistryKey hive, string path, string name, object value)
    {
        RegBackups[key] = (hive, path, name);
        using (var k = hive.OpenSubKey(path))
            if (!s.TweakBackup.ContainsKey(key))
                s.TweakBackup[key] = k?.GetValue(name) switch { null => "<ausente>", int i => $"d:{i}", var o => $"s:{o}" };
        using var w = hive.CreateSubKey(path);
        if (value is int iv) w.SetValue(name, iv, RegistryValueKind.DWord); else w.SetValue(name, value.ToString()!, RegistryValueKind.String);
        s.Save();
        return "";
    }

    private static void RestoreReg(RegistryKey hive, string path, string name, string? prev)
    {
        using var k = hive.CreateSubKey(path);
        if (prev is null or "<ausente>") k.DeleteValue(name, false);
        else if (prev.StartsWith("d:")) k.SetValue(name, int.Parse(prev[2..]), RegistryValueKind.DWord);
        else k.SetValue(name, prev.StartsWith("s:") ? prev[2..] : prev, RegistryValueKind.String);
    }

    /// <summary>Registra de antemão onde fica cada valor, para "Desfazer" funcionar mesmo depois de reabrir o app.</summary>
    static SecurityAudit()
    {
        RegBackups["sec.smartscreen"] = (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "SmartScreenEnabled");
        RegBackups["sec.uac.lua"] = (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "EnableLUA");
        RegBackups["sec.uac.prompt"] = (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin");
        RegBackups["sec.lsa"] = (Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa", "RunAsPPL");
        RegBackups["sec.autorun"] = (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun");
        RegBackups["sec.rdp"] = (Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections");
        RegBackups["sec.assist"] = (Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp");
    }

    /// <summary>
    /// Roda um comando PowerShell. Devolve "" se deu certo, ou a mensagem de erro (ou a saída, com returnOutput).
    /// Só a saída padrão é lida como resultado: o PowerShell escreve o progresso (CLIXML "#&lt; CLIXML…") na saída de erro
    /// quando ela está redirecionada, e misturar as duas corrompia o JSON e os "deu certo".
    /// </summary>
    internal static string Ps(string script, int timeoutMs = 60_000, bool returnOutput = false)
    {
        string body = returnOutput ? script : $"try {{ {script}; 'TGOK' }} catch {{ $_.Exception.Message }}";
        string wrapped = "$ProgressPreference='SilentlyContinue'; $WarningPreference='SilentlyContinue'; " +
                         "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + body;
        var (code, stdout, stderr) = RunSplit("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)), timeoutMs);
        if (returnOutput) return stdout;
        string o = stdout.Trim();
        if (o.EndsWith("TGOK")) return "";
        string err = o.Length > 0 ? o : CleanStderr(stderr);
        return err.Length > 0 ? Tail(err) : $"PowerShell terminou com o código {code}";
    }

    /// <summary>Tira o CLIXML de progresso da saída de erro do PowerShell, deixando só mensagens legíveis.</summary>
    private static string CleanStderr(string e)
    {
        if (!e.Contains("CLIXML")) return e.Trim();
        var msgs = Regex.Matches(e, @"<S S=""Error"">(.*?)</S>").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Replace("_x000D__x000A_", " ")));
        return string.Join(" ", msgs).Trim();
    }

    internal static (int Code, string Output) Run(string exe, string args, int timeoutMs)
    {
        var (code, o, e) = RunSplit(exe, args, timeoutMs);
        return (code, o + e);
    }

    internal static (int Code, string Stdout, string Stderr) RunSplit(string exe, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        if (exe.StartsWith("powershell", StringComparison.OrdinalIgnoreCase))
            psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.UTF8;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "", "tempo esgotado"); }
        p.WaitForExit(); // sem tempo: espera também o fim das saídas redirecionadas, então as leituras abaixo já terminaram
        return (p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string Tail(string s)
    {
        s = s.Trim();
        return s.Length > 240 ? "…" + s[^240..] : s;
    }
}
