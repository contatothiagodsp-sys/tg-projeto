using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ThgOptimization;

/// <summary>Jogo com perfil "pro" no driver: executáveis, tipo e se tem NVIDIA Reflex no próprio jogo.</summary>
public sealed record GamePreset(string Name, string[] Exes, bool Competitive, bool Reflex, bool Dlss = false)
{
    public string Kind => Competitive ? "COMPETITIVO" : "ALTA FIDELIDADE";
}

/// <summary>
/// Perfis por jogo no driver NVIDIA (o mesmo banco de perfis do Painel de Controle NVIDIA e do NVIDIA Profile Inspector),
/// via NVAPI DRS, com os IDs públicos de NvApiDriverSettings.h. Cada valor anterior é guardado em TweakBackup com o
/// prefixo "nv." e "Restaurar driver" devolve o perfil a como estava (ou ao padrão da NVIDIA).
///
/// Receitas:
/// - Competitivo (CS2, Valorant, LoL…): Modo de baixa latência ligado, energia "preferir desempenho máximo",
///   filtragem de textura "alto desempenho"; sem VRR: V-Sync desligado e FPS livre (como jogam os profissionais);
///   com G-SYNC/FreeSync: V-Sync ligado no driver + FPS travado um pouco abaixo da taxa do monitor (sem tearing e sem
///   o atraso do V-Sync, a receita do Blur Busters / NVIDIA).
/// - Alta fidelidade (Cyberpunk, RDR2…): baixa latência e desempenho máximo; com VRR, a mesma trava de FPS. A qualidade
///   da imagem fica como o jogo pede.
/// </summary>
public static unsafe class NvDriverProfiles
{
    public static readonly GamePreset[] Catalog =
    {
        new("Counter-Strike 2", new[] { "cs2.exe" }, true, true),
        new("VALORANT", new[] { "VALORANT-Win64-Shipping.exe" }, true, true),
        new("League of Legends", new[] { "League of Legends.exe" }, true, false),
        new("Fortnite", new[] { "FortniteClient-Win64-Shipping.exe" }, true, true, Dlss: true),
        new("Apex Legends", new[] { "r5apex.exe", "r5apex_dx12.exe" }, true, true),
        new("Overwatch 2", new[] { "Overwatch.exe" }, true, true),
        new("Rainbow Six Siege", new[] { "RainbowSix.exe", "RainbowSix_Vulkan.exe" }, true, true, Dlss: true),
        new("Call of Duty", new[] { "cod.exe" }, true, true, Dlss: true),
        new("Marvel Rivals", new[] { "Marvel-Win64-Shipping.exe" }, true, true, Dlss: true),
        new("PUBG: Battlegrounds", new[] { "TslGame.exe" }, true, true, Dlss: true),
        new("Rocket League", new[] { "RocketLeague.exe" }, true, false),
        new("Dota 2", new[] { "dota2.exe" }, true, false),
        new("The Finals", new[] { "Discovery.exe" }, true, true, Dlss: true),
        new("Cyberpunk 2077", new[] { "Cyberpunk2077.exe" }, false, true, Dlss: true),
        new("GTA V", new[] { "GTA5.exe", "GTA5_Enhanced.exe" }, false, false, Dlss: true),
        new("Red Dead Redemption 2", new[] { "RDR2.exe" }, false, false, Dlss: true),
        new("Forza Horizon 5", new[] { "ForzaHorizon5.exe" }, false, false, Dlss: true),
        new("Black Myth: Wukong", new[] { "b1-Win64-Shipping.exe" }, false, true, Dlss: true),
        new("Alan Wake 2", new[] { "AlanWake2.exe" }, false, true, Dlss: true),
        new("Hogwarts Legacy", new[] { "HogwartsLegacy.exe" }, false, true, Dlss: true),
    };

    // ---------------- IDs públicos (NvApiDriverSettings.h) ----------------
    private const uint PRERENDERLIMIT_ID = 0x007BA09E;      // 1 = "Modo de baixa latência: Ligado"
    private const uint PREFERRED_PSTATE_ID = 0x1057EB71;    // 1 = preferir desempenho máximo
    private const uint QUALITY_ENHANCEMENTS_ID = 0x00CE2691; // 0x14 = alto desempenho
    private const uint VSYNCMODE_ID = 0x00A879CF;
    private const uint VSYNC_FORCEOFF = 0x08416747, VSYNC_FORCEON = 0x47814940;
    private const uint FRL_FPS_ID = 0x10835002;              // limite de FPS do driver (0 = sem limite)
    private const uint SHADERCACHE_SIZE_ID = 0x00AC8497;     // tamanho máximo do cache de shaders (0xFFFFFFFF = ilimitado)
    private const uint DLSS_SR_OVERRIDE_ID = 0x10E41E01;     // ligar a troca de preset do DLSS (1)
    private const uint DLSS_SR_PRESET_ID = 0x10E41DF3;       // preset do DLSS Super Resolution (0x00FFFFFF = o mais novo)
    private const uint DLSS_PRESET_LATEST = 0x00FFFFFF;

    /// <summary>Forçar o modelo mais novo do DLSS (Transformer do DLSS 4) nos jogos com DLSS. Só placas RTX.</summary>
    public static bool ForceLatestDlss { get; set; } = true;

    private static readonly Dictionary<uint, string> SettingNames = new()
    {
        [PRERENDERLIMIT_ID] = "Baixa latência", [PREFERRED_PSTATE_ID] = "Energia", [QUALITY_ENHANCEMENTS_ID] = "Filtragem de textura",
        [VSYNCMODE_ID] = "V-Sync", [FRL_FPS_ID] = "Limite de FPS", [SHADERCACHE_SIZE_ID] = "Cache de shaders",
        [DLSS_SR_OVERRIDE_ID] = "Troca de DLSS", [DLSS_SR_PRESET_ID] = "Preset do DLSS",
    };

    /// <summary>Valores da receita para este jogo. vrr = monitor com G-SYNC/FreeSync ligado.</summary>
    public static List<(uint Id, uint Value)> Recipe(GamePreset p, bool vrr, int refreshHz)
    {
        // Cache de shaders ilimitado: o driver não apaga shaders já compilados quando o cache enche (menos engasgos de compilação).
        var r = new List<(uint, uint)> { (PRERENDERLIMIT_ID, 1), (PREFERRED_PSTATE_ID, 1), (SHADERCACHE_SIZE_ID, 0xFFFFFFFF) };
        if (p.Competitive) r.Add((QUALITY_ENHANCEMENTS_ID, 0x14));
        // DLSS mais novo: o jogo continua com a DLL dele, e o driver usa o modelo mais recente (melhor nitidez e menos ghosting),
        // como a opção "DLSS Override" do NVIDIA App.
        if (p.Dlss && ForceLatestDlss) { r.Add((DLSS_SR_OVERRIDE_ID, 1)); r.Add((DLSS_SR_PRESET_ID, DLSS_PRESET_LATEST)); }
        if (vrr)
        {
            r.Add((VSYNCMODE_ID, VSYNC_FORCEON));
            r.Add((FRL_FPS_ID, (uint)VrrCap(refreshHz)));
        }
        else if (p.Competitive)
        {
            r.Add((VSYNCMODE_ID, VSYNC_FORCEOFF));
            r.Add((FRL_FPS_ID, 0));
        }
        return r;
    }

    /// <summary>Trava de FPS para VRR: ~3 FPS (ou 3%) abaixo da taxa do monitor, para nunca encostar no V-Sync.</summary>
    public static int VrrCap(int hz) => Math.Max(30, hz - Math.Max(3, (int)Math.Round(hz * 0.03)));

    public static string Describe(GamePreset p, bool vrr, int hz)
    {
        var parts = new List<string> { "baixa latência", "desempenho máximo", "cache de shaders ilimitado" };
        if (p.Competitive) parts.Add("texturas em alto desempenho");
        if (vrr) parts.Add($"V-Sync + G-SYNC com trava em {VrrCap(hz)} FPS");
        else if (p.Competitive) parts.Add("V-Sync desligado, FPS livre");
        if (p.Dlss && ForceLatestDlss) parts.Add("DLSS mais novo (Transformer)");
        if (p.Reflex) parts.Add("no jogo: NVIDIA Reflex em \"Ligado + Boost\"");
        return string.Join(" · ", parts);
    }

    // ================================================================== API

    private static bool _init;
    private static string? _initError;

    /// <summary>Carrega a NVAPI. null = pronta; senão, o motivo (sem placa/driver NVIDIA).</summary>
    public static string? Init()
    {
        if (_init) return _initError;
        _init = true;
        try
        {
            if (!NativeLibrary.TryLoad("nvapi64.dll", out var lib)) return _initError = "Driver NVIDIA não encontrado (nvapi64.dll).";
            _query = (delegate* unmanaged<uint, IntPtr>)NativeLibrary.GetExport(lib, "nvapi_QueryInterface");
            int r = ((delegate* unmanaged<int>)Fn(0x0150E828))(); // NvAPI_Initialize
            if (r != 0) return _initError = $"A NVAPI não iniciou (código {r}). Sem placa NVIDIA ativa?";
        }
        catch (Exception ex) { return _initError = ex.Message; }
        return _initError = null;
    }

    private static delegate* unmanaged<uint, IntPtr> _query;

    private static IntPtr Fn(uint id)
    {
        IntPtr p = _query(id);
        if (p == IntPtr.Zero) throw new InvalidOperationException($"Função 0x{id:X8} não existe neste driver NVIDIA.");
        return p;
    }

    // Tamanhos com #pragma pack(4) do nvapi.h: versão = sizeof | (ver << 16)
    private const int SettingSize = 12320, AppSize = 20492, ProfileSize = 4116;
    private const uint SettingVer = SettingSize | (1u << 16), AppVer = AppSize | (4u << 16), ProfileVer = ProfileSize | (1u << 16);
    // Deslocamentos dentro de NVDRS_SETTING
    private const int SetIdOff = 4100, SetTypeOff = 4104, SetPredefOff = 4112, SetCurOff = 8220;

    private const int NVAPI_SETTING_NOT_FOUND = -160, NVAPI_PROFILE_NOT_FOUND = -163, NVAPI_EXECUTABLE_NOT_FOUND = -166;

    private sealed class Session : IDisposable
    {
        public IntPtr H;
        public Session()
        {
            IntPtr h;
            Check(((delegate* unmanaged<IntPtr*, int>)Fn(0x0694D52E))(&h), "CreateSession");
            H = h;
            Check(((delegate* unmanaged<IntPtr, int>)Fn(0x375DBD6B))(H), "LoadSettings");
        }
        public void Save() => Check(((delegate* unmanaged<IntPtr, int>)Fn(0xFCBC7E14))(H), "SaveSettings");
        public void Dispose() { if (H != IntPtr.Zero) ((delegate* unmanaged<IntPtr, int>)Fn(0xDAD9CFF8))(H); H = IntPtr.Zero; }
    }

    private static void Check(int r, string what)
    {
        if (r != 0) throw new InvalidOperationException($"NVAPI {what} falhou (código {r}).");
    }

    /// <summary>Texto UTF-16 num buffer NvAPI_UnicodeString (2048 caracteres).</summary>
    private static void PutString(byte* dst, string s)
    {
        var bytes = Encoding.Unicode.GetBytes(s.Length > 2047 ? s[..2047] : s);
        new Span<byte>(dst, 4096).Clear();
        bytes.CopyTo(new Span<byte>(dst, 4096));
    }

    /// <summary>Perfil que contém o .exe (o perfil da NVIDIA para o jogo, se existir) ou um perfil novo "tHg · Jogo".</summary>
    private static IntPtr ProfileFor(Session s, GamePreset p, string exe, AppSettings st, bool create)
    {
        byte* name = stackalloc byte[4096];
        byte* app = (byte*)NativeMemory.AllocZeroed(AppSize);
        try
        {
            PutString(name, exe);
            *(uint*)app = AppVer;
            IntPtr prof;
            int r = ((delegate* unmanaged<IntPtr, byte*, IntPtr*, byte*, int>)Fn(0xEEE566B2))(s.H, name, &prof, app); // FindApplicationByName
            if (r == 0) return prof;
            if (!create || r is not (NVAPI_EXECUTABLE_NOT_FOUND or NVAPI_PROFILE_NOT_FOUND)) { Check(r, "FindApplicationByName"); return IntPtr.Zero; }

            // Sem perfil da NVIDIA para esse .exe: cria "tHg · Jogo" (ou reaproveita, se já existir) e adiciona o .exe.
            string profName = $"tHg · {p.Name}";
            PutString(name, profName);
            r = ((delegate* unmanaged<IntPtr, byte*, IntPtr*, int>)Fn(0x7E4A9A0B))(s.H, name, &prof); // FindProfileByName
            if (r == NVAPI_PROFILE_NOT_FOUND)
            {
                byte* pi = (byte*)NativeMemory.AllocZeroed(ProfileSize);
                try
                {
                    *(uint*)pi = ProfileVer;
                    PutString(pi + 4, profName);
                    Check(((delegate* unmanaged<IntPtr, byte*, IntPtr*, int>)Fn(0xCC176068))(s.H, pi, &prof), "CreateProfile");
                    st.TweakBackup[$"nv.profile.{profName}"] = "created";
                }
                finally { NativeMemory.Free(pi); }
            }
            else Check(r, "FindProfileByName");

            new Span<byte>(app, AppSize).Clear();
            *(uint*)app = AppVer;
            PutString(app + 8, exe);                // appName
            PutString(app + 8 + 4096, p.Name);      // userFriendlyName
            r = ((delegate* unmanaged<IntPtr, IntPtr, byte*, int>)Fn(0x4347A9DE))(s.H, prof, app); // CreateApplication
            if (r != 0 && r != -167 /* já está em outro perfil */) Check(r, "CreateApplication");
            return prof;
        }
        finally { NativeMemory.Free(app); }
    }

    /// <summary>(valor atual, true se é o padrão da NVIDIA) ou null se a configuração não existe no perfil.</summary>
    private static (uint Value, bool Predefined)? GetSetting(Session s, IntPtr prof, uint id)
    {
        byte* st = (byte*)NativeMemory.AllocZeroed(SettingSize);
        try
        {
            *(uint*)st = SettingVer;
            int r = ((delegate* unmanaged<IntPtr, IntPtr, uint, byte*, int>)Fn(0x73BF8338))(s.H, prof, id, st);
            if (r == NVAPI_SETTING_NOT_FOUND) return null;
            Check(r, "GetSetting");
            return (*(uint*)(st + SetCurOff), *(uint*)(st + SetPredefOff) != 0);
        }
        finally { NativeMemory.Free(st); }
    }

    private static void SetSetting(Session s, IntPtr prof, uint id, uint value)
    {
        byte* st = (byte*)NativeMemory.AllocZeroed(SettingSize);
        try
        {
            *(uint*)st = SettingVer;
            *(uint*)(st + SetIdOff) = id;
            *(uint*)(st + SetTypeOff) = 0; // NVDRS_DWORD_TYPE
            *(uint*)(st + SetCurOff) = value;
            Check(((delegate* unmanaged<IntPtr, IntPtr, byte*, int>)Fn(0x577DD202))(s.H, prof, st), "SetSetting");
        }
        finally { NativeMemory.Free(st); }
    }

    /// <summary>Aplica a receita a cada jogo. Devolve (aplicados, erros).</summary>
    public static (int Applied, List<string> Errors) Apply(IEnumerable<GamePreset> presets, bool vrr, int refreshHz, AppSettings st)
    {
        if (Init() is string e) return (0, new List<string> { e });
        int ok = 0; var errors = new List<string>();
        using var s = new Session();
        foreach (var p in presets)
        {
            try
            {
                foreach (string exe in p.Exes)
                {
                    IntPtr prof = ProfileFor(s, p, exe, st, create: true);
                    foreach (var (id, value) in Recipe(p, vrr, refreshHz))
                    {
                        string key = $"nv.{exe}.{id:X8}";
                        if (!st.TweakBackup.ContainsKey(key))
                            st.TweakBackup[key] = GetSetting(s, prof, id) switch
                            {
                                null => "<ausente>", { Predefined: true } => "pre", var (v, _) => $"u:{v}",
                            };
                        SetSetting(s, prof, id, value);
                    }
                }
                ok++;
            }
            catch (Exception ex) { errors.Add($"{p.Name}: {ex.Message}"); }
        }
        s.Save();
        st.Save();
        return (ok, errors);
    }

    /// <summary>Volta cada configuração ao valor anterior (do usuário) ou ao padrão da NVIDIA.</summary>
    public static string RestoreAll(AppSettings st)
    {
        var keys = st.TweakBackup.Keys.Where(k => k.StartsWith("nv.") && !k.StartsWith("nv.profile.")).ToList();
        if (keys.Count == 0) return "";
        if (Init() is string e) return e;
        using var s = new Session();
        var errors = new List<string>();
        foreach (var key in keys)
        {
            try
            {
                int dot = key.LastIndexOf('.');
                string exe = key[3..dot];
                uint id = Convert.ToUInt32(key[(dot + 1)..], 16);
                var preset = Catalog.FirstOrDefault(p => p.Exes.Contains(exe)) ?? new GamePreset(exe, new[] { exe }, true, false);
                IntPtr prof = ProfileFor(s, preset, exe, st, create: false);
                if (prof == IntPtr.Zero) { st.TweakBackup.Remove(key); continue; }
                string prev = st.TweakBackup[key]!;
                int r = prev.StartsWith("u:")
                    ? Try(() => SetSetting(s, prof, id, uint.Parse(prev[2..])))
                    : prev == "pre"
                        ? ((delegate* unmanaged<IntPtr, IntPtr, uint, int>)Fn(0x53F0381E))(s.H, prof, id) // RestoreProfileDefaultSetting
                        : ((delegate* unmanaged<IntPtr, IntPtr, uint, int>)Fn(0xE4A26362))(s.H, prof, id); // DeleteProfileSetting
                if (r != 0 && r != NVAPI_SETTING_NOT_FOUND) errors.Add($"{exe} · {SettingNames.GetValueOrDefault(id, $"0x{id:X8}")}: código {r}");
                st.TweakBackup.Remove(key);
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        }

        // Perfis criados pelo app: apaga (os da NVIDIA nunca são apagados).
        byte* name = stackalloc byte[4096];
        foreach (var key in st.TweakBackup.Keys.Where(k => k.StartsWith("nv.profile.")).ToList())
        {
            PutString(name, key["nv.profile.".Length..]);
            IntPtr prof;
            if (((delegate* unmanaged<IntPtr, byte*, IntPtr*, int>)Fn(0x7E4A9A0B))(s.H, name, &prof) == 0)
                ((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(0x17093206))(s.H, prof); // DeleteProfile
            st.TweakBackup.Remove(key);
        }
        s.Save();
        st.Save();
        return string.Join("; ", errors);
    }

    private static int Try(Action a) { try { a(); return 0; } catch { return -1; } }

    /// <summary>Se a receita está aplicada neste jogo (todas as configurações com o valor da receita).</summary>
    public static bool? IsApplied(GamePreset p, bool vrr, int hz)
    {
        if (Init() != null) return null;
        try
        {
            using var s = new Session();
            foreach (string exe in p.Exes)
            {
                IntPtr prof = ProfileFor(s, p, exe, new AppSettings(), create: false);
                if (prof == IntPtr.Zero) return false;
                foreach (var (id, value) in Recipe(p, vrr, hz))
                    if (GetSetting(s, prof, id)?.Value != value) return false;
            }
            return true;
        }
        catch { return null; }
    }

    // ================================================================== detecção dos jogos instalados

    /// <summary>Jogos do catálogo instalados: pelas bibliotecas detectadas (Steam, Epic…) e pelas pastas padrão da Riot.</summary>
    public static HashSet<GamePreset> DetectInstalled(IEnumerable<LibGame> library)
    {
        var found = new HashSet<GamePreset>();
        var byExe = Catalog.SelectMany(p => p.Exes.Select(e => (Exe: e, Preset: p)))
                           .ToDictionary(x => x.Exe, x => x.Preset, StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true };
        foreach (var g in library)
        {
            if (g.ExePath != null && byExe.TryGetValue(Path.GetFileName(g.ExePath), out var p1)) { found.Add(p1); continue; }
            if (!Directory.Exists(g.InstallDir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(g.InstallDir, "*.exe", opts))
                    if (byExe.TryGetValue(Path.GetFileName(f), out var p2)) { found.Add(p2); break; }
            }
            catch { }
        }
        // Riot Games não entra nas bibliotecas: pastas padrão em cada disco.
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            if (File.Exists(Path.Combine(d.RootDirectory.FullName, @"Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe")))
                found.Add(Catalog.First(p => p.Name == "VALORANT"));
            if (File.Exists(Path.Combine(d.RootDirectory.FullName, @"Riot Games\League of Legends\Game\League of Legends.exe")))
                found.Add(Catalog.First(p => p.Name == "League of Legends"));
        }
        return found;
    }

    public static GamePreset? ByExe(string exeName) =>
        Catalog.FirstOrDefault(p => p.Exes.Any(e => e.Equals(exeName, StringComparison.OrdinalIgnoreCase)));
}
