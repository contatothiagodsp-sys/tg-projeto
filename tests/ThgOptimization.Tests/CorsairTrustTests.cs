using System.IO;
using System.Runtime.InteropServices;

namespace ThgOptimization.Tests;

public sealed class CorsairTrustTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("thg-trust-").FullName;
    private static readonly string[] Corsair = { "Corsair", "Corsair Memory, Inc.", "Corsair Components, Inc." };

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData("CN=\"Corsair Memory, Inc.\", O=\"Corsair Memory, Inc.\", L=Fremont, S=California, C=US")] // certificado real da v4.0.84
    [InlineData("CN=Corsair, O=Outra")]
    [InlineData("CN=Outra, O=\"Corsair Components, Inc.\"")]
    [InlineData("CN=\"CORSAIR MEMORY, INC.\", O=X")]
    public void PublisherMatches_aceita_o_nome_exato(string subject) =>
        Assert.True(DriverManager.PublisherMatches(subject, Corsair));

    [Theory]
    [InlineData("CN=Corsair Falso Ltda, O=Corsair Falso Ltda")]
    [InlineData("CN=Malware, O=Malware, OU=Corsair")]
    [InlineData("CN=\"Corsair Memory, Inc. Falsa\", O=X")]
    [InlineData("CN=Malware Corsair")]
    [InlineData("")]
    public void PublisherMatches_recusa_trecho_do_nome(string subject) =>
        Assert.False(DriverManager.PublisherMatches(subject, Corsair));

    [Fact]
    public void Verify_recusa_dll_sem_assinatura()
    {
        string p = Path.Combine(_dir, "iCUESDK.x64_2019.dll");
        var bytes = new byte[200_000];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        File.WriteAllBytes(p, bytes);
        var ex = Assert.Throws<InvalidOperationException>(() => CorsairSdkInstaller.Verify(p));
        Assert.Contains("assinatura", ex.Message);
        Assert.True(File.Exists(p)); // Verify não apaga: quem decide é o instalador
    }

    [Fact]
    public void Verify_recusa_arquivo_que_nao_e_dll()
    {
        string p = Path.Combine(_dir, "x.dll");
        File.WriteAllBytes(p, new byte[200_000]);
        Assert.Throws<InvalidOperationException>(() => CorsairSdkInstaller.Verify(p));
    }

    [Fact]
    public void Verify_recusa_arquivo_pequeno()
    {
        string p = Path.Combine(_dir, "x.dll");
        File.WriteAllBytes(p, new byte[] { (byte)'M', (byte)'Z' });
        Assert.Throws<InvalidOperationException>(() => CorsairSdkInstaller.Verify(p));
    }

    /// <summary>Assinatura válida, mas de outro fabricante (a CoreLib é assinada pela Microsoft).</summary>
    [Fact]
    public void Verify_recusa_assinatura_de_outro_fabricante()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CorsairSdkInstaller.Verify(typeof(object).Assembly.Location));
        Assert.Contains("outro fabricante", ex.Message);
    }

    /// <summary>Verificar e fechar o estado várias vezes continua dando o mesmo resultado.</summary>
    [Fact]
    public void WinVerifyTrustFile_aceita_arquivo_assinado_repetidamente()
    {
        for (int i = 0; i < 20; i++)
            Assert.Equal(0, CorsairSdkInstaller.WinVerifyTrustFile(typeof(object).Assembly.Location));
    }

    /// <summary>O LoadDll segura o arquivo aberto (FileShare.Read) entre conferir e carregar: o carregador do Windows aceita isso.</summary>
    [Fact]
    public void Dll_segurada_aberta_sem_escrita_ainda_carrega_e_nao_pode_ser_trocada()
    {
        string p = Path.Combine(_dir, "thg-guard-test.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "version.dll"), p);
        using (var guard = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(p, new byte[] { 1 }));
            Assert.True(NativeLibrary.TryLoad(p, out var lib));
            NativeLibrary.Free(lib);
        }
    }
}
