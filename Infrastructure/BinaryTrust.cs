using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ThgOptimization.Infrastructure;

/// <summary>
/// Conferência de binários externos antes de executar: SHA-256 e assinatura Authenticode (WinVerifyTrust, a mesma
/// verificação que o Windows faz). Tudo que o tHg baixa e executa passa por aqui (ou por DriverManager.VerifyPublisher,
/// que exige o fabricante certo), e a conferência é repetida logo antes de executar, não só ao baixar.
/// </summary>
public static class BinaryTrust
{
    private const int TrustENoSignature = unchecked((int)0x800B0100), TrustESubjectFormUnknown = unchecked((int)0x800B0003);

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    /// <summary>
    /// Quem assinou o arquivo (Subject do certificado) se a assinatura é válida; null se o arquivo não tem assinatura.
    /// Assinatura presente mas inválida (arquivo alterado, certificado não confiável, expirado sem carimbo) lança exceção.
    /// </summary>
    public static string? Signer(string path)
    {
        int trust = CorsairSdkInstaller.WinVerifyTrustFile(path);
        if (trust is TrustENoSignature or TrustESubjectFormUnknown) return null;
        if (trust != 0) throw new InvalidDataException($"Assinatura digital inválida (0x{trust:X8}).");
#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        return cert.Subject;
    }

    /// <summary>
    /// Instalador de atualização do próprio tHg: o SHA-256 tem de bater com o publicado pelo GitHub e a assinatura tem de ser
    /// do mesmo emissor do tHg em execução. Se este tHg é assinado, um instalador sem assinatura ou de outro emissor é
    /// recusado (ninguém troca o instalador por outro, nem tira a assinatura). Builds sem assinatura dependem só do SHA-256.
    /// </summary>
    public static void VerifyUpdate(string path, string expectedSha256)
    {
        if (!Sha256(path).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O arquivo baixado não confere com o publicado (SHA-256 diferente).");
        string? mine = null;
        try { if (Environment.ProcessPath is string self) mine = Signer(self); } catch { }
        string? theirs = Signer(path); // assinatura quebrada: exceção
        if (mine != null && !string.Equals(mine, theirs, StringComparison.Ordinal))
            throw new InvalidDataException(theirs == null
                ? "O instalador novo não tem assinatura digital, e esta versão do tHg tem: atualização recusada."
                : $"O instalador novo está assinado por outro emissor ({theirs}): atualização recusada.");
    }
}
