using System.Diagnostics;
using System.IO;
using System.Text;

namespace ThgOptimization.Infrastructure;

/// <summary>Resultado de um programa de linha de comando. TimedOut: o prazo venceu e a árvore foi encerrada (as saídas vêm vazias).</summary>
internal readonly record struct ProcessResult(int Code, string Stdout, string Stderr, bool TimedOut);

/// <summary>
/// Roda um programa de linha de comando com prazo de verdade: ler a saída de forma síncrona bloqueia até ele terminar, e o prazo
/// nunca chegava a contar. É o único lugar que captura saída de processo: SecurityAudit.Run/RunSplit passam por aqui.
/// </summary>
internal static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Teto de cada saída guardada na memória (caracteres). O que passar é lido e descartado, para o programa não travar escrevendo.</summary>
    internal const int MaxOutputChars = 4 * 1024 * 1024;

    /// <summary>
    /// O prazo vale para o programa e para as saídas dele: um filho que herda o stdout mantém o pipe aberto depois que o pai sai, e a
    /// leitura nunca terminaria. Vencido o prazo, mata a árvore e volta sem a saída; o código é -1, ou o real se o programa em si já
    /// tinha terminado.
    /// </summary>
    public static async Task<ProcessResult> RunSplitAsync(string file, string args, TimeSpan timeout, Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        if (encoding != null) psi.StandardOutputEncoding = psi.StandardErrorEncoding = encoding;
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"{Path.GetFileName(file)} não iniciou.");
        // As duas leituras começam antes da espera: com stderr cheio e sem leitor, o programa travaria escrevendo.
        var stdout = ReadCappedAsync(p.StandardOutput, MaxOutputChars);
        var stderr = ReadCappedAsync(p.StandardError, MaxOutputChars);
        var output = Task.WhenAll(stdout, stderr);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            await output.WaitAsync(cts.Token).ConfigureAwait(false);
            return new(p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), false);
        }
        catch (OperationCanceledException)
        {
            bool finished = p.HasExited;
            KillTree(p);
            return new(finished ? p.ExitCode : -1, "", "", true);
        }
        finally
        {
            // Só depois das leituras: fechar um pipe com leitura pendente pode travar, e esse é o caso em que o prazo venceu.
            _ = output.ContinueWith(t =>
            {
                if (t.Exception != null) AppLog.Debug("processo", t.Exception);
                p.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Lê até o fim, guardando no máximo <paramref name="maxChars"/>: uma saída sem fim não cresce na memória do tHg.</summary>
    internal static async Task<string> ReadCappedAsync(TextReader reader, int maxChars)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buf.AsMemory()).ConfigureAwait(false)) > 0)
        {
            int room = maxChars - sb.Length;
            if (room > 0) sb.Append(buf, 0, Math.Min(read, room));
        }
        return sb.ToString();
    }

    private static void KillTree(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // terminou sozinho entre o prazo vencer e o encerramento
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
        {
            AppLog.Warn("processo", ex);
        }
    }

    /// <summary>Devolve a saída padrão. Passou do prazo: mata o programa e os filhos dele e lança TimeoutException.</summary>
    public static async Task<string> RunAsync(string file, string args, TimeSpan timeout)
    {
        var r = await RunSplitAsync(file, args, timeout).ConfigureAwait(false);
        if (r.TimedOut) throw new TimeoutException($"{Path.GetFileName(file)} não terminou em {timeout.TotalSeconds:0.#} s e foi encerrado.");
        return r.Stdout;
    }

    /// <summary>Para quem já roda fora da janela. Não prende a thread da janela numa troca de contexto: RunAsync usa ConfigureAwait(false).</summary>
    public static string Run(string file, string args, TimeSpan? timeout = null) =>
        RunAsync(file, args, timeout ?? DefaultTimeout).GetAwaiter().GetResult();
}
