using System.IO;
using System.Security.Cryptography;

namespace ThgOptimization.Infrastructure;

/// <summary>O instalador não pôde ser executado. Locked: nem deu para travar o arquivo (ele não foi conferido nem apagado).</summary>
internal sealed class LaunchRefusedException(string message, bool locked, Exception? inner = null) : Exception(message, inner)
{
    public bool Locked { get; } = locked;
}

/// <summary>
/// Conferir e executar como uma coisa só. O arquivo é aberto compartilhando apenas leitura (sem escrita, sem exclusão e,
/// por consequência, sem renomear), conferido e iniciado com essa trava ainda aberta: o arquivo que executa é o arquivo
/// conferido. Depois que o processo nasce o Windows mantém a imagem dele mapeada, e a trava pode ser solta.
/// </summary>
internal static class VerifiedLaunch
{
    /// <summary>
    /// Trava, confere e inicia. A conferência recebe a própria trava (o SHA-256 sai dela, não de uma segunda abertura).
    /// Conferência recusada: o arquivo é apagado depois de solto (se <paramref name="deleteRefused"/>) e sobe
    /// <see cref="LaunchRefusedException"/>. Falha ao iniciar sobe como veio, sem apagar nada.
    /// </summary>
    public static T Run<T>(string file, Action<FileStream> verify, Func<T> start, bool deleteRefused = true)
    {
        FileStream hold;
        try { hold = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LaunchRefusedException("Não foi possível travar o instalador para a conferência (outro programa está usando o arquivo, " +
                                             "ou ele não existe mais). Ele não foi executado.", locked: true, ex);
        }
        Exception? refusal;
        using (hold)
        {
            try { verify(hold); refusal = null; }
            catch (Exception ex) { refusal = ex; }
            if (refusal == null) return start();
        }
        if (deleteRefused)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Warn("instalador", ex); }
        }
        throw new LaunchRefusedException(refusal.Message, locked: false, refusal);
    }

    /// <summary>SHA-256 lido pela própria trava, comparado com o esperado (hex, sem diferenciar maiúsculas).</summary>
    public static void CheckSha256(FileStream hold, string expected, string mismatch)
    {
        hold.Position = 0;
        if (!Convert.ToHexString(SHA256.HashData(hold)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(mismatch);
    }
}
