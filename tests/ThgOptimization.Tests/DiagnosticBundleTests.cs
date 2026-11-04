using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

public sealed class DiagnosticBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "thg-suporte-" + Guid.NewGuid().ToString("N"));

    public DiagnosticBundleTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File_(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private DiagnosticInputs Inputs() => new()
    {
        SystemJson = "[]", GpuJson = "[]", SensorsCsv = "Hardware", DriversCsv = "Dispositivo", EventsCsv = "Quando", ConfigJson = "{}",
        ThgLogPath = File_("thg.log", "linha"), CrashLogPath = File_("falhas.txt", "falha"), DduLogPath = File_("ddu.txt", "ddu"), SessionPath = File_("s.jsonl", "{}"),
        AppVersion = "1.8.0", WindowsVersion = "Windows 11", Now = new DateTime(2026, 11, 3, 20, 0, 0),
    };

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void Bundle_contem_arquivos_obrigatorios()
    {
        using var zip = ZipFile.OpenRead(DiagnosticBundle.Write(Inputs(), "travou", _dir));
        string[] required = { "system.json", "gpu.json", "sensors.csv", "drivers.csv", "events-24h.csv", "thg.log", "falhas.txt", "ddu-log.txt", "config.json", "session-timeline.jsonl", "README.txt" };
        Assert.Equal(required.OrderBy(n => n), zip.Entries.Select(e => e.Name).OrderBy(n => n));
        Assert.Contains("travou", Read(zip, "README.txt"));
        Assert.Contains("1.8.0", Read(zip, "README.txt"));
    }

    [Fact]
    public void Parte_que_falhou_aparece_no_readme_e_nao_impede_o_pacote()
    {
        var inputs = Inputs() with { DriversCsv = null, DduLogPath = Path.Combine(_dir, "nao-existe") };
        using var zip = ZipFile.OpenRead(DiagnosticBundle.Write(inputs, "x", _dir));
        Assert.Null(zip.GetEntry("drivers.csv"));
        Assert.Contains("drivers.csv", Read(zip, "README.txt"));
        Assert.Contains("ddu-log.txt", Read(zip, "README.txt"));
        Assert.NotNull(zip.GetEntry("system.json"));
    }

    [Fact]
    public void Config_nao_tem_chaves_nem_caminhos_executaveis()
    {
        var s = new AppSettings
        {
            HowManyFpsKeyProtected = "SEGREDO",
            PendingDriver = new PendingDriver(),
            DduPending = "NVIDIA|x",
            LcdImage = @"C:\Users\pessoa\foto.png",
        };
        s.CustomGames.Add(new CustomGame { Name = "x", Exe = @"C:\jogo\malicioso.exe" });
        s.GameProfiles["a"] = new GameProfile { ExePath = @"C:\jogo\outro.exe", Helpers = { new HelperProgram { Path = @"C:\h.exe" } } };
        s.TweakBackup["sec.uac.lua"] = "d:1";
        s.TweakBackup["hags"] = "<ausente>";

        string json = DiagnosticBundle.SanitizedConfig(s);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Null(back.HowManyFpsKeyProtected);
        Assert.Null(back.PendingDriver);
        Assert.Null(back.DduPending);
        Assert.Empty(back.CustomGames);
        Assert.Null(back.GameProfiles["a"].ExePath);
        Assert.DoesNotContain("SEGREDO", json);
        Assert.DoesNotContain(".exe", json);
        Assert.DoesNotContain("foto.png", json);
        Assert.Equal("d:1", back.TweakBackup["sec.uac.lua"]); // o que explica por que um ajuste não voltou continua lá
        Assert.Equal("<ausente>", back.TweakBackup["hags"]);
    }

    [Fact]
    public void Gerar_pacote_nao_mexe_nas_configuracoes_em_uso()
    {
        var s = new AppSettings { HowManyFpsKeyProtected = "SEGREDO" };
        s.CustomGames.Add(new CustomGame { Name = "x" });
        DiagnosticBundle.SanitizedConfig(s);
        Assert.Equal("SEGREDO", s.HowManyFpsKeyProtected);
        Assert.Single(s.CustomGames);
    }

    [Fact]
    public void Descricao_vazia_usa_placeholder()
    {
        using var zip = ZipFile.OpenRead(DiagnosticBundle.Write(Inputs(), "   ", _dir));
        Assert.Contains(DiagnosticBundle.NoDescription, Read(zip, "README.txt"));
    }

    [Fact]
    public void Nome_nao_sobrescreve_existente()
    {
        string first = DiagnosticBundle.Write(Inputs(), "a", _dir), second = DiagnosticBundle.Write(Inputs(), "b", _dir);
        Assert.NotEqual(first, second);
        Assert.EndsWith("-2.zip", second);
        using var zip = ZipFile.OpenRead(first);
        Assert.Contains("a", Read(zip, "README.txt"));
    }

    [Fact]
    public void Nome_da_pessoa_e_do_pc_saem_dos_textos()
    {
        string text = DiagnosticBundle.Redact("""
            Abriu C:\Users\Fulano\Documents\a.txt e "C:\\Users\\Fulano\\b" em DESKTOP-ABC. Fulano. Fulanos fica.
            """, @"C:\Users\Fulano", "Fulano", "DESKTOP-ABC");
        Assert.DoesNotContain(@"\Fulano", text);
        Assert.DoesNotContain("DESKTOP-ABC", text);
        Assert.Contains(@"%USERPROFILE%\Documents\a.txt", text);
        Assert.Contains("<usuario>.", text);
        Assert.Contains("Fulanos", text); // palavra inteira: não corta outras palavras
    }

    [Fact]
    public void Csv_nao_quebra_com_virgula_nem_vira_formula()
    {
        Assert.Equal("\"a,b\",\"x\"\"y\",'=1+1,", DiagnosticBundle.Csv("a,b", "x\"y", "=1+1", null));
    }

    [Fact]
    public void Log_grande_entra_so_no_final_sem_cortar_linha()
    {
        string big = string.Concat(Enumerable.Range(0, 40_000).Select(i => $"linha numero {i}\n"));
        var inputs = Inputs() with { ThgLogPath = File_("grande.log", big) };
        using var zip = ZipFile.OpenRead(DiagnosticBundle.Write(inputs, "x", _dir));
        string log = Read(zip, "thg.log");
        Assert.True(log.Length <= 256 * 1024);
        Assert.StartsWith("linha numero", log);
        Assert.EndsWith("linha numero 39999\n", log);
    }
}
