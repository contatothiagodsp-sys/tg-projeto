using System.Diagnostics;
using System.IO;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

/// <summary>Com atalhos de pasta (junctions) de verdade: o que está do outro lado nunca pode ser apagado.</summary>
public sealed class DduDeleteTreeTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("thg-ddu-").FullName;
    private readonly string _outside;
    private readonly string _precious;

    public DduDeleteTreeTests()
    {
        _outside = Path.Combine(_base, "fora");
        Directory.CreateDirectory(_outside);
        _precious = Path.Combine(_outside, "importante.txt");
        File.WriteAllText(_precious, "não pode sumir");
    }

    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }

    private static void Junction(string link, string target)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private static (int Files, long Bytes, int Left) Delete(string dir, bool keepRoot = false) =>
        DduCleaner.DeleteTree(dir, _ => { }, _ => { }, keepRoot);

    [Fact]
    public void Pasta_normal_e_apagada_inteira_inclusive_ocultos()
    {
        string dir = Path.Combine(_base, "normal");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "12345");
        string oculto = Path.Combine(dir, "sub", "b.txt");
        File.WriteAllText(oculto, "1");
        File.SetAttributes(oculto, FileAttributes.Hidden | FileAttributes.System);

        var (files, bytes, left) = Delete(dir);

        Assert.Equal((2, 6L, 0), (files, bytes, left));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void KeepRoot_mantem_a_raiz_e_apaga_o_conteudo()
    {
        string dir = Path.Combine(_base, "cache");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "a.txt"), "1");

        Delete(dir, keepRoot: true);

        Assert.True(Directory.Exists(dir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir));
    }

    [Fact]
    public void Raiz_junction_remove_so_o_atalho()
    {
        string link = Path.Combine(_base, "link");
        Junction(link, _outside);

        var r = Delete(link);

        Assert.Equal((0, 0L, 0), r);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(_precious));
    }

    [Fact]
    public void Raiz_junction_com_keepRoot_nao_toca_em_nada()
    {
        string link = Path.Combine(_base, "link");
        Junction(link, _outside);

        Delete(link, keepRoot: true);

        Assert.True(Directory.Exists(link));
        Assert.True(File.Exists(_precious));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Subpasta_junction_nao_e_atravessada(bool keepRoot)
    {
        string dir = Path.Combine(_base, "alvo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "1");
        string link = Path.Combine(dir, "link");
        Junction(link, _outside);

        var (files, _, _) = Delete(dir, keepRoot);

        Assert.Equal(1, files); // só o a.txt: nada do outro lado do atalho entra na conta
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(_precious));
    }

    [Fact]
    public void Ancestral_junction_e_detectado()
    {
        string root = Path.Combine(_base, "local");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(_outside, "DXCache"));
        Junction(Path.Combine(root, "NVIDIA"), _outside);

        Assert.True(DduCleaner.HasReparseAncestor(Path.Combine(root, "NVIDIA", "DXCache"), new[] { root }));
    }

    [Fact]
    public void Caminho_normal_nao_acusa_ancestral()
    {
        string root = Path.Combine(_base, "local");
        string dir = Path.Combine(root, "NVIDIA", "DXCache");
        Directory.CreateDirectory(dir);
        Assert.False(DduCleaner.HasReparseAncestor(dir, new[] { root + Path.DirectorySeparatorChar }));
    }

    /// <summary>Atalho acima da pasta-base é escolha do administrador (perfil movido): não bloqueia a limpeza.</summary>
    [Fact]
    public void Atalho_acima_da_pasta_base_nao_conta()
    {
        string real = Path.Combine(_base, "real");
        Directory.CreateDirectory(Path.Combine(real, "local", "NVIDIA"));
        string link = Path.Combine(_base, "perfil-movido");
        Junction(link, real);
        string root = Path.Combine(link, "local");
        Assert.False(DduCleaner.HasReparseAncestor(Path.Combine(root, "NVIDIA"), new[] { root }));
    }

    private static CleanTarget CleanTargetFor(params string[] folders) => new() { Id = "teste", Title = "Teste", Folders = () => folders };

    /// <summary>Com a barra no fim também: Path.GetTempPath() devolve a pasta assim.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("\\")]
    public void WindowsCleanup_raiz_junction_nao_apaga_o_outro_lado(string suffix)
    {
        string link = Path.Combine(_base, "link");
        Junction(link, _outside);
        var messages = new List<string>();
        var target = CleanTargetFor(link + suffix);

        long measured = WindowsCleanup.Measure(target);
        long freed = WindowsCleanup.Clean(target, messages.Add);

        Assert.Equal(0, measured);
        Assert.Equal(0, freed);
        Assert.True(File.Exists(_precious));
        Assert.Contains(messages, m => m.Contains("ignorada"));
    }

    [Fact]
    public void WindowsCleanup_ancestral_junction_nao_apaga_o_outro_lado()
    {
        string sub = Path.Combine(_outside, "DXCache");
        Directory.CreateDirectory(sub);
        string sentinel = Path.Combine(sub, "importante.bin");
        File.WriteAllText(sentinel, "não pode sumir");
        string link = Path.Combine(_base, "NVIDIA");
        Junction(link, _outside);

        long freed = WindowsCleanup.Clean(CleanTargetFor(Path.Combine(link, "DXCache")), _ => { });

        Assert.Equal(0, freed);
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void WindowsCleanup_subpasta_junction_nao_e_atravessada_e_o_resto_e_limpo()
    {
        string dir = Path.Combine(_base, "cache");
        Directory.CreateDirectory(dir);
        string comum = Path.Combine(dir, "a.bin");
        File.WriteAllText(comum, "12345");
        Junction(Path.Combine(dir, "link"), _outside);

        long freed = WindowsCleanup.Clean(CleanTargetFor(dir), _ => { });

        Assert.Equal(5, freed);
        Assert.False(File.Exists(comum));
        Assert.True(File.Exists(_precious));
    }

    /// <summary>Uma pasta com atalho não pode impedir a limpeza das outras do mesmo alvo.</summary>
    [Fact]
    public void WindowsCleanup_pasta_normal_ao_lado_da_junction_continua_sendo_limpa()
    {
        string link = Path.Combine(_base, "link");
        Junction(link, _outside);
        string dir = Path.Combine(_base, "cache");
        Directory.CreateDirectory(dir);
        string comum = Path.Combine(dir, "a.bin");
        File.WriteAllText(comum, "123");

        long freed = WindowsCleanup.Clean(CleanTargetFor(link, dir), _ => { });

        Assert.Equal(3, freed);
        Assert.False(File.Exists(comum));
        Assert.True(File.Exists(_precious));
    }

    [Fact]
    public void Pasta_inexistente_nao_lanca_excecao() =>
        Assert.Equal((0, 0L, 0), Delete(Path.Combine(_base, "nao-existe")));

    [Fact]
    public void WaitStopped_servico_inexistente_volta_na_hora()
    {
        var clock = Stopwatch.StartNew();
        Assert.True(ServiceStatus.WaitStopped("thg-servico-que-nao-existe", TimeSpan.FromSeconds(5)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
    }
}
