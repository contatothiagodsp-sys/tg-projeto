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

/// <summary>
/// Resultado da conferência. Subject e KeyId só existem em Valid; Code é o HRESULT do WinVerifyTrust (0 = aceito).
/// KeyId é o SHA-256 (hex) da chave pública do certificado que assinou (SubjectPublicKeyInfo): é ele, e não o texto do
/// Subject, que identifica o assinante.
/// </summary>
public readonly record struct SignatureInfo(SignatureState State, string? Subject, int Code, string? Detail = null, string? KeyId = null);

/// <summary>Quem assinou: o titular (texto, só para mostrar) e a identidade da chave pública (o que é comparado).</summary>
internal readonly record struct SignerIdentity(string Subject, string KeyId);

/// <summary>
/// Conferência de binários externos antes de executar: SHA-256 e assinatura Authenticode (WinVerifyTrust, a mesma
/// verificação que o Windows faz). Tudo que o tHg baixa e executa passa por aqui (ou por DriverManager.VerifyPublisher,
/// que exige o fabricante certo), e a conferência é repetida logo antes de executar, não só ao baixar.
///
/// Política de confiança da atualização do próprio tHg (docs/POLITICA-ASSINATURA.md):
/// 1. o SHA-256 publicado tem de bater, sempre;
/// 2. tHg sem assinatura (build local ou release sem certificado): vale o SHA-256, mas instalador com assinatura inválida
///    ou não verificável é recusado mesmo assim;
/// 3. tHg assinado: o instalador tem de ter assinatura válida (cadeia, validade/carimbo de tempo e revogação conferidos
///    pelo Windows) feita com a MESMA chave pública deste tHg, ou com uma chave sucessora listada em
///    <see cref="SuccessorKeys"/>. Mesmo Subject com outra chave é recusado: nome igual não é identidade;
/// 4. renovação do certificado com a mesma chave passa sozinha; troca de chave exige uma versão-ponte, assinada com a
///    chave atual, que liste a chave nova em SuccessorKeys.
/// </summary>
public static class BinaryTrust
{
    private const int TrustENoSignature = unchecked((int)0x800B0100), TrustESubjectFormUnknown = unchecked((int)0x800B0003),
                      TrustEProviderUnknown = unchecked((int)0x800B0001);

    /// <summary>Costuras de teste: a chamada ao WinVerifyTrust e a leitura do certificado do arquivo.</summary>
    internal static Func<string, int> TrustProbe = CorsairSdkInstaller.WinVerifyTrustFile;
    internal static Func<string, SignerIdentity> SignerProbe = ReadSigner;

    /// <summary>
    /// Chaves sucessoras aceitas (SHA-256 hex do SubjectPublicKeyInfo do certificado novo). Vazio: só a própria chave vale.
    /// Para trocar de chave, publique antes uma versão assinada com a chave atual que inclua aqui a chave nova.
    /// A lista vale porque vem de dentro de um tHg cuja assinatura acabou de ser conferida.
    /// </summary>
    internal static IReadOnlySet<string> SuccessorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    public static bool IsSha256Hex(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    private static SignerIdentity ReadSigner(string path)
    {
#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        return new(cert.Subject, KeyIdOf(cert));
    }

    /// <summary>Identidade da chave: SHA-256 do SubjectPublicKeyInfo (algoritmo, parâmetros e chave), em hex.</summary>
    internal static string KeyIdOf(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));

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
            // O Windows aceitou a assinatura, mas sem saber de quem ela é não há como comparar assinantes.
            var signer = SignerProbe(path);
            if (string.IsNullOrEmpty(signer.Subject)) return new(SignatureState.Unverifiable, null, 0, "certificado sem titular");
            if (!IsSha256Hex(signer.KeyId)) return new(SignatureState.Unverifiable, null, 0, "certificado sem chave pública legível");
            return new(SignatureState.Valid, signer.Subject, 0, null, signer.KeyId);
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
    internal static void CheckSameSigner(SignatureInfo mine, SignatureInfo theirs) => CheckSameSigner(mine, theirs, SuccessorKeys);

    internal static void CheckSameSigner(SignatureInfo mine, SignatureInfo theirs, IReadOnlySet<string> successorKeys)
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
        // Daqui em diante este tHg tem assinatura válida: o instalador precisa provar que vem do mesmo assinante.
        if (theirs.State == SignatureState.Unsigned)
            throw new InvalidDataException("O instalador novo não tem assinatura digital, e esta versão do tHg tem: atualização recusada.");
        if (!IsSha256Hex(mine.KeyId) || !IsSha256Hex(theirs.KeyId))
            throw new InvalidDataException("Não foi possível identificar a chave que assinou o tHg ou o instalador: atualização recusada.");
        if (string.Equals(mine.KeyId, theirs.KeyId, StringComparison.OrdinalIgnoreCase)) return; // mesma chave (inclui certificado renovado)
        if (successorKeys.Contains(theirs.KeyId!)) return; // troca de chave anunciada por esta versão
        throw new InvalidDataException(string.Equals(mine.Subject, theirs.Subject, StringComparison.Ordinal)
            ? "O instalador novo traz o mesmo nome de assinante, mas foi assinado com outra chave: atualização recusada. " +
              "Se o certificado do tHg mudou, baixe o instalador na página oficial."
            : $"O instalador novo está assinado por outro emissor ({theirs.Subject}): atualização recusada.");
    }
}
