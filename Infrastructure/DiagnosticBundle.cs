using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Text;
using System.Text.Json;
using ThgOptimization.Core.Benchmark;

namespace ThgOptimization.Infrastructure;

/// <summary>O que entra no pacote. Separado da coleta: os testes montam o ZIP sem consultar o Windows. Parte null = não foi possível ler, e o README diz qual.</summary>
internal sealed record DiagnosticInputs
{
    public string? SystemJson { get; init; }
    public string? GpuJson { get; init; }
    public string? SensorsCsv { get; init; }
    public string? DriversCsv { get; init; }
    public string? EventsCsv { get; init; }
    public string? ConfigJson { get; init; }
    public string? ThgLogPath { get; init; }
    public string? CrashLogPath { get; init; }
    public string? DduLogPath { get; init; }
    public string? SessionPath { get; init; }
    public string AppVersion { get; init; } = "";
    public string WindowsVersion { get; init; } = "";
    public DateTime Now { get; init; } = DateTime.Now;
}

/// <summary>
/// Pacote para pedir ajuda: um ZIP com o estado do PC e os registros do app. Fica só no PC, e quem decide enviar é o usuário. Nome da pessoa e do PC saem de todos os textos,
/// e o config.json vai sem chaves, sem caminhos executáveis e sem nada que o tHg executaria.
/// </summary>
public static class DiagnosticBundle
{
    private const int LogTailBytes = 256 * 1024, DduTailBytes = 64 * 1024, MaxEvents = 500;
    internal const string NoDescription = "(sem descrição: o usuário não escreveu o que aconteceu)";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "tHgOptimization", "Suporte");

    /// <summary>
    /// Copia na thread de quem chama o que é da janela (as linhas de sensor) e coleta o resto em segundo plano. Devolve o caminho do ZIP.
    /// </summary>
    public static async Task<string> BuildAsync(AppSettings settings, SensorMonitor monitor, IReadOnlyList<GpuInfo> gpus,
                                                IEnumerable<ReportNode> system, string userDescription, CancellationToken ct)
    {
        string sensors = SensorsCsv(monitor.Rows.ToList());
        string gpuJson = Serialize(gpus.Select(g => new ReportGpu(g.Name, g.Subtitle, g.Sections.Select(ReportBuilder.ToNode).Where(n => n != null).Cast<ReportNode>().ToList())));
        string systemJson = Serialize(system);
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var inputs = new DiagnosticInputs
            {
                SystemJson = systemJson, GpuJson = gpuJson, SensorsCsv = sensors,
                DriversCsv = Try("drivers", DriversCsv), EventsCsv = Try("eventos", EventsCsv),
                ConfigJson = Try("config", () => SanitizedConfig(settings)),
                ThgLogPath = AppLog.PathOnDisk, CrashLogPath = CrashReporter.LogPath, DduLogPath = DduCleaner.LogPath,
                SessionPath = SessionStore.Default.Files().FirstOrDefault(),
                AppVersion = AppUpdater.Current.ToString(), WindowsVersion = Environment.OSVersion.VersionString,
            };
            ct.ThrowIfCancellationRequested();
            return Write(inputs, userDescription, Folder);
        }, ct);
    }

    private static string? Try(string what, Func<string> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is ManagementException or EventLogException or UnauthorizedAccessException or IOException or System.Runtime.InteropServices.COMException or JsonException)
        {
            AppLog.Warn("suporte", $"Parte do pacote não lida ({what}): {ex.GetType().Name}");
            return null;
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Indented);

    /// <summary>Monta o ZIP. Nunca sobrescreve um pacote anterior: o nome ganha um número.</summary>
    internal static string Write(DiagnosticInputs inputs, string userDescription, string folder)
    {
        Directory.CreateDirectory(folder);
        string path = FreeName(folder, $"thg-diagnostico-{inputs.Now:yyyyMMdd-HHmmss}");
        var missing = new List<string>();
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Text(string name, string? content)
            {
                if (content == null) { missing.Add(name); return; }
                using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                writer.Write(Redact(content));
            }
            void Tail(string name, string? source, int bytes)
            {
                string? text = source != null && File.Exists(source) ? ReadTail(source, bytes) : null;
                if (text == null) { missing.Add(name); return; }
                Text(name, text);
            }

            Text("system.json", inputs.SystemJson);
            Text("gpu.json", inputs.GpuJson);
            Text("sensors.csv", inputs.SensorsCsv);
            Text("drivers.csv", inputs.DriversCsv);
            Text("events-24h.csv", inputs.EventsCsv);
            Text("config.json", inputs.ConfigJson);
            Tail("thg.log", inputs.ThgLogPath, LogTailBytes);
            Tail("falhas.txt", inputs.CrashLogPath, LogTailBytes);
            Tail("ddu-log.txt", inputs.DduLogPath, DduTailBytes);
            Tail("session-timeline.jsonl", inputs.SessionPath, int.MaxValue);
            Text("README.txt", Readme(inputs, userDescription, missing));
        }
        return path;
    }

    private static string FreeName(string folder, string stem)
    {
        string path = Path.Combine(folder, stem + ".zip");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{stem}-{i}.zip");
        return path;
    }

    private static string Readme(DiagnosticInputs inputs, string description, IReadOnlyList<string> missing)
    {
        string text = string.IsNullOrWhiteSpace(description) ? NoDescription : description.Trim();
        var sb = new StringBuilder();
        sb.AppendLine("Pacote de diagnóstico do tHg Optimization");
        sb.AppendLine($"Gerado em: {inputs.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Versão do app: {inputs.AppVersion}");
        sb.AppendLine($"Windows: {inputs.WindowsVersion}");
        sb.AppendLine();
        sb.AppendLine("O que aconteceu, nas palavras do usuário:");
        sb.AppendLine(text);
        sb.AppendLine();
        sb.AppendLine("Nome do usuário e do PC foram retirados dos arquivos. O config.json não tem chaves nem caminhos executáveis.");
        if (missing.Count > 0) sb.AppendLine("Sem dados nesta coleta: " + string.Join(", ", missing));
        return sb.ToString();
    }

    /// <summary>O final do arquivo, sem cortar uma linha no meio. Abre compartilhado: o app pode estar gravando nele.</summary>
    private static string? ReadTail(string path, int bytes)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, file.Length - bytes);
            file.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(file, Encoding.UTF8);
            if (start > 0) reader.ReadLine();
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("suporte", ex);
            return null;
        }
    }

    /// <summary>Quem recebe o pacote não precisa saber o nome de ninguém: caminhos e registros citam a pasta do usuário e o nome do PC.</summary>
    internal static string Redact(string text, string? userProfile = null, string? userName = null, string? machine = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        userName ??= Environment.UserName;
        machine ??= Environment.MachineName;
        if (userProfile.Length > 3)
        {
            text = text.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            text = text.Replace(userProfile.Replace(@"\", @"\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase); // caminhos dentro de JSON
        }
        if (userName.Length > 2) text = System.Text.RegularExpressions.Regex.Replace(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(userName)}\b", "<usuario>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (machine.Length > 2) text = System.Text.RegularExpressions.Regex.Replace(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(machine)}\b", "<pc>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text;
    }

    /// <summary>
    /// A cópia sem nada que dê poder a quem lê ou que identifique a pessoa. TweakBackup fica inteiro: é o que explica por que um ajuste não voltou.
    /// Só a cópia muda: as configurações em uso não são tocadas.
    /// </summary>
    internal static string SanitizedConfig(AppSettings settings)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(settings.SerializeSnapshot()) ?? new AppSettings();
        var backup = new SettingsMap(copy.TweakBackup ?? new SettingsMap());
        LegacySettingsMigration.Sanitize(copy);
        copy.TweakBackup = backup;
        copy.HowManyFpsKeyProtected = null;
        copy.LcdImage = null;
        copy.LcdScreens = new();
        copy.Captures = new();
        copy.BenchLabSessions = new();
        return JsonSerializer.Serialize(copy, Indented);
    }

    internal static string Csv(params string?[] fields) => string.Join(",", fields.Select(CsvField));

    private static string CsvField(string? value)
    {
        string text = (value ?? "").Replace("\r", " ").Replace("\n", " ");
        if (text.Length > 0 && "=+-@".Contains(text[0])) text = "'" + text;
        return text.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }

    internal static string SensorsCsv(IReadOnlyList<SensorRow> rows)
    {
        var sb = new StringBuilder("Hardware,Categoria,Sensor,Valor,Min,Max\r\n");
        foreach (var r in rows) sb.Append(Csv(r.Hardware, r.Category, r.Name, r.Value, r.Min, r.Max)).Append("\r\n");
        return sb.ToString();
    }

    private static string DriversCsv()
    {
        var sb = new StringBuilder("Dispositivo,Fabricante,Versao,Data\r\n");
        using var searcher = new ManagementObjectSearcher("SELECT DeviceName, Manufacturer, DriverVersion, DriverDate FROM Win32_PnPSignedDriver");
        using var results = searcher.Get();
        foreach (ManagementObject o in results)
            using (o)
            {
                if (o["DeviceName"] is not string name) continue;
                sb.Append(Csv(name, o["Manufacturer"]?.ToString(), o["DriverVersion"]?.ToString(), o["DriverDate"]?.ToString())).Append("\r\n");
            }
        return sb.ToString();
    }

    /// <summary>Críticos e erros do log Sistema nas últimas 24 h. Só a primeira linha de cada: o resto repete o evento.</summary>
    private static string EventsCsv()
    {
        var sb = new StringBuilder("Quando,Origem,Id,Nivel,Mensagem\r\n");
        string since = DateTime.UtcNow.AddHours(-24).ToString("o");
        var query = new EventLogQuery("System", PathType.LogName, $"*[System[(Level=1 or Level=2) and TimeCreated[@SystemTime>='{since}']]]") { ReverseDirection = true };
        using var reader = new EventLogReader(query);
        int count = 0;
        for (EventRecord? rec = reader.ReadEvent(); rec != null && count < MaxEvents; rec = reader.ReadEvent(), count++)
            using (rec)
            {
                string message;
                try { message = rec.FormatDescription()?.Split('\n')[0].Trim() ?? ""; }
                catch (EventLogException) { message = ""; }
                sb.Append(Csv(rec.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss"), rec.ProviderName, rec.Id.ToString(), rec.LevelDisplayName, message)).Append("\r\n");
            }
        return sb.ToString();
    }
}
