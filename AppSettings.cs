using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ThgOptimization;

public sealed record BenchRun(DateTime Date, double? Multi, double? Single, bool Sustained, string Cpu);

/// <summary>Resultado dos benchmarks de jogo (Test = "gpu", "sim" ou "mem"). Score é usado para a barra do histórico.</summary>
public sealed record BenchRecord(DateTime Date, string Test, double Score, string Summary);

/// <summary>Programa extra que abre antes do jogo (ex.: ferramenta de comando/monitoramento) e pode fechar junto com ele.</summary>
public sealed class HelperProgram
{
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public bool CloseWithGame { get; set; } = true;
    public string Display => System.IO.Path.GetFileName(Path) + (string.IsNullOrWhiteSpace(Args) ? "" : " " + Args);
}

/// <summary>Perfil de um jogo na aba GAME: desempenho, argumentos de gráficos e programas auxiliares.</summary>
public sealed class GameProfile
{
    public string Priority { get; set; } = "normal";   // normal | above | high
    public bool HighPerfPlan { get; set; }
    public bool GpuHigh { get; set; }
    public bool DisableFso { get; set; }
    public string? ExePath { get; set; }
    public string WinMode { get; set; } = "default";   // default | full | borderless | window
    public string Res { get; set; } = "default";       // default | 1280x720 | 1920x1080 | 2560x1440 | 3840x2160
    public int Hz { get; set; }                        // 0 = não definir
    public string Style { get; set; } = "source";      // source | unity | unreal
    public string Extra { get; set; } = "";
    public List<HelperProgram> Helpers { get; set; } = new();
}

/// <summary>Perfil de overclock: limite de energia (W) e teto de clock (MHz, null = sem teto).</summary>
public sealed record OcProfile(double PowerW, uint? CapMhz);

/// <summary>Jogo adicionado à mão (qualquer .exe).</summary>
public sealed class CustomGame
{
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public string Args { get; set; } = "";
}

/// <summary>Configurações em %AppData%\tHgOptimization. A chave da API é cifrada com DPAPI (só este usuário do Windows a lê).</summary>
public sealed class AppSettings
{
    /// <summary>
    /// %ProgramData%\tHgOptimization\users\&lt;SID&gt;, que só administradores alteram (ver SecureFolder). Antes ficava em
    /// %AppData%, onde qualquer programa do usuário escreve, e o tHg (administrador) lê dali o que executar e restaurar.
    /// </summary>
    public static string Folder { get; } = SecureFolder.User;
    /// <summary>Pasta das versões antigas (copiada para a nova na primeira abertura).</summary>
    private static string LegacyFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "tHgOptimization");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public string? HowManyFpsKeyProtected { get; set; }

    // BENCH: últimos resultados (mais recente primeiro)
    public List<BenchRun> BenchRuns { get; set; } = new();
    public List<BenchRecord> BenchRecords { get; set; } = new();

    // OTIMIZA: valores originais dos ajustes do Windows antes da primeira alteração ("<ausente>" = não existia)
    public Dictionary<string, string?> TweakBackup { get; set; } = new();
    /// <summary>ComDB original (reservas de números COM) antes da primeira "liberação".</summary>
    public byte[]? ComDbBackup { get; set; }
    /// <summary>Programas de inicialização removidos, para "Restaurar removidos".</summary>
    public List<StartupBackup> StartupRemoved { get; set; } = new();

    // GAME
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new();
    public List<CustomGame> CustomGames { get; set; } = new();

    // Modo Jogo automático (GAME · Modo Jogo)
    public bool AutoGameMode { get; set; }
    public bool GameModeCleanRam { get; set; } = true;
    public bool GameModePowerPlan { get; set; } = true;
    public bool GameModeAffinity { get; set; }
    public bool GameModeCloseApps { get; set; }
    /// <summary>Timer do Windows em 0,5 ms enquanto o jogo roda.</summary>
    public bool GameModeTimer { get; set; } = true;
    /// <summary>Notificações do Windows silenciadas durante o jogo.</summary>
    public bool GameModeQuiet { get; set; } = true;
    /// <summary>Monitor na taxa máxima quando o jogo abre.</summary>
    public bool GameModeMaxRefresh { get; set; } = true;
    /// <summary>Pausar programas pesados (nomes separados por vírgula) enquanto o jogo roda.</summary>
    public bool GameModeSuspend { get; set; }
    public string GameModeSuspendList { get; set; } = "SignalRgb, SignalRgbLauncher, L-Connect 3, LConnect3, Overwolf";
    /// <summary>Processos pausados agora (para retomar mesmo se o tHg fechar no meio).</summary>
    public List<int> SuspendedPids { get; set; } = new();
    /// <summary>EcoQoS (modo de eficiência) em navegadores, launchers e Discord enquanto o jogo roda.</summary>
    public bool GameModeEcoQos { get; set; } = true;
    /// <summary>Pausar SysMain e Windows Search enquanto o jogo roda.</summary>
    public bool GameModeServices { get; set; }
    /// <summary>Serviços parados pelo Modo Jogo (para religar mesmo se o tHg fechar no meio).</summary>
    public List<string> StoppedServices { get; set; } = new();
    /// <summary>Valor das notificações antes do Modo Jogo silenciar (para voltar mesmo se o app fechar no meio).</summary>
    public int? QuietRestore { get; set; }
    /// <summary>Rebaixa programas de fundo que monopolizam a CPU durante o jogo (como o ProBalance do Process Lasso).</summary>
    public bool GameModeProBalance { get; set; } = true;
    /// <summary>Forçar o DLSS mais novo nos perfis NVIDIA (placas RTX).</summary>
    public bool NvDlssLatest { get; set; } = true;
    /// <summary>Gravações de frametime feitas pelo usuário (resumo; o CSV fica em Documentos).</summary>
    public List<CaptureResult> Captures { get; set; } = new();
    /// <summary>Apps pré-instalados removidos pela Limpeza do Windows (para "Reinstalar removidos").</summary>
    public List<string> RemovedApps { get; set; } = new();
    /// <summary>Último teste de engasgos (latência de drivers).</summary>
    public LatencyResult? LastLatency { get; set; }
    /// <summary>Monitor com G-SYNC / FreeSync ligado: muda a receita dos perfis do driver.</summary>
    public bool HasVrr { get; set; }
    /// <summary>Quando o TURBO foi aplicado pela primeira vez: divide as sessões em "antes" e "depois".</summary>
    public DateTime? TurboAppliedAt { get; set; }
    public List<GameSessionRecord> GameSessions { get; set; } = new();
    /// <summary>Versão do driver de vídeo na última abertura: se mudar, sugere limpar o cache de shaders.</summary>
    public string? LastGpuDriver { get; set; }

    /// <summary>Limpeza do DDU agendada para o Modo de Segurança ("Fabricante|backup|wu|shaders|restos").</summary>
    public string? DduPending { get; set; }

    /// <summary>Instalador de driver baixado e ainda não instalado (ou instalado e ainda não apagado).</summary>
    public PendingDriver? PendingDriver { get; set; }

    // Bandeja do sistema e atalho do overlay
    public bool CloseToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    /// <summary>Modificadores do atalho do overlay (MOD_*). 0 = padrão Ctrl+Shift; 0x8000 = nenhum.</summary>
    public uint OverlayHotkeyMods { get; set; }
    /// <summary>Tecla do atalho do overlay (código virtual). 0 = padrão O.</summary>
    public uint OverlayHotkeyVk { get; set; }

    /// <summary>Categorias do menu que o usuário deixou abertas.</summary>
    public List<string> OpenNavGroups { get; set; } = new();
    /// <summary>Última falha já avisada (para não repetir o aviso).</summary>
    public DateTime? LastCrashSeen { get; set; }
    /// <summary>Até que byte do falhas.txt o usuário já foi avisado (CrashReporter).</summary>
    public long CrashLogSeen { get; set; }

    // Atualizações
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>O app estava só na bandeja quando a atualização começou: reabre do mesmo jeito.</summary>
    public bool ResumeInTray { get; set; }

    // Overclock (NVIDIA): 5 perfis e reaplicar ao abrir
    public List<OcProfile?> OcProfiles { get; set; } = new() { null, null, null, null, null };
    public int OcAutoProfile { get; set; } = -1;

    // Fans (curvas da placa-mãe)
    public FanSettings Fans { get; set; } = new();
    // Telas LCD
    public string? LcdImage { get; set; }
    public string LcdTarget { get; set; } = "round480";
    /// <summary>Rotação da imagem na tela TURZX (graus).</summary>
    public int TurzxRotation { get; set; }
    /// <summary>Configuração de cada tela, pela chave estável dela (ex.: "tl:1:0" = porta 2, fan 1; "turzx"; "com:COM5"; "file").</summary>
    public Dictionary<string, LcdScreenConfig> LcdScreens { get; set; } = new();

    // Aparência
    public bool LightTheme { get; set; }

    // Overlay
    public string OverlayCorner { get; set; } = "TL";
    public double OverlayScale { get; set; } = 1.0;
    /// <summary>Itens escolhidos para o overlay (chaves de OverlayMetrics); null = padrão.</summary>
    public List<string>? OverlayItems { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] // nunca gravar a chave em texto puro
    public string? HowManyFpsKey
    {
        get
        {
            if (string.IsNullOrEmpty(HowManyFpsKeyProtected)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(HowManyFpsKeyProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return null; }
        }
        set
        {
            HowManyFpsKeyProtected = string.IsNullOrWhiteSpace(value)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
        }
    }

    public static AppSettings Load()
    {
        MigrateLegacy(LegacyFolder);
        MigrateLegacy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ResetExplode"));
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    /// <summary>
    /// Primeira abertura desta versão: copia as configurações da pasta antiga (%AppData%\tHgOptimization, ou do RESET EXPLODE)
    /// para a pasta protegida. Perfis, histórico e, principalmente, os valores originais dos ajustes do Windows (sem eles,
    /// "Desfazer tudo" não teria como voltar). A DLL do iCUE não é copiada: ela é baixada e verificada de novo.
    /// </summary>
    private static void MigrateLegacy(string old)
    {
        try
        {
            if (File.Exists(FilePath) || !Directory.Exists(old)) return;
            foreach (string src in Directory.GetFiles(old, "*", SearchOption.AllDirectories))
            {
                if (src.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                string dst = Path.Combine(Folder, Path.GetRelativePath(old, src));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: false);
            }
        }
        catch { }
    }

    // O Modo Jogo salva de uma tarefa de fundo: uma gravação por vez.
    private static readonly object SaveLock = new();

    public void Save()
    {
        lock (SaveLock)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

/// <summary>O que uma tela LCD mostra. Cada tela tem a sua; aplicar numa não muda as outras.</summary>
public sealed class LcdScreenConfig
{
    public string? Image { get; set; }
    public bool Fill { get; set; } = true;
    public bool CpuTemp { get; set; } = true;
    public bool GpuTemp { get; set; } = true;
    public bool CpuLoad { get; set; }
    public bool GpuLoad { get; set; }
    public bool Clock { get; set; }
    public int Brightness { get; set; } = 75;
    /// <summary>Rotação feita na própria imagem (0/90/180/270), sem comando de hardware.</summary>
    public int Rotation { get; set; }
    /// <summary>Último envio confirmado (para mostrar o estado real).</summary>
    public DateTime? LastSent { get; set; }

    public LcdOverlayOptions Overlay() => new() { CpuTemp = CpuTemp, GpuTemp = GpuTemp, CpuLoad = CpuLoad, GpuLoad = GpuLoad, Clock = Clock, Fill = Fill };
}
