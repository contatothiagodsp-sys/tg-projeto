using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using ThgOptimization.Infrastructure;
using Xunit;

namespace ThgOptimization.Tests;

/// <summary>Só pastas temporárias: nenhum teste aqui pode tocar em %ProgramData%\tHgOptimization.</summary>
public class SecureFolderTests : IDisposable
{
    private const string Trusted = "D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "thg-securefolder-" + Guid.NewGuid().ToString("N"));
    private readonly string _log;

    public SecureFolderTests()
    {
        _log = _root + ".log";
        AppLog.UseFile(_log);
    }

    public void Dispose()
    {
        try { File.Delete(_log); } catch { }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private static DirectorySecurity Sddl(string sddl)
    {
        var sec = new DirectorySecurity();
        sec.SetSecurityDescriptorSddlForm(sddl);
        return sec;
    }

    private static void Nothing(string _) { }

    [Theory]
    [InlineData("O:BA" + Trusted)]
    [InlineData("O:SY" + Trusted)]
    public void Regras_corretas_passam(string sddl) => Assert.Null(SecureFolder.FindAclProblem(Sddl(sddl)));

    [Theory]
    [InlineData("O:BU" + Trusted, "dona")] // grupo comum como dono
    [InlineData("O:S-1-5-21-1-2-3-1001" + Trusted, "dona")] // a conta do próprio usuário: sem elevação, ela reescreveria as regras
    [InlineData("O:BAD:AI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", "herdam")]
    [InlineData("O:BAD:PAI(A;OICI;FA;;;SY)", "faltam")]
    [InlineData("O:BAD:PAI(A;OICI;FA;;;BA)", "faltam")]
    [InlineData("O:BAD:PAI(A;OICIIO;FA;;;SY)(A;OICI;FA;;;BA)", "faltam")] // só para o que vier a ser criado, não para a pasta
    [InlineData("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FW;;;BU)", "pode alterar")]
    [InlineData("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;WD)", "pode alterar")]
    [InlineData("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GW;;;BU)", "pode alterar")]
    [InlineData("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GA;;;AU)", "pode alterar")]
    [InlineData("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;WD;;;BU)", "pode alterar")] // WRITE_DAC
    [InlineData("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;WO;;;BU)", "pode alterar")] // WRITE_OWNER
    [InlineData("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;SD;;;BU)", "pode alterar")] // DELETE
    [InlineData("O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICIIO;FW;;;BU)", "pode alterar")] // herdado pelo que for criado dentro
    public void Regras_que_nao_protegem_sao_recusadas(string sddl, string motivo)
    {
        string? problem = SecureFolder.FindAclProblem(Sddl(sddl));
        Assert.NotNull(problem);
        Assert.Contains(motivo, problem);
    }

    [Fact]
    public void Falha_ao_proteger_lanca_em_vez_de_seguir_e_nao_cria_a_pasta()
    {
        var ex = Assert.Throws<UnauthorizedAccessException>(() =>
            SecureFolder.Secure(_root, "downloads", _ => throw new IOException("sem permissão"), Nothing));
        Assert.Contains(Path.Combine(_root, "downloads"), ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
        Assert.False(Directory.Exists(Path.Combine(_root, "downloads")));
    }

    [Fact]
    public void Falha_na_conferencia_lanca_mesmo_com_a_pasta_criada()
    {
        Assert.Throws<UnauthorizedAccessException>(() => SecureFolder.Secure(_root, "downloads",
            d => Directory.CreateDirectory(d), _ => throw new UnauthorizedAccessException("aberta")));
    }

    [Fact]
    public void Pasta_recusada_nao_entra_no_cache_e_a_seguinte_e_conferida_de_novo()
    {
        Assert.Throws<UnauthorizedAccessException>(() => SecureFolder.Secure(_root, "x",
            d => Directory.CreateDirectory(d), _ => throw new UnauthorizedAccessException("aberta")));

        int checks = 0;
        Action<string> count = _ => checks++;
        Assert.Equal(Path.Combine(_root, "x"), SecureFolder.Secure(_root, "x", d => Directory.CreateDirectory(d), count));
        Assert.Equal(2, checks); // raiz e pasta

        // Só depois de aprovada vale o cache.
        SecureFolder.Secure(_root, "x", d => Directory.CreateDirectory(d), count);
        Assert.Equal(2, checks);
    }

    [Fact]
    public void Cada_nivel_e_protegido_e_conferido_do_topo_para_baixo()
    {
        var ensured = new List<string>();
        var verified = new List<string>();
        SecureFolder.Secure(_root, Path.Combine("users", "S-1-5-21"), d => { ensured.Add(d); Directory.CreateDirectory(d); }, verified.Add);

        var expected = new[] { _root, Path.Combine(_root, "users"), Path.Combine(_root, "users", "S-1-5-21") };
        Assert.Equal(expected, ensured);
        Assert.Equal(expected, verified);
    }

    [Fact]
    public void Nivel_recusado_interrompe_antes_de_criar_o_de_baixo()
    {
        Assert.Throws<UnauthorizedAccessException>(() => SecureFolder.Secure(_root, Path.Combine("users", "eu"),
            d => Directory.CreateDirectory(d),
            d => { if (d.EndsWith("users")) throw new UnauthorizedAccessException("aberta"); }));
        Assert.False(Directory.Exists(Path.Combine(_root, "users", "eu")));
    }

    [Fact]
    public void Falha_fica_registrada_com_a_pasta_e_o_motivo()
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            SecureFolder.Secure(_root, "update", _ => throw new IOException("sem permissão"), Nothing));
        string log = File.ReadAllText(_log);
        Assert.Contains("[secure-folder]", log);
        Assert.Contains(Path.Combine(_root, "update"), log);
        Assert.Contains("sem permissão", log);
    }

    /// <summary>AppLog segura a trava dele e pede a pasta protegida: registrar segurando a trava da pasta seria um deadlock.</summary>
    [Fact]
    public void Registro_acontece_fora_da_trava()
    {
        bool? heldDuringLog = null;
        AppLog.UsePathSource(() => { heldDuringLog = Monitor.IsEntered(SecureFolder.Gate); return _log; });
        Assert.Throws<UnauthorizedAccessException>(() =>
            SecureFolder.Secure(_root, "update", _ => throw new IOException("x"), Nothing));
        Assert.False(heldDuringLog);
    }

    /// <summary>A pasta do registro é protegida: se ela falhar, a falha tenta registrar de novo. Sem a saída, a pilha estouraria e derrubaria tudo.</summary>
    [Fact]
    public void Registro_nao_entra_em_recursao_quando_a_pasta_do_registro_falha()
    {
        int calls = 0;
        AppLog.UsePathSource(() =>
        {
            calls++;
            return SecureFolder.Secure(_root, "users", _ => throw new IOException("x"), Nothing);
        });
        AppLog.Error("teste", "qualquer");
        Assert.Equal(1, calls);

        // A saída vale só durante a gravação: o registro seguinte funciona.
        AppLog.UseFile(_log);
        AppLog.Error("teste", "depois");
        Assert.Contains("depois", File.ReadAllText(_log));
    }

    /// <summary>Caminho real, com ACLs de verdade em uma pasta temporária.</summary>
    [Fact]
    public void Pasta_real_termina_protegida_ou_e_recusada()
    {
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        if (!admin)
        {
            Assert.Throws<UnauthorizedAccessException>(() => SecureFolder.Secure(_root, "sub", SecureFolder.EnsureTrusted, SecureFolder.VerifyTrustedDirectory));
            return;
        }

        string dir = SecureFolder.Secure(_root, Path.Combine("users", "eu"), SecureFolder.EnsureTrusted, SecureFolder.VerifyTrustedDirectory);
        Assert.True(Directory.Exists(dir));
        Assert.Null(SecureFolder.FindAclProblem(new DirectoryInfo(dir).GetAccessControl()));
    }

    /// <summary>Instalações existentes: "users" herda as regras da raiz, e versões antigas podem ter deixado o usuário como dono.</summary>
    [Fact]
    public void Pasta_que_ja_existia_herdando_e_com_o_usuario_como_dono_fica_protegida()
    {
        var me = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator)) return; // trocar a dona exige administrador

        string users = Path.Combine(_root, "users");
        var info = Directory.CreateDirectory(users);
        var owned = info.GetAccessControl();
        owned.SetOwner(me.User!);
        info.SetAccessControl(owned);
        Assert.NotNull(SecureFolder.FindAclProblem(new DirectoryInfo(users).GetAccessControl()));

        SecureFolder.Secure(_root, "users", SecureFolder.EnsureTrusted, SecureFolder.VerifyTrustedDirectory);

        var after = new DirectoryInfo(users).GetAccessControl();
        Assert.Null(SecureFolder.FindAclProblem(after));
        Assert.True(((SecurityIdentifier)after.GetOwner(typeof(SecurityIdentifier))!).IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        Assert.Empty(Directory.GetDirectories(_root, "*.nao-confiavel-*"));
    }

    [Fact]
    public void Atalho_de_pasta_e_recusado_pela_conferencia()
    {
        Directory.CreateDirectory(_root);
        string target = Path.Combine(_root, "alvo"), link = Path.Combine(_root, "atalho");
        Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(link, target); }
        catch (IOException) { return; } // sem o direito de criar links neste ambiente
        catch (UnauthorizedAccessException) { return; }

        var ex = Assert.Throws<UnauthorizedAccessException>(() => SecureFolder.VerifyTrustedDirectory(link));
        Assert.Contains("atalho", ex.Message);
    }
}
