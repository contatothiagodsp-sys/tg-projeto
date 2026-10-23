using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ThgOptimization;

/// <summary>O que a DDU remove: o driver de vídeo de uma marca, todos os de vídeo, ou drivers de áudio (como o modo "Audio" do DDU).
/// Os valores novos ficam no fim para não mudar os números de uma limpeza já agendada.</summary>
public enum VideoVendor { Nvidia, Amd, Intel, AllVideo, Realtek, Creative, AllAudio }

/// <summary>Pacote de driver no repositório do Windows (Driver Store).</summary>
public sealed record DriverPackage(string Oem, string Original, string Provider, string Class, string Version)
{
    public string Display => $"{Original} · {Class} · {Version}";
}

/// <summary>Como terminar, como no DDU: limpar e reiniciar (recomendado), limpar sem reiniciar, limpar e desligar (para trocar a placa).</summary>
public enum DduFinish { Restart, None, Shutdown }

/// <summary>Opções da limpeza. InstallerPath: instalador do driver novo para abrir sozinho no fim (ou no próximo login).</summary>
public sealed record DduOptions(bool Backup, bool BlockWindowsUpdate, bool ShaderCache, bool Leftovers,
                                bool Monitors = true, bool StoreApps = true, bool RestorePoint = true,
                                string? InstallerPath = null, DduFinish Finish = DduFinish.Restart);

public sealed record DduJob(VideoVendor Vendor, DduOptions Options);

/// <summary>
/// DDU do tHg: remoção completa do driver de vídeo, no princípio do Display Driver Uninstaller. Remove os dispositivos
/// de vídeo e de áudio HDMI da marca, apaga do Driver Store todos os pacotes do driver (pnputil /delete-driver
/// /uninstall /force), para os serviços do painel, apaga pastas e caches que sobram e, se pedido, impede o Windows Update
/// de reinstalar um driver sozinho antes de você instalar o novo. Pode rodar no Modo de Segurança, como o DDU
/// recomenda: o app agenda, reinicia, limpa e volta para o Windows normal sozinho.
///
/// Diferenças conscientes do DDU: não apaga chaves de registro "na mão" (o pnputil remove o que pertence a cada pacote)
/// e, na AMD e na Intel, só toca no que é de vídeo, nunca nos drivers de chipset, rede ou áudio da placa-mãe.
/// </summary>
public static class DduCleaner
{
    public static string LogPath => Path.Combine(AppSettings.Folder, "ddu-log.txt");
    public static string BackupRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "tHgOptimization-DDU");

    public static string Name(VideoVendor v) => v switch
    {
        VideoVendor.Nvidia => "NVIDIA", VideoVendor.Amd => "AMD", VideoVendor.Intel => "Intel",
        VideoVendor.AllVideo => "todos os drivers de vídeo", VideoVendor.Realtek => "Realtek (áudio)",
        VideoVendor.Creative => "Creative / Sound Blaster", _ => "todos os drivers de áudio",
    };
    private static string PciVendor(VideoVendor v) => v switch { VideoVendor.Nvidia => "10DE", VideoVendor.Amd => "1002", VideoVendor.Creative => "1102", VideoVendor.Realtek => "10EC", _ => "8086" };

    /// <summary>Texto para frases: "o driver NVIDIA", "todos os drivers de vídeo", "o driver de áudio Realtek"…</summary>
    public static string What(VideoVendor v) => v switch
    {
        VideoVendor.AllVideo or VideoVendor.AllAudio => Name(v),
        VideoVendor.Realtek => "o driver de áudio Realtek",
        VideoVendor.Creative => "o driver de áudio Creative / Sound Blaster",
        _ => $"o driver {Name(v)}",
    };

    public static bool IsAudio(VideoVendor v) => v is VideoVendor.Realtek or VideoVendor.Creative or VideoVendor.AllAudio;

    /// <summary>Marcas que um alvo "todos" junta.</summary>
    private static VideoVendor[] Members(VideoVendor v) => v switch
    {
        VideoVendor.AllVideo => new[] { VideoVendor.Nvidia, VideoVendor.Amd, VideoVendor.Intel },
        VideoVendor.AllAudio => new[] { VideoVendor.Realtek, VideoVendor.Creative },
        _ => new[] { v },
    };

    public static bool InSafeMode => GetSystemMetrics(67 /* SM_CLEANBOOT */) != 0;

    // ================================================================== o que existe

    /// <summary>Pacotes de vídeo da marca no Driver Store (Get-WindowsDriver: nomes de campos iguais em qualquer idioma do Windows).</summary>
    public static List<DriverPackage> Packages(VideoVendor v)
    {
        var all = new List<DriverPackage>();
        try { all = PackagesFromDism(); } catch { }
        if (all.Count == 0) all = PackagesFromWmi(); // reserva: só os pacotes ligados a algum dispositivo
        return all.Where(p => Matches(v, p)).GroupBy(p => p.Oem, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                  .OrderBy(p => p.Class).ToList();
    }

    private static List<DriverPackage> PackagesFromDism()
    {
        string raw = SecurityAudit.Ps("Get-WindowsDriver -Online | Select-Object Driver,OriginalFileName,ProviderName,ClassName,Version | ConvertTo-Json -Compress",
                                      240_000, returnOutput: true);
        // Só o trecho JSON (qualquer texto antes ou depois é ignorado).
        int a = raw.IndexOfAny(new[] { '[', '{' });
        int b = Math.Max(raw.LastIndexOf(']'), raw.LastIndexOf('}'));
        var all = new List<DriverPackage>();
        if (a < 0 || b <= a) return all;
        string json = raw[a..(b + 1)];
        using var doc = JsonDocument.Parse(json.StartsWith("[") ? json : $"[{json}]");
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string S(string n) => e.TryGetProperty(n, out var p) ? (p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : p.ToString()) : "";
            all.Add(new DriverPackage(S("Driver"), Path.GetFileName(S("OriginalFileName")), S("ProviderName"), S("ClassName"), S("Version")));
        }
        return all;
    }

    private static List<DriverPackage> PackagesFromWmi()
    {
        var list = new List<DriverPackage>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT InfName, DriverProviderName, DeviceClass, DriverVersion FROM Win32_PnPSignedDriver");
            foreach (ManagementObject o in s.Get())
            {
                string inf = o["InfName"]?.ToString() ?? "";
                if (!inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase)) continue;
                string cls = (o["DeviceClass"]?.ToString() ?? "") switch { "DISPLAY" => "Display", "MEDIA" => "MEDIA", "SOFTWARECOMPONENT" => "SoftwareComponent", "EXTENSION" => "Extension", var c => c };
                list.Add(new DriverPackage(inf, OriginalInf(inf), o["DriverProviderName"]?.ToString() ?? "", cls, o["DriverVersion"]?.ToString() ?? ""));
            }
        }
        catch { }
        return list;
    }

    /// <summary>Nome original do .inf (o oemNN.inf aponta para ele na primeira linha "; Original INF"); senão, o próprio oemNN.</summary>
    private static string OriginalInf(string oem)
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", oem);
            foreach (var line in File.ReadLines(path).Take(40))
                if (line.Contains(".inf", StringComparison.OrdinalIgnoreCase) && line.TrimStart().StartsWith(";") && !line.Contains(oem, StringComparison.OrdinalIgnoreCase))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"[\w\-]+\.inf", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (m.Success) return m.Value;
                }
        }
        catch { }
        return oem;
    }

    private static bool Matches(VideoVendor v, DriverPackage p) => v switch
    {
        VideoVendor.AllVideo => Members(v).Any(m => IsVideoPackage(m, p)),
        // Todo o áudio de terceiros: qualquer pacote da classe de som (inclui o áudio HDMI das placas de vídeo e headsets USB)
        // e os componentes/extensões de áudio das marcas conhecidas. O Windows volta com o driver de áudio genérico.
        VideoVendor.AllAudio => p.Class.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) || Members(v).Any(m => IsAudioPackage(m, p)),
        VideoVendor.Realtek or VideoVendor.Creative => IsAudioPackage(v, p),
        _ => IsVideoPackage(v, p),
    };

    /// <summary>Áudio da Realtek ou da Creative. A Realtek também faz drivers de rede e leitor de cartão: esses ficam (outra classe).</summary>
    private static bool IsAudioPackage(VideoVendor v, DriverPackage p)
    {
        string prov = p.Provider, orig = p.Original.ToLowerInvariant(), cls = p.Class;
        bool media = cls.Equals("MEDIA", StringComparison.OrdinalIgnoreCase);
        bool comp = cls.Equals("SoftwareComponent", StringComparison.OrdinalIgnoreCase) || cls.Equals("Extension", StringComparison.OrdinalIgnoreCase)
                    || cls.Equals("AudioProcessingObject", StringComparison.OrdinalIgnoreCase);
        return v switch
        {
            VideoVendor.Realtek => prov.Contains("Realtek", StringComparison.OrdinalIgnoreCase)
                                   && (media || (comp && (orig.Contains("hdx") || orig.Contains("audio") || orig.Contains("apo") || orig.Contains("realtekservice") || orig.Contains("realtekhsa")))),
            VideoVendor.Creative => prov.Contains("Creative", StringComparison.OrdinalIgnoreCase) && (media || comp),
            _ => false,
        };
    }

    /// <summary>Só o que é do vídeo. AMD e Intel também fazem drivers de chipset, rede e áudio: esses ficam.</summary>
    private static bool IsVideoPackage(VideoVendor v, DriverPackage p)
    {
        string prov = p.Provider, orig = p.Original.ToLowerInvariant(), cls = p.Class;
        switch (v)
        {
            case VideoVendor.Nvidia:
                return prov.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
            case VideoVendor.Amd:
                if (!(prov.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) || prov.Equals("AMD", StringComparison.OrdinalIgnoreCase)
                      || prov.Contains("ATI Technologies", StringComparison.OrdinalIgnoreCase))) return false;
                return cls.Equals("Display", StringComparison.OrdinalIgnoreCase)
                       || (cls.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) && orig.StartsWith("atihd"))
                       || (cls.Equals("SoftwareComponent", StringComparison.OrdinalIgnoreCase) && (orig.StartsWith("amdfendr") || orig.StartsWith("amdxe") || orig.StartsWith("u0")))
                       || (cls.Equals("Extension", StringComparison.OrdinalIgnoreCase) && (orig.StartsWith("u0") || orig.Contains("amdwddmg")));
            default:
                if (!prov.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return false;
                return cls.Equals("Display", StringComparison.OrdinalIgnoreCase)
                       || ((cls.Equals("SoftwareComponent", StringComparison.OrdinalIgnoreCase) || cls.Equals("Extension", StringComparison.OrdinalIgnoreCase))
                           && (orig.StartsWith("iigd") || orig.StartsWith("igdlh") || orig.StartsWith("cui_") || orig.StartsWith("igcc") || orig.Contains("gfx")));
        }
    }

    /// <summary>Dispositivos da marca: placa de vídeo e o áudio HDMI/DisplayPort dela.</summary>
    public static List<(string Id, string Name)> Devices(VideoVendor v)
    {
        var list = new List<(string, string)>();
        using var s = new ManagementObjectSearcher("SELECT PNPDeviceID, Name, PNPClass FROM Win32_PnPEntity");
        foreach (ManagementObject o in s.Get())
        {
            string id = o["PNPDeviceID"]?.ToString() ?? "", cls = o["PNPClass"]?.ToString() ?? "";
            if (IsDevice(v, id, cls)) list.Add((id, o["Name"]?.ToString() ?? id));
        }
        return list;
    }

    private static bool IsDevice(VideoVendor v, string id, string cls)
    {
        bool media = cls.Equals("MEDIA", StringComparison.OrdinalIgnoreCase);
        switch (v)
        {
            case VideoVendor.AllVideo:
                return Members(v).Any(m => IsDevice(m, id, cls));
            case VideoVendor.AllAudio:
                // Placas e chips de som reais (HD Audio, PCI e USB); dispositivos virtuais de software ficam.
                return media && (id.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)
                                 || id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase));
            case VideoVendor.Realtek or VideoVendor.Creative:
                string ven = PciVendor(v);
                return media && (id.StartsWith($@"HDAUDIO\FUNC_01&VEN_{ven}", StringComparison.OrdinalIgnoreCase)
                                 || id.StartsWith($@"PCI\VEN_{ven}", StringComparison.OrdinalIgnoreCase));
            default:
                string gv = PciVendor(v);
                bool gpu = cls == "Display" && id.StartsWith($@"PCI\VEN_{gv}", StringComparison.OrdinalIgnoreCase);
                bool hdmiAudio = v != VideoVendor.Intel && id.StartsWith($@"HDAUDIO\FUNC_01&VEN_{gv}", StringComparison.OrdinalIgnoreCase);
                return gpu || hdmiAudio;
        }
    }

    // ================================================================== limpeza

    /// <summary>
    /// Faz a limpeza com registro completo (como o "registro estendido" do DDU): cada pacote, dispositivo, serviço, pasta,
    /// arquivo e valor do registro, com o resultado. Na tela vai um item por linha; no arquivo de registro vai também
    /// cada arquivo apagado. Cada etapa que falhar é registrada e a limpeza continua. Devolve quantos itens foram removidos.
    /// </summary>
    public static int Run(VideoVendor v, DduOptions o, AppSettings st, Action<string> log)
    {
        Directory.CreateDirectory(AppSettings.Folder);
        var file = new System.Text.StringBuilder();
        void Write(string m) { file.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {m}\r\n"); if (file.Length > 4096) Flush(); }
        void Flush() { try { File.AppendAllText(LogPath, file.ToString()); } catch { } file.Clear(); }
        void L(string m) { log(m); Write(m); Flush(); }  // tela + arquivo, gravado na hora (a janela acompanha pelo arquivo)
        void D(string m) => Write("    " + m);           // só arquivo (detalhe: cada arquivo apagado)
        void Step(string what, Action a) { try { a(); } catch (Exception ex) { L($"  ✗ falha em \"{what}\": {ex.Message} (seguindo com o resto)"); } }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int removed = 0;

        L("");
        L($"════════ DDU {Name(v)} · {(InSafeMode ? "Modo de Segurança" : "Windows normal")} · {DateTime.Now:dd/MM/yyyy HH:mm:ss} ════════");
        L($"Windows {Environment.OSVersion.Version} · tHg Optimization {typeof(DduCleaner).Assembly.GetName().Version}");
        L($"Opções: backup={Yes(o.Backup)} · bloquear Windows Update={Yes(o.BlockWindowsUpdate)} · serviços e pastas={Yes(o.Leftovers)} · " +
          $"painel da Store={Yes(o.StoreApps)} · monitores={Yes(o.Monitors)} · cache de shaders={Yes(o.ShaderCache)} · " +
          $"ponto de restauração={Yes(o.RestorePoint)} · fim={o.Finish} · driver novo={(o.InstallerPath != null ? Path.GetFileName(o.InstallerPath) : "nenhum")}");

        // ---------------- 1. O que existe
        L("");
        L("[1/8] Procurando o driver");
        var pkgs = new List<DriverPackage>();
        var devs = new List<(string Id, string Name)>();
        Step("procurar os pacotes", () => pkgs = Packages(v));
        Step("procurar os dispositivos", () => devs = Devices(v));
        L($"  {devs.Count} dispositivo(s):");
        foreach (var (id, name) in devs) L($"    • {name}  [{id}]");
        L($"  {pkgs.Count} pacote(s) no Driver Store:");
        foreach (var p in pkgs) L($"    • {p.Oem} = {p.Original} · {p.Class} · {p.Provider} · versão {p.Version}");
        if (pkgs.Count == 0 && devs.Count == 0)
        {
            L("  Nada da " + Name(v) + " para remover. Nenhuma alteração foi feita.");
            Flush();
            return 0;
        }

        // ---------------- 2. Segurança antes de apagar
        L("");
        L("[2/8] Antes de apagar");
        if (o.RestorePoint && !InSafeMode)
            Step("ponto de restauração", () =>
            {
                L("  … Criando ponto de restauração do Windows (pode levar até 90 segundos)");
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                string rp = GamerAudit.CreateRestorePointAsync("tHg DDU", 90).GetAwaiter().GetResult();
                L(rp.Length == 0 ? $"  ✓ Ponto de restauração criado ({t0.Elapsed.TotalSeconds:N0} s)" : $"  – Sem ponto de restauração ({t0.Elapsed.TotalSeconds:N0} s): {rp}. Seguindo com o backup dos drivers.");
            });
        else if (o.RestorePoint) L("  – Ponto de restauração: indisponível no Modo de Segurança");
        if (o.Backup && pkgs.Count > 0)
            Step("backup", () =>
            {
                string dir = Path.Combine(BackupRoot, $"{Name(v)}-{DateTime.Now:yyyyMMdd-HHmm}");
                Directory.CreateDirectory(dir);
                L($"  … Copiando {pkgs.Count} pacote(s) para {dir}");
                foreach (var p in pkgs)
                {
                    var (code, output) = SecurityAudit.Run("pnputil.exe", $"/export-driver {p.Oem} \"{dir}\"", 120_000);
                    L(code == 0 ? $"  ✓ Backup de {p.Oem} ({p.Original})" : $"  ✗ Backup de {p.Oem} falhou (código {code}) {Short(output)}");
                }
                L($"  Pasta do backup: {dir}");
            });

        // ---------------- 3. Windows Update
        L("");
        L("[3/8] Windows Update");
        if (o.BlockWindowsUpdate)
            Step("bloquear o Windows Update", () =>
            {
                BlockWindowsUpdate(st);
                L($"  ✓ HKLM\\{WuPolicy}\\ExcludeWUDriversInQualityUpdate = 1 (antes: {st.TweakBackup.GetValueOrDefault("ddu.wu.policy")})");
                L($"  ✓ HKLM\\{DriverSearch}\\SearchOrderConfig = 0 (antes: {st.TweakBackup.GetValueOrDefault("ddu.wu.search")})");
            });
        else L("  – Não alterado");

        // ---------------- 4. Dispositivos
        L("");
        L("[4/8] Removendo os dispositivos (a tela passa para o vídeo básico da Microsoft)");
        foreach (var (id, name) in devs)
            Step($"remover {name}", () =>
            {
                var (code, output) = SecurityAudit.Run("pnputil.exe", $"/remove-device \"{id}\"", 60_000);
                if (code is 0 or 3010) { removed++; L($"  ✓ {name}"); }
                else L($"  ✗ {name}: código {code}. {Short(output)}");
                D($"pnputil /remove-device \"{id}\" → {code}: {Short(output)}");
            });
        if (devs.Count == 0) L("  – Nenhum dispositivo conectado");

        // ---------------- 5. Pacotes
        L("");
        L("[5/8] Apagando os pacotes do Driver Store");
        foreach (var p in pkgs)
            Step($"apagar {p.Oem}", () =>
            {
                var (code, output) = SecurityAudit.Run("pnputil.exe", $"/delete-driver {p.Oem} /uninstall /force", 120_000);
                if (code is 0 or 3010) { removed++; L($"  ✓ {p.Oem} ({p.Original}, {p.Class}){(code == 3010 ? " · termina de sair ao reiniciar" : "")}"); }
                else L($"  ✗ {p.Oem} ({p.Original}): código {code}. {Short(output)}");
                D($"pnputil /delete-driver {p.Oem} /uninstall /force → {code}: {Short(output)}");
            });

        // ---------------- 6. Serviços, processos e pastas do painel
        L("");
        L("[6/8] Serviços, programas e pastas do painel");
        if (o.Leftovers)
            Step("serviços e pastas", () =>
            {
                foreach (var svc in Services(v))
                {
                    var (qc, _) = SecurityAudit.Run("sc.exe", $"query \"{svc}\"", 15_000);
                    if (qc != 0) { D($"serviço {svc}: não existe"); continue; }
                    SecurityAudit.Run("sc.exe", $"stop \"{svc}\"", 30_000);
                    var (dc, dout) = SecurityAudit.Run("sc.exe", $"delete \"{svc}\"", 30_000);
                    if (dc == 0) { removed++; L($"  ✓ Serviço removido: {svc}"); } else L($"  ✗ Serviço {svc}: código {dc}. {Short(dout)}");
                }
                foreach (var proc in Processes(v))
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(proc))
                        try { p.Kill(true); L($"  ✓ Programa fechado: {proc}.exe (PID {p.Id})"); } catch { }
                foreach (var dir in Folders(v))
                {
                    if (!Directory.Exists(dir)) { D($"pasta {dir}: não existe"); continue; }
                    var (files, bytes, left) = DeleteTree(dir, f => D($"apagado: {f}"), f => D($"em uso, ficou: {f}"));
                    removed++;
                    L(left == 0 ? $"  ✓ Pasta apagada: {dir} ({files} arquivos, {bytes / 1048576.0:N0} MB)"
                                : $"  – Pasta limpa em parte: {dir} ({files} apagados, {left} em uso, {bytes / 1048576.0:N0} MB)");
                }
            });
        else L("  – Não alterado");

        // ---------------- 7. Painel da Store e monitores
        L("");
        L("[7/8] Painel da Microsoft Store e monitores");
        if (o.StoreApps)
            Step("painel da Microsoft Store", () =>
            {
                foreach (var app in StoreApps(v))
                {
                    string found = SecurityAudit.Ps($"Get-AppxPackage -AllUsers -Name '*{app}*' | ForEach-Object {{ $_.PackageFullName }}", 60_000, returnOutput: true).Trim();
                    if (found.Length == 0) { D($"app da Store {app}: não instalado"); continue; }
                    string r = SecurityAudit.Ps($"Get-AppxPackage -AllUsers -Name '*{app}*' | Remove-AppxPackage -AllUsers -ErrorAction Stop", 120_000);
                    foreach (var full in found.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        L(r.Length == 0 ? $"  ✓ App removido: {full}" : $"  ✗ App {full}: {r}");
                    if (r.Length == 0) removed++;
                }
            });
        if (o.Monitors && !IsAudio(v))
            Step("monitores", () =>
            {
                string raw = SecurityAudit.Ps("Get-PnpDevice -Class Monitor -ErrorAction SilentlyContinue | ForEach-Object { $_.InstanceId + '|' + $_.FriendlyName + '|' + $_.Status }",
                                              60_000, returnOutput: true);
                foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split('|');
                    string id = parts[0], name = parts.Length > 1 ? parts[1] : id, state = parts.Length > 2 && parts[2] == "OK" ? "conectado" : "antigo";
                    var (code, _) = SecurityAudit.Run("pnputil.exe", $"/remove-device \"{id}\"", 30_000);
                    L(code is 0 or 3010 ? $"  ✓ Monitor ({state}): {name}" : $"  ✗ Monitor {name}: código {code}");
                }
            });
        if (!o.StoreApps && !o.Monitors) L("  – Não alterado");

        // ---------------- 8. Cache de shaders
        L("");
        L("[8/8] Cache de shaders");
        if (o.ShaderCache && !IsAudio(v))
            Step("cache de shaders", () =>
            {
                foreach (var dir in ShaderCache.Folders())
                {
                    var (files, bytes, left) = DeleteTree(dir, f => D($"apagado: {f}"), f => D($"em uso, ficou: {f}"), keepRoot: true);
                    L($"  ✓ {dir}: {files} arquivos, {bytes / 1048576.0:N0} MB{(left > 0 ? $" ({left} em uso ficaram)" : "")}");
                }
            });
        else L(IsAudio(v) ? "  – Não se aplica a áudio" : "  – Não alterado");

        L("");
        L(removed > 0 ? $"Concluído em {clock.Elapsed.TotalSeconds:N0} s: {removed} item(ns) removido(s). Instale o driver novo depois de reiniciar."
                      : $"Concluído em {clock.Elapsed.TotalSeconds:N0} s, mas nenhum dispositivo ou pacote foi removido. Veja as mensagens acima.");
        L($"Registro completo (com cada arquivo apagado): {LogPath}");
        Flush();
        st.Save();
        return removed;
    }

    private static string Yes(bool b) => b ? "sim" : "não";

    private static string Short(string s)
    {
        s = string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(2));
        return s.Length > 160 ? s[..160] + "…" : s;
    }

    private static string[] Services(VideoVendor v) => Members(v).Length > 1 ? Members(v).SelectMany(Services).Distinct().ToArray() : v switch
    {
        VideoVendor.Realtek => new[] { "RtkAudioUniversalService", "RtkAudioService" },
        VideoVendor.Creative => new[] { "CTAudSvcService" },
        VideoVendor.Nvidia => new[] { "NVDisplay.ContainerLocalSystem", "NvContainerLocalSystem", "NvContainerNetworkService", "NvTelemetryContainer", "nvagent" },
        VideoVendor.Amd => new[] { "AMD External Events Utility", "AMD Crash Defender Service", "amdfendr", "amdfendrmgr", "AUEPLauncher", "AMD Log Utility" },
        _ => new[] { "igccservice", "cplspcon", "IntelGraphicsSoftwareService" },
    };

    private static string[] StoreApps(VideoVendor v) => Members(v).Length > 1 ? Members(v).SelectMany(StoreApps).Distinct().ToArray() : v switch
    {
        VideoVendor.Realtek => new[] { "RealtekAudioControl", "RealtekSemiconductorCorp.RealtekAudioControl" },
        VideoVendor.Creative => new[] { "CreativeTechnologyLtd" },
        VideoVendor.Nvidia => new[] { "NVIDIAControlPanel" },
        VideoVendor.Amd => new[] { "AMDRadeonSoftware", "AdvancedMicroDevicesInc-RSXCM", "AdvancedMicroDevicesInc-2.AMDLink" },
        _ => new[] { "IntelGraphicsCommandCenter", "IntelGraphicsExperience", "IntelGraphicsControlPanel", "IntelArcSoftware" },
    };

    private static string[] Processes(VideoVendor v) => Members(v).Length > 1 ? Members(v).SelectMany(Processes).Distinct().ToArray() : v switch
    {
        VideoVendor.Realtek => new[] { "RtkAudUService64", "RAVBg64", "RAVCpl64", "RtkNGUI64" },
        VideoVendor.Creative => new[] { "CTAudSvc", "SBCommand" },
        VideoVendor.Nvidia => new[] { "NVIDIA app", "NVIDIA Share", "nvcontainer", "NVDisplay.Container", "nvsphelper64", "NVIDIA Overlay" },
        VideoVendor.Amd => new[] { "RadeonSoftware", "AMDRSServ", "AMDRSSrcExt", "amdow", "atieclxx", "cncmd" },
        _ => new[] { "IntelGraphicsSoftware", "igfxEM", "IGCC" },
    };

    private static IEnumerable<string> Folders(VideoVendor v)
    {
        if (Members(v).Length > 1) return Members(v).SelectMany(Folders).Distinct();
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string sys = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return v switch
        {
            VideoVendor.Realtek => new[] { Path.Combine(pf, @"Realtek\Audio"), Path.Combine(pf86, @"Realtek\Audio") },
            VideoVendor.Creative => new[] { Path.Combine(pf86, "Creative"), Path.Combine(pf, "Creative") },
            VideoVendor.Nvidia => new[]
            {
                Path.Combine(pf, "NVIDIA Corporation"), Path.Combine(pf86, "NVIDIA Corporation"), Path.Combine(pd, "NVIDIA Corporation"),
                Path.Combine(pd, "NVIDIA"), Path.Combine(local, "NVIDIA"), Path.Combine(local, "NVIDIA Corporation"), Path.Combine(roam, "NVIDIA"),
                Path.Combine(sys, "NVIDIA"),
            },
            // Só as pastas do Adrenalin e do instalador: AMD Chipset e Ryzen Master ficam.
            VideoVendor.Amd => new[] { Path.Combine(sys, "AMD"), Path.Combine(pf, @"AMD\CNext"), Path.Combine(local, @"AMD\CN"), Path.Combine(local, @"AMD\DxCache") },
            _ => new[] { Path.Combine(local, @"Intel\IntelGraphicsSoftware") },
        };
    }

    /// <summary>Apaga a pasta arquivo por arquivo (para registrar cada um). Devolve (apagados, bytes, em uso).</summary>
    private static (int Files, long Bytes, int Left) DeleteTree(string dir, Action<string> deleted, Action<string> inUse, bool keepRoot = false)
    {
        int files = 0, left = 0; long bytes = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            try { long len = f.Length; f.Attributes = FileAttributes.Normal; f.Delete(); files++; bytes += len; deleted(f.FullName); }
            catch { left++; inUse(f.FullName); }
        }
        try
        {
            if (keepRoot) foreach (var d in Directory.EnumerateDirectories(dir)) Directory.Delete(d, true);
            else Directory.Delete(dir, true);
        }
        catch { }
        return (files, bytes, left);
    }

    // ================================================================== processo separado

    public static string JobPath => Path.Combine(AppSettings.Folder, "ddu-job.json");

    /// <summary>
    /// Inicia a limpeza num processo separado (o próprio tHg com --ddu-worker, sem janela). Quando a placa de vídeo é
    /// removida, o Windows troca o driver da tela e uma janela aberta pode fechar; o processo separado continua até o fim.
    /// Com finish = true, ele mesmo reinicia/desliga no fim se algo foi removido.
    /// </summary>
    public static System.Diagnostics.Process StartWorker(VideoVendor v, DduOptions o, bool finish)
    {
        Directory.CreateDirectory(AppSettings.Folder);
        File.WriteAllText(JobPath, JsonSerializer.Serialize(new DduJob(v, o)));
        return System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, finish ? "--ddu-worker --finish" : "--ddu-worker")
        {
            UseShellExecute = false, CreateNoWindow = true,
        })!;
    }

    /// <summary>Entrada do processo separado. Código de saída = itens removidos (0 = nada).</summary>
    public static int WorkerMain(bool finish)
    {
        var st = AppSettings.Load();
        DduJob? job = null;
        try { job = JsonSerializer.Deserialize<DduJob>(File.ReadAllText(JobPath)); } catch { }
        if (job == null) return 0;
        int removed = 0;
        // Em Task.Run, fora da thread da janela: um .GetResult() de algo assíncrono nesta thread trava para sempre
        // (foi o que aconteceu no ponto de restauração).
        try { removed = Task.Run(() => Run(job.Vendor, job.Options, st, _ => { })).GetAwaiter().GetResult(); }
        catch (Exception ex) { try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  Erro: {ex.Message}\r\n"); } catch { } }
        try { File.Delete(JobPath); } catch { }
        if (finish && removed > 0) Task.Run(() => Finish(job.Options, st, m => { try { File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {m}\r\n"); } catch { } })).Wait();
        return Math.Min(removed, 250);
    }

    /// <summary>Linhas novas do registro desde a posição informada (para a janela acompanhar o processo separado).</summary>
    public static (List<string> Lines, long Pos) ReadLogFrom(long pos)
    {
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < pos) pos = 0;
            fs.Seek(pos, SeekOrigin.Begin);
            using var r = new StreamReader(fs);
            string all = r.ReadToEnd();
            int cut = all.LastIndexOf('\n');
            if (cut < 0) return (lines, pos);
            foreach (var l in all[..cut].Split('\n'))
            {
                string t = l.TrimEnd('\r');
                if (t.Length > 21 && !t[21..].StartsWith("    ")) lines.Add(t[21..]); // sem data e sem os detalhes por arquivo
            }
            pos += System.Text.Encoding.UTF8.GetByteCount(all[..(cut + 1)]);
        }
        catch { }
        return (lines, pos);
    }

    /// <summary>Há uma limpeza rodando agora em outro processo (o app foi reaberto no meio dela)?</summary>
    public static System.Diagnostics.Process? RunningWorker()
    {
        if (!File.Exists(JobPath)) return null;
        var me = System.Diagnostics.Process.GetCurrentProcess();
        var other = System.Diagnostics.Process.GetProcessesByName(me.ProcessName).FirstOrDefault(p => p.Id != me.Id && SafeStart(p) > DateTime.Now.AddHours(-1));
        if (other == null) { try { File.Delete(JobPath); } catch { } } // sobrou de uma limpeza que já acabou
        return other;
    }

    private static DateTime SafeStart(System.Diagnostics.Process p) { try { return p.StartTime; } catch { return DateTime.MinValue; } }

    public static long LogLength() { try { return new FileInfo(LogPath).Length; } catch { return 0; } }

    // ================================================================== Windows Update

    private const string WuPolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string DriverSearch = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching";

    public static bool WindowsUpdateBlocked(AppSettings st) => st.TweakBackup.ContainsKey("ddu.wu.policy");

    private static void BlockWindowsUpdate(AppSettings st)
    {
        Backup(st, "ddu.wu.policy", WuPolicy, "ExcludeWUDriversInQualityUpdate");
        Backup(st, "ddu.wu.search", DriverSearch, "SearchOrderConfig");
        using (var k = Registry.LocalMachine.CreateSubKey(WuPolicy)) k.SetValue("ExcludeWUDriversInQualityUpdate", 1, RegistryValueKind.DWord);
        using (var k = Registry.LocalMachine.CreateSubKey(DriverSearch)) k.SetValue("SearchOrderConfig", 0, RegistryValueKind.DWord);
        st.Save();
    }

    /// <summary>Volta o Windows Update a instalar drivers como antes.</summary>
    public static void UnblockWindowsUpdate(AppSettings st)
    {
        Restore(st, "ddu.wu.policy", WuPolicy, "ExcludeWUDriversInQualityUpdate");
        Restore(st, "ddu.wu.search", DriverSearch, "SearchOrderConfig");
        st.Save();
    }

    private static void Backup(AppSettings st, string key, string path, string name)
    {
        if (st.TweakBackup.ContainsKey(key)) return;
        using var k = Registry.LocalMachine.OpenSubKey(path);
        st.TweakBackup[key] = k?.GetValue(name) is int i ? i.ToString() : "<ausente>";
    }

    private static void Restore(AppSettings st, string key, string path, string name)
    {
        if (st.TweakBackup.GetValueOrDefault(key) is not string prev) return;
        using var k = Registry.LocalMachine.CreateSubKey(path);
        if (prev == "<ausente>") k.DeleteValue(name, false); else k.SetValue(name, int.Parse(prev), RegistryValueKind.DWord);
        st.TweakBackup.Remove(key);
    }

    // ================================================================== Modo de Segurança

    private const string RunOnce = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";

    /// <summary>
    /// Agenda a limpeza no Modo de Segurança e reinicia. Duas entradas RunOnce com "*" (rodam também no Modo de Segurança):
    /// uma desliga o modo de segurança para o próximo boot logo no login (o PC nunca fica preso nele, mesmo se algo falhar)
    /// e a outra abre o tHg com --ddu, que limpa e termina como escolhido (reiniciar no Windows normal ou desligar).
    /// </summary>
    public static string ScheduleSafeMode(VideoVendor v, DduOptions o, AppSettings st)
    {
        st.DduPending = JsonSerializer.Serialize(new DduJob(v, o));
        st.Save();
        var (code, output) = SecurityAudit.Run("bcdedit.exe", "/set {current} safeboot minimal", 30_000);
        if (code != 0) { st.DduPending = null; st.Save(); return $"O Windows não aceitou iniciar no Modo de Segurança: {output.Trim()}"; }
        using (var k = Registry.LocalMachine.CreateSubKey(RunOnce))
        {
            k.SetValue("*tHgSafeExit", $"\"{Environment.SystemDirectory}\\bcdedit.exe\" /deletevalue {{current}} safeboot");
            k.SetValue("*tHgDDU", $"\"{Environment.ProcessPath}\" --ddu");
        }
        SecurityAudit.Run("shutdown.exe", "/r /t 5 /c \"tHg Optimization: reiniciando no Modo de Segurança para limpar o driver de vídeo\"", 15_000);
        return "";
    }

    public static DduJob? Pending(AppSettings st)
    {
        if (string.IsNullOrEmpty(st.DduPending)) return null;
        try { return JsonSerializer.Deserialize<DduJob>(st.DduPending); } catch { return null; }
    }

    /// <summary>
    /// Fim da limpeza: agenda o instalador do driver novo para o próximo login (ou abre já, sem reiniciar) e
    /// reinicia, desliga ou não faz nada. No Modo de Segurança, sempre tira o PC dele antes.
    /// </summary>
    public static void Finish(DduOptions o, AppSettings st, Action<string>? log = null)
    {
        bool safe = InSafeMode;
        st.DduPending = null; st.Save();
        if (safe) SecurityAudit.Run("bcdedit.exe", "/deletevalue {current} safeboot", 30_000);

        bool installer = o.InstallerPath is string path && File.Exists(path);
        var finish = safe && o.Finish == DduFinish.None ? DduFinish.Restart : o.Finish; // do Modo de Segurança sempre sai
        if (installer && finish == DduFinish.None)
        {
            log?.Invoke("Abrindo o instalador do driver novo…");
            if (st.PendingDriver != null) { st.PendingDriver.Launched = true; st.Save(); }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(o.InstallerPath!) { UseShellExecute = true }); } catch { }
        }
        else if (installer)
        {
            using var k = Registry.LocalMachine.CreateSubKey(RunOnce);
            k.SetValue("tHgDriverInstall", $"\"{o.InstallerPath}\"");
            if (st.PendingDriver != null) { st.PendingDriver.Launched = true; st.Save(); }
            log?.Invoke("O instalador do driver novo abre sozinho depois que você entrar no Windows.");
        }

        if (finish == DduFinish.Restart)
            SecurityAudit.Run("shutdown.exe", "/r /t 10 /c \"tHg Optimization: limpeza concluída, reiniciando\"", 15_000);
        else if (finish == DduFinish.Shutdown)
            SecurityAudit.Run("shutdown.exe", "/s /t 10 /c \"tHg Optimization: limpeza concluída, desligando para a troca da placa\"", 15_000);
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
