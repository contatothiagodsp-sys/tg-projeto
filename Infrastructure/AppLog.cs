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
    private static readonly AsyncLocal<string?> Scoped = new();

    public static string PathOnDisk => Scoped.Value ?? (_path ??= Path.Combine(AppSettings.Folder, "thg.log"));

    /// <summary>Só para os testes: quem chama, e o que ele iniciar, grava em outro arquivo. Os outros testes em paralelo não entram nele.</summary>
    internal static void UseFile(string path) => Scoped.Value = path;

    /// <summary>Só para os testes: nada vai para o registro real do usuário.</summary>
    internal static void RedirectAll(string path) => _path = path;

    public static void Info(string area, string message) => Write("INFO", area, message);

    public static void Warn(string area, string message) => Write("AVISO", area, message);

    public static void Warn(string area, Exception ex) => Write("AVISO", area, $"{ex.GetType().Name}: {ex.Message}");

    /// <summary>Só com a variável de ambiente THG_DEBUG_LOG=1. O caminho quente passa por aqui: normalmente, nem toca no disco.</summary>
    public static bool DebugEnabled { get; internal set; } = Environment.GetEnvironmentVariable("THG_DEBUG_LOG") == "1";

    /// <summary>Falha esperada e recuperável, como um processo que fechou no meio da chamada.</summary>
    public static void Debug(string area, Exception ex)
    {
        if (DebugEnabled) Write("DEBUG", area, $"{ex.GetType().Name}: {ex.Message}");
    }

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

/// <summary>Quando compactar o heap do .NET: janela escondida há um tempo, sem jogo nem teste, e não muito seguido.</summary>
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
    public const int Current = 3;

    public static void Apply(AppSettings s) => Apply(s, Environment.ProcessorCount);

    internal static void Apply(AppSettings s, int logicalProcessors)
    {
        if (s.SettingsVersion < 2)
        {
            s.LightAutoOrder = true;
        }
        if (s.SettingsVersion < 3)
        {
            // Com 12 threads ou mais, o "ligado" salvo podia ser só o padrão antigo, que vinha da contagem de threads. Abaixo disso
            // o padrão era desligado, então um "ligado" foi escolha do usuário e fica. Quem quiser o corte marca a opção de novo.
            if (s.GameModeAvoidCpu0 && logicalProcessors >= 12)
            {
                s.GameModeAvoidCpu0 = false;
                AppLog.Info("config", "Núcleo 0 de volta ao jogo: o padrão antigo vinha da contagem de threads. A opção continua no Modo Jogo.");
            }
            // Só normaliza a escrita: toda escolha manual de ordem continua valendo, e o que não for ordem válida fica intacto e é ignorado.
            foreach (var key in s.LightColorOrder.Keys.ToList())
                if (ColorOrderAuto.TryParseOrder(s.LightColorOrder[key], out var order)) s.LightColorOrder[key] = order.ToString();
            s.LightMaxFps = s.LightMaxFps is >= 1 and <= 30 ? s.LightMaxFps : 30;
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
        ["1.7"] =
            "• Overclock mais seguro: sem leitura válida e recente de temperatura e tensão, nada é aplicado. Energia e clock são conferidos e voltam sozinhos se o driver não obedecer.\n" +
            "• Cores RGB automáticas: conectores endereçáveis de ASUS, Gigabyte e ASRock usam GRB. Use o botão TESTAR CORES para conferir, e corrija em Avançado se precisar.\n" +
            "• Modo Jogo devolve a prioridade do jogo ao terminar a sessão, a não ser que você a tenha mudado depois.\n" +
            "• Fans sem leitura de sensor voltam para a BIOS em vez de ficar na última rotação.\n" +
            "• Busca (Ctrl+K) rola até o ajuste e o destaca. Diagnóstico mudou para Desempenho · Engasgos.\n" +
            "• Em CPUs X3D de dois CCDs, o jogo sempre prefere o CCD com V-Cache.",
        ["1.6"] =
            "• Busca (Ctrl+K) agora acha cada ajuste pelo nome, com ou sem acento.\n" +
            "• Ajustes mostram APLICADO e a página conta quantos estão ligados.\n" +
            "• Iluminação: página mais simples, cores em RGB por padrão e a ordem de cada aparelho sempre à mão na lista.\n" +
            "• Overclock: análise do seu setup e aviso de risco antes do primeiro ajuste.\n" +
            "• Dois ajustes novos, opcionais e experimentais: interrupções e filas de mouse e teclado.\n" +
            "• SHIELD: confere a lista de bloqueio de drivers vulneráveis e avisa sobre o WinRing0.\n" +
            "• TURBO: informa se o PC está pronto para shaders pré-compilados.\n" +
            "• Menos engasgos: o OTIMIZA só limpa a memória em espera, o tHg não joga mais a própria memória no disco e as curvas de fan rodam fora da janela.",
    };
}
