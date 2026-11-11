using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ThgOptimization.Infrastructure;

/// <summary>Situação da assinatura Authenticode de um arquivo. As quatro são diferentes e nunca se misturam.</summary>
public enum SignatureState
{
    /// <summary>O arquivo não tem assinatura (build local, por exemplo).</summary>
    Unsigned,
    /// <summary>Assinatura presente e aceita pelo Windows.</summary>
    Valid,
    /// <summary>Assinatura presente e recusada: arquivo alterado, certificado não confiável, revogado ou expirado sem carimbo.</summary>
    Invalid,
    /// <summary>Não deu para verificar (erro do Windows, arquivo inacessível): não se sabe se é confiável.</summary>
    Unverifiable,
}

/// <summary>Resultado da conferência. Subject só existe em Valid; Code é o HRESULT do WinVerifyTrust (0 = aceito).</summary>
public readonly record struct SignatureInfo(SignatureState State, string? Subject, int Code, string? Detail = null);

/// <summary>
/// Conferência de binários externos antes de executar: SHA-256 e assinatura Authenticode (WinVerifyTrust, a mesma
/// verificação que o Windows faz). Tudo que o tHg baixa e executa passa por aqui (ou por DriverManager.VerifyPublisher,
/// que exige o fabricante certo), e a conferência é repetida logo antes de executar, não só ao baixar.
/// </summary>
public static class BinaryTrust
{
    private const int TrustENoSignature = unchecked((int)0x800B0100), TrustESubjectFormUnknown = unchecked((int)0x800B0003),
                      TrustEProviderUnknown = unchecked((int)0x800B0001);

    /// <summary>Costuras de teste: a chamada ao WinVerifyTrust e a leitura do certificado do arquivo.</summary>
    internal static Func<string, int> TrustProbe = CorsairSdkInstaller.WinVerifyTrustFile;
    internal static Func<string, string> SubjectProbe = ReadSubject;

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    public static bool IsSha256Hex(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    private static string ReadSubject(string path)
    {
#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        return cert.Subject;
    }

    /// <summary>
    /// Assinatura recusada pelo Windows (0x800Bxxxx de certificado/confiança e 0x8009xxxx de conteúdo adulterado).
    /// Qualquer outro código de erro é falha técnica: fica como "não verificável", nunca como "sem assinatura".
    /// </summary>
    internal static SignatureState Classify(int trust) => trust switch
    {
        0 => SignatureState.Valid,
        TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => SignatureState.Unsigned,
        _ when (trust & unchecked((int)0xFFFF0000)) is unchecked((int)0x800B0000) or unchecked((int)0x80090000) => SignatureState.Invalid,
        _ => SignatureState.Unverifiable,
    };

    /// <summary>Confere a assinatura sem lançar exceção: o resultado diz em qual das quatro situações o arquivo está.</summary>
    public static SignatureInfo Inspect(string path)
    {
        int trust;
        try { trust = TrustProbe(path); }
        catch (Exception ex) { return new(SignatureState.Unverifiable, null, 0, $"{ex.GetType().Name}: {ex.Message}"); }
        var state = Classify(trust);
        if (state != SignatureState.Valid) return new(state, null, trust);
        try
        {
            // O Windows aceitou a assinatura, mas sem saber de quem ela é não há como comparar emissores.
            return SubjectProbe(path) is { Length: > 0 } subject
                ? new(SignatureState.Valid, subject, 0)
                : new(SignatureState.Unverifiable, null, 0, "certificado sem titular");
        }
        catch (Exception ex) { return new(SignatureState.Unverifiable, null, 0, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>
    /// Quem assinou o arquivo (Subject do certificado) se a assinatura é válida; null se o arquivo não tem assinatura.
    /// Assinatura presente mas inválida (arquivo alterado, certificado não confiável, expirado sem carimbo) ou impossível
    /// de verificar lança exceção.
    /// </summary>
    public static string? Signer(string path)
    {
        var info = Inspect(path);
        return info.State switch
        {
            SignatureState.Unsigned => null,
            SignatureState.Valid => info.Subject,
            SignatureState.Invalid => throw new InvalidDataException($"Assinatura digital inválida (0x{info.Code:X8})."),
            _ => throw new InvalidDataException($"Não foi possível verificar a assinatura digital ({Describe(info)})."),
        };
    }

    private static string Describe(SignatureInfo info) => info.Detail ?? $"0x{info.Code:X8}";

    /// <summary>
    /// Instalador de atualização do próprio tHg: o SHA-256 tem de bater com o publicado pelo GitHub e a assinatura tem de ser
    /// do mesmo emissor do tHg em execução. Se este tHg é assinado, um instalador sem assinatura ou de outro emissor é
    /// recusado (ninguém troca o instalador por outro, nem tira a assinatura). Builds sem assinatura dependem só do SHA-256.
    /// Se a assinatura deste tHg não puder ser conferida, a atualização é recusada: falha de verificação não vira "sem assinatura".
    /// </summary>
    public static void VerifyUpdate(string path, string expectedSha256) => VerifyUpdate(path, expectedSha256, Environment.ProcessPath);

    internal static void VerifyUpdate(string path, string expectedSha256, string? selfPath)
    {
        if (!IsSha256Hex(expectedSha256))
            throw new InvalidDataException("O SHA-256 publicado para a versão nova é inválido: atualização recusada.");
        if (!Sha256(path).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O arquivo baixado não confere com o publicado (SHA-256 diferente).");
        var mine = selfPath is { Length: > 0 }
            ? Inspect(selfPath)
            : new SignatureInfo(SignatureState.Unverifiable, null, 0, "caminho do tHg em execução desconhecido");
        CheckSameSigner(mine, Inspect(path));
    }

    /// <summary>A decisão, separada da leitura dos arquivos: lança se o instalador não pode ser aceito por este tHg.</summary>
    internal static void CheckSameSigner(SignatureInfo mine, SignatureInfo theirs)
    {
        switch (theirs.State)
        {
            case SignatureState.Invalid:
                throw new InvalidDataException($"Assinatura digital inválida (0x{theirs.Code:X8}).");
            case SignatureState.Unverifiable:
                throw new InvalidDataException($"Não foi possível verificar a assinatura digital do instalador ({Describe(theirs)}): atualização recusada.");
        }
        switch (mine.State)
        {
            case SignatureState.Unsigned:
                return; // build local sem assinatura: vale o SHA-256 publicado
            case SignatureState.Invalid:
                throw new InvalidDataException($"A assinatura digital deste tHg está inválida (0x{mine.Code:X8}): atualização recusada. " +
                                               "Baixe o instalador na página oficial.");
            case SignatureState.Unverifiable:
                throw new InvalidDataException($"Não foi possível verificar a assinatura digital deste tHg ({Describe(mine)}): atualização recusada. " +
                                               "Tente de novo ou baixe o instalador na página oficial.");
        }
        if (!string.Equals(mine.Subject, theirs.Subject, StringComparison.Ordinal))
            throw new InvalidDataException(theirs.State == SignatureState.Unsigned
                ? "O instalador novo não tem assinatura digital, e esta versão do tHg tem: atualização recusada."
                : $"O instalador novo está assinado por outro emissor ({theirs.Subject}): atualização recusada.");
    }
}
