using System.Diagnostics;
using System.IO;
using System.Text;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Falhas não tratadas: gravadas em %AppData%\tHgOptimization\falhas.txt com versão, Windows, tempo aberto e memória.
/// Nada sai do PC sozinho: na abertura seguinte o tHg oferece abrir uma issue no GitHub do projeto já preenchida
/// (o usuário vê e revisa o texto antes de enviar), com o nome de usuário do Windows trocado por "%USUARIO%".
/// </summary>
public static class CrashReporter
{
    public static string LogPath => Path.Combine(AppSettings.Folder, "falhas.txt");

    public static void Log(Exception? ex, string where)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            using var me = Process.GetCurrentProcess();
            File.AppendAllText(LogPath,
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · falha na {where} · tHg {AppUpdater.Current} · " +
                $"Windows {Environment.OSVersion.Version} · aberto há {(DateTime.Now - me.StartTime).TotalMinutes:0} min · " +
                $"memória {me.WorkingSet64 >> 20} MB\r\n{ex}\r\n\r\n");
        }
        catch { }
    }

    /// <summary>Falhas gravadas desde a última vez que o usuário viu o aviso (null se nenhuma). Marca como vistas.</summary>
    public static string? TakeNew(AppSettings st)
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (!fi.Exists) { st.CrashLogSeen = 0; return null; }
            long from = st.CrashLogSeen <= fi.Length ? st.CrashLogSeen : 0; // arquivo apagado/recriado: lê do começo
            if (fi.Length == from) return null;
            using var f = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            f.Seek(from, SeekOrigin.Begin);
            string text = new StreamReader(f, Encoding.UTF8).ReadToEnd();
            st.CrashLogSeen = fi.Length;
            st.Save();
            return text.Trim().Length > 0 ? text.Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>Teto do texto copiado: o GitHub aceita 65.536 caracteres numa issue, e sobra espaço para o usuário escrever.</summary>
    internal const int MaxReportChars = 30_000;

    /// <summary>O que o usuário recebe para relatar a falha: a página curta e o texto que vai para a área de transferência.</summary>
    public sealed record Report(string Url, string Text);

    /// <summary>
    /// Relatório pronto para envio (null se o build não tem repositório). O texto da falha NÃO vai no endereço: endereço
    /// longo não passa pelo Explorer do usuário e teria de ser aberto pelo tHg, com administrador. A página abre curta e o
    /// texto, já sem nome de usuário, pasta do perfil e nome do PC, é copiado para o usuário colar e revisar.
    /// </summary>
    public static Report? Prepare(string crashText) =>
        AppUpdater.Repo is string repo
            ? Prepare(repo, AppUpdater.Current.ToString(), Sanitize(crashText, Environment.UserName,
                      Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.MachineName))
            : null;

    internal static Report Prepare(string repo, string version, string sanitized)
    {
        if (sanitized.Length > MaxReportChars) sanitized = sanitized[..MaxReportChars] + "\n…(cortado)";
        string title = $"Falha no tHg {version}";
        const string body = "**O que eu estava fazendo:** (descreva aqui)\n\n**Relatório da falha:** (cole aqui com Ctrl+V: o tHg já copiou o texto)\n";
        return new Report($"https://github.com/{repo}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}",
                          "```\n" + sanitized + "\n```");
    }

    /// <summary>
    /// Tira do texto o que identifica a pessoa ou o PC: a pasta do perfil, o nome de usuário e o nome do computador.
    /// Nomes com menos de 3 letras não são trocados soltos (trocariam pedaços de palavras), mas a pasta do perfil é.
    /// </summary>
    internal static string Sanitize(string text, string? user, string? profile, string? machine)
    {
        if (profile is { Length: > 3 }) text = text.Replace(profile, "%PERFIL%", StringComparison.OrdinalIgnoreCase);
        if (user is { Length: >= 3 }) text = text.Replace(user, "%USUARIO%", StringComparison.OrdinalIgnoreCase);
        if (machine is { Length: >= 3 }) text = text.Replace(machine, "%PC%", StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
