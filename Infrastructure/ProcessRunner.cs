using System.Diagnostics;

namespace ThgOptimization.Infrastructure;

/// <summary>Roda um programa de linha de comando com prazo de verdade: ler a saída de forma síncrona bloqueia até ele terminar, e o prazo nunca chegava a contar.</summary>
internal static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Devolve a saída padrão. Passou do prazo: mata o programa e os filhos dele e lança TimeoutException.</summary>
    public static async Task<string> RunAsync(string file, string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{file} não iniciou.");
        // As duas leituras começam antes da espera: com stderr cheio e sem leitor, o programa travaria escrevendo.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { AppLog.Error("processo", ex); }
            throw new TimeoutException($"{System.IO.Path.GetFileName(file)} não terminou em {timeout.TotalSeconds:0.#} s e foi encerrado.");
        }
        await stderr.ConfigureAwait(false);
        return await stdout.ConfigureAwait(false);
    }

    /// <summary>Para quem já roda fora da janela. Não prende a thread da janela numa troca de contexto: RunAsync usa ConfigureAwait(false).</summary>
    public static string Run(string file, string args, TimeSpan? timeout = null) =>
        RunAsync(file, args, timeout ?? DefaultTimeout).GetAwaiter().GetResult();
}
