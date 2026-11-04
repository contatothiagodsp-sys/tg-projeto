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
    private static readonly AsyncLocal<Func<string>?> Scoped = new();
    [ThreadStatic] private static bool _writing;

    public static string PathOnDisk => Scoped.Value?.Invoke() ?? (_path ??= Path.Combine(AppSettings.Folder, "thg.log"));

    /// <summary>Só para os testes: quem chama, e o que ele iniciar, grava em outro arquivo. Os outros testes em paralelo não entram nele.</summary>
    internal static void UseFile(string path) => Scoped.Value = () => path;

    /// <summary>Só para os testes: o caminho vem desta função, como vem de AppSettings.Folder no app.</summary>
    internal static void UsePathSource(Func<string> source) => Scoped.Value = source;

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
        // Descobrir o caminho pode criar a pasta protegida, e a falha dela registra aqui de novo: sem esta saída, a pilha estouraria.
        if (_writing) return;
        _writing = true;
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
        finally { _writing = false; }
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

    /// <summary>
    /// O JSON pode trazer "lista": null, e o valor padrão da propriedade não volta sozinho. Sem isto, cada leitor teria de se proteger.
    /// Ficam de fora só os campos em que null tem significado: OverlayItems (itens padrão), ComDbBackup e os objetos opcionais.
    /// </summary>
    internal static void EnsureCollections(AppSettings s)
    {
        s.BenchRuns ??= new();
        s.BenchRecords ??= new();
        s.TweakBackup ??= new();
        s.StartupRemoved ??= new();
        s.LightColorOrder ??= new();
        s.GameProfiles ??= new();
        s.CustomGames ??= new();
        s.SuspendedPids ??= new();
        s.BgCorePids ??= new();
        s.ClientDemotedPids ??= new();
        s.StoppedServices ??= new();
        s.Captures ??= new();
        s.BenchLabSessions ??= new();
        s.AbComparisons ??= new();
        s.RemovedApps ??= new();
        s.GameSessions ??= new();
        s.OpenNavGroups ??= new();
        s.PinnedSensors ??= new();
        s.OcProfiles ??= new() { null, null, null, null, null }; // igual ao padrão: uma vaga por perfil
        s.LcdScreens ??= new();
        s.Fans ??= new();
        s.Fans.Channels ??= new();

        // Item null nos dicionários: quem lê usaria o TryGetValue verdadeiro e quebraria no valor.
        foreach (var key in s.GameProfiles.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) s.GameProfiles.Remove(key);
        foreach (var key in s.LcdScreens.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) s.LcdScreens.Remove(key);
        foreach (var profile in s.GameProfiles.Values) profile.Helpers ??= new();
    }

    internal static void Apply(AppSettings s, int logicalProcessors)
    {
        EnsureCollections(s);
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

/// <summary>Janela "O que há de novo": aparece uma vez em cada versão que tiver texto aqui. O histórico fica em NOVIDADES.md.</summary>
public static class WhatsNew
{
    public static string VersionKey(string version) =>
        Version.TryParse(version, out var v) && v.Build >= 0 ? v.ToString(3) : version;

    public static bool ShouldShow(string? lastSeen, string currentVersion) =>
        Notes.ContainsKey(VersionKey(currentVersion)) && lastSeen != VersionKey(currentVersion);

    public static string? NotesFor(string currentVersion) => Notes.GetValueOrDefault(VersionKey(currentVersion));

    private static readonly Dictionary<string, string> Notes = new()
    {
        ["1.8.0"] =
            "• Sensores: favoritos com gráfico, filtros e grupos, e unidades de rede e disco corrigidas.\n" +
            "• Novo: Histórico, em Jogos. Frametime, temperaturas e clocks de cada sessão no mesmo gráfico, para ver o que aconteceu quando travou.\n" +
            "• O TURBO mede antes e depois: Integridade de Memória, agendamento de GPU e timer de 0,5 ms por jogo, sempre com o resultado deste PC.\n" +
            "• Editor visual da curva de fan e overlay próprio por jogo.\n" +
            "• Configurações: o que o app está fazendo, medição de consumo e pacote de diagnóstico para pedir ajuda.",
        ["1.7.7"] =
            "• Segurança: a limpeza do Windows agora ignora pastas com atalho, em vez de apagar o que está do outro lado dele.\n" +
            "• Mais estável: uma falha em limpeza, teste ou Telas LCD vira um aviso, sem fechar o app nem deixar botões travados.\n" +
            "• Reset de overclock diz separadamente o resultado de energia e de clock.\n" +
            "• Painel e prévia das Telas LCD mais leves.",
        ["1.7.6"] =
            "• Segurança: se o Windows não confirmar a proteção de uma pasta do tHg, ela é recusada em vez de usada aberta. Sem a pasta das configurações, o tHg avisa e fecha sem alterar nada.",
        ["1.7.5"] =
            "• Cores RGB: GRB agora é o padrão em conectores endereçáveis e coolers. Se as cores saírem trocadas, use o botão TESTAR CORES e corrija em Avançado.\n" +
            "• Novo: INSTALAR TUDO, na página Driver. Instala sem janelas os drivers novos de NVIDIA e AMD, só com a assinatura do fabricante.\n" +
            "• Novo: RECONECTAR, nas Telas LCD. Quando outro programa prende a porta, o tHg diz qual fechar e tenta de novo sem reiniciar.\n" +
            "• Hubs Lian Li UNI FAN por USB direto (experimental).\n" +
            "• Sensores sem leitura aparecem como Indisponível, e a aba avisa quando o driver PawnIO não está ativo.\n" +
            "• Página de iluminação: Controle Global primeiro, Integrações recolhidas no fim da página.",
        ["1.7.4"] =
            "• Novo: Performance Lab, na página Bench. Grave a mesma cena antes e depois de uma mudança, e o Lab diz se a diferença é real ou está dentro do ruído. Também testa um ajuste de cada vez e gera um relatório com gráfico.\n" +
            "• A versão mostrada agora é o número real do app, em todos os lugares.\n" +
            "• Overclock mais seguro: sem leitura válida e recente de temperatura e tensão, nada é aplicado. Energia e clock são conferidos e voltam sozinhos se o driver não obedecer.\n" +
            "• Cores RGB automáticas: conectores endereçáveis de ASUS, Gigabyte e ASRock usam GRB. Use o botão TESTAR CORES para conferir, e corrija em Avançado se precisar.\n" +
            "• Modo Jogo devolve a prioridade do jogo ao terminar a sessão, a não ser que você a tenha mudado depois.\n" +
            "• Fans sem leitura de sensor voltam para a BIOS em vez de ficar na última rotação.\n" +
            "• Busca (Ctrl+K) rola até o ajuste e o destaca. Diagnóstico mudou para Desempenho · Engasgos.\n" +
            "• Em CPUs X3D de dois CCDs, o jogo sempre prefere o CCD com V-Cache.",
    };
}
