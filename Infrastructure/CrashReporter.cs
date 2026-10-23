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
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · falha na {where} · tHg {typeof(CrashReporter).Assembly.GetName().Version} · " +
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

    /// <summary>Página "nova issue" do repositório do app, com título e texto preenchidos (null se o build não tem repositório).</summary>
    public static string? IssueUrl(string crashText)
    {
        if (AppUpdater.Repo is not string repo) return null;
        string body = crashText.Replace(Environment.UserName, "%USUARIO%", StringComparison.OrdinalIgnoreCase);
        if (body.Length > 6000) body = body[..6000] + "\n…(cortado)";
        body = "**O que eu estava fazendo:** (descreva aqui)\n\n```\n" + body + "\n```";
        string title = $"Falha no tHg {typeof(CrashReporter).Assembly.GetName().Version}";
        return $"https://github.com/{repo}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
    }
}
