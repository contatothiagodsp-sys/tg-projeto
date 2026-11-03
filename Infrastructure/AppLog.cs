using System.IO;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Registro local do app, para entender um problema relatado. Fica só no PC (nada é enviado), com no máximo dois arquivos de
/// 256 KB. Falhar ao registrar nunca derruba quem chamou. Não registre chaves, senhas nem conteúdo do usuário.
/// </summary>
public static class AppLog
{
    internal const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static string PathOnDisk => _path ??= Path.Combine(AppSettings.Folder, "thg.log");

    /// <summary>Só para os testes: grava em outro arquivo.</summary>
    internal static void UseFile(string path) => _path = path;

    public static void Info(string area, string message) => Write("INFO", area, message);

    public static void Error(string area, Exception ex) => Write("ERRO", area, $"{ex.GetType().Name}: {ex.Message}");

    public static void Error(string area, string message) => Write("ERRO", area, message);

    private static void Write(string level, string area, string message)
    {
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} [{area}] {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}";
            lock (Gate)
            {
                string path = PathOnDisk;
                var info = new FileInfo(path);
                if (info.Exists && info.Length + line.Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, line);
            }
        }
        catch { }
    }
}

/// <summary>Quando devolver memória ao Windows: janela escondida há um tempo, sem jogo nem teste, e não muito seguido.</summary>
public static class MemoryTrimPolicy
{
    public static readonly TimeSpan HiddenFor = TimeSpan.FromSeconds(30), MinInterval = TimeSpan.FromMinutes(10);

    /// <param name="hiddenSince">Desde quando a janela está escondida ou minimizada; null = visível.</param>
    public static bool ShouldTrim(DateTime now, DateTime? hiddenSince, DateTime? lastTrim, bool gameOrTestRunning) =>
        !gameOrTestRunning && hiddenSince is DateTime h && now - h >= HiddenFor && (lastTrim is not DateTime l || now - l >= MinInterval);
}

public readonly record struct MemorySnapshot(long WorkingSetMb, long PeakWorkingSetMb, long PrivateMb, long GcHeapMb)
{
    /// <summary>Metas da 1.6: até 80 MB em repouso, 150 MB com jogo aberto e 400 MB no pico.</summary>
    public string Summary => $"Agora {WorkingSetMb} MB · pico {PeakWorkingSetMb} MB · memória privada {PrivateMb} MB · heap .NET {GcHeapMb} MB · metas: repouso até 80 MB, jogo até 150 MB, pico até 400 MB";

    public static MemorySnapshot Read()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        const long Mb = 1024 * 1024;
        return new(p.WorkingSet64 / Mb, p.PeakWorkingSet64 / Mb, p.PrivateMemorySize64 / Mb, GC.GetTotalMemory(false) / Mb);
    }
}

/// <summary>Migrações do arquivo de configurações, em ordem. Cada uma roda uma vez, inclusive se o app abrir sem salvar.</summary>
public static class SettingsMigration
{
    public const int Current = 2;

    public static void Apply(AppSettings s)
    {
        if (s.SettingsVersion < 2)
        {
            // Até a 1.5, sem entrada salva = RGB. Quem já tem luzes certas não pode ver as cores trocarem depois de atualizar.
            s.LightAutoOrder = false;
        }
        s.SettingsVersion = Current;
    }

    /// <summary>Instalação nova: nada a migrar e nenhuma novidade para mostrar.</summary>
    public static AppSettings NewInstall(string currentVersion) =>
        new() { SettingsVersion = Current, LastSeenVersion = WhatsNew.VersionKey(currentVersion) };
}

/// <summary>Janela "O que há de novo": uma vez por versão Major.Minor, porque o número de execução do CI muda a cada envio.</summary>
public static class WhatsNew
{
    public static string VersionKey(string version) =>
        Version.TryParse(version, out var v) ? $"{v.Major}.{v.Minor}" : version;

    public static bool ShouldShow(string? lastSeen, string currentVersion) =>
        Notes.ContainsKey(VersionKey(currentVersion)) && lastSeen != VersionKey(currentVersion);

    public static string? NotesFor(string currentVersion) => Notes.GetValueOrDefault(VersionKey(currentVersion));

    private static readonly Dictionary<string, string> Notes = new()
    {
        ["1.6"] =
            "• Busca (Ctrl+K) agora acha cada ajuste pelo nome, com ou sem acento.\n" +
            "• Ajustes mostram APLICADO e a página conta quantos estão ligados.\n" +
            "• Iluminação: ordem das cores automática por fabricante em instalações novas. Quem atualizou continua como estava e pode ligar na página de RGB.\n" +
            "• Overclock: análise do seu setup e aviso de risco antes do primeiro ajuste.\n" +
            "• Dois ajustes novos, opcionais e experimentais: interrupções e filas de mouse e teclado.\n" +
            "• SHIELD: confere a lista de bloqueio de drivers vulneráveis e avisa sobre o WinRing0.\n" +
            "• TURBO: informa se o PC está pronto para shaders pré-compilados.\n" +
            "• Menos memória e menos trabalho com a janela escondida.",
    };
}
