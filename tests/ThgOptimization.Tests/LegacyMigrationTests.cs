using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ThgOptimization.Tests;

/// <summary>Só com pastas temporárias: nenhum teste aqui pode chamar AppSettings.Save(), que grava nas configurações reais do PC.</summary>
public sealed class LegacyMigrationTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("thg-mig-").FullName;
    private readonly string _old;
    private readonly string _target;

    public LegacyMigrationTests()
    {
        _old = Path.Combine(_base, "appdata");
        Directory.CreateDirectory(_old);
        _target = Path.Combine(_base, "seguro", "settings.json");
    }

    public void Dispose() { try { Directory.Delete(_base, true); } catch { } }

    private static AppSettings Envenenado() => new()
    {
        PendingDriver = new PendingDriver { Path = @"C:\mal\driver.exe" },
        DduPending = "{\"InstallerPath\":\"C:\\\\mal\\\\x.exe\"}",
        ComDbBackup = new byte[] { 1, 2, 3 },
        CustomGames = new() { new CustomGame { Name = "Mal", Exe = @"C:\mal\jogo.exe", Args = "--x" } },
        GameProfiles = new()
        {
            ["cs2"] = new GameProfile
            {
                Priority = "high", Hz = 240, ExePath = @"C:\mal\cs2.exe", Extra = "--inject",
                Helpers = new() { new HelperProgram { Path = @"C:\mal\ajudante.exe" } },
            },
        },
        StartupRemoved = new()
        {
            new StartupBackup { Kind = "Run", Machine = true, Name = "maquina", Command = @"C:\mal\a.exe" },
            new StartupBackup { Kind = "Folder", Name = "pasta", Backup = @"C:\mal\a.dll", Original = @"C:\Windows\System32\a.dll" },
            new StartupBackup { Kind = "Run", Name = "usuario", Command = "app.exe" },
        },
        SuspendedPids = new() { 1 }, BgCorePids = new() { 2 }, ClientDemotedPids = new() { 3 },
        StoppedServices = new() { "WinDefend" },
        GameModePrevScheme = "x", GameModePrevOverlay = "y", QuietRestore = 1,
        OverlayScale = 1.5, CloseToTray = false,
        BenchRecords = new() { new BenchRecord(new DateTime(2026, 1, 1), "gpu", 123, "ok") },
        TweakBackup = new()
        {
            ["comp.prof.ifeo.CpuPriorityClass@cs2.exe"] = "<ausente>|3",
            ["comp.irq@SYSTEM\\CurrentControlSet\\Enum\\PCI\\x"] = "4|0100",
            ["power"] = "381b4222-f694-41f0-9685-ff5bb260df2e",
            ["windowed"] = "VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;",
            ["mouse.MouseSpeed"] = null,
            ["ruim.caminho"] = @"C:\mal\a.exe",
            ["ruim.comando"] = "1 & calc",
            ["ruim.aspas"] = "a\"b",
            ["ruim.linha"] = "1\n",
            ["ruim.grande"] = new string('a', 257),
            ["sec.uac.lua"] = "d:0",
            ["sec.firewall.publicprofile"] = "off",
            ["net.dns"] = "Ethernet|6.6.6.6",
        },
    };

    [Fact]
    public void Sanitize_tira_tudo_que_executa_ou_restaura_com_privilegio()
    {
        var s = Envenenado();
        LegacySettingsMigration.Sanitize(s);

        Assert.Null(s.PendingDriver);
        Assert.Null(s.DduPending);
        Assert.Null(s.ComDbBackup);
        Assert.Empty(s.CustomGames);
        var p = s.GameProfiles["cs2"];
        Assert.Empty(p.Helpers);
        Assert.Null(p.ExePath);
        Assert.Equal("", p.Extra);
        Assert.Empty(s.SuspendedPids);
        Assert.Empty(s.BgCorePids);
        Assert.Empty(s.ClientDemotedPids);
        Assert.Empty(s.StoppedServices);
        Assert.Null(s.GameModePrevScheme);
        Assert.Null(s.GameModePrevOverlay);
        Assert.Null(s.QuietRestore);
    }

    [Fact]
    public void Sanitize_mantem_so_inicializacao_do_proprio_usuario()
    {
        var s = Envenenado();
        LegacySettingsMigration.Sanitize(s);
        Assert.Equal("usuario", Assert.Single(s.StartupRemoved).Name);
    }

    [Fact]
    public void Sanitize_filtra_as_copias_dos_ajustes()
    {
        var s = Envenenado();
        LegacySettingsMigration.Sanitize(s);
        Assert.Equal(new[] { "comp.irq@SYSTEM\\CurrentControlSet\\Enum\\PCI\\x", "comp.prof.ifeo.CpuPriorityClass@cs2.exe", "mouse.MouseSpeed", "power", "windowed" },
                     s.TweakBackup.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("<ausente>|3", s.TweakBackup["comp.prof.ifeo.CpuPriorityClass@cs2.exe"]);
    }

    [Fact]
    public void Sanitize_mantem_preferencias_perfis_e_historico()
    {
        var s = Envenenado();
        LegacySettingsMigration.Sanitize(s);
        Assert.Equal(1.5, s.OverlayScale);
        Assert.False(s.CloseToTray);
        Assert.Single(s.BenchRecords);
        Assert.Equal("high", s.GameProfiles["cs2"].Priority);
        Assert.Equal(240, s.GameProfiles["cs2"].Hz);
    }

    [Fact]
    public void Run_grava_o_arquivo_limpo_e_ignora_os_outros_arquivos()
    {
        File.WriteAllText(Path.Combine(_old, "settings.json"), JsonSerializer.Serialize(Envenenado()));
        File.WriteAllText(Path.Combine(_old, "ddu-job.json"), "{}");
        File.WriteAllText(Path.Combine(_old, "malware.exe"), "MZ");
        Directory.CreateDirectory(Path.Combine(_old, "drivers"));
        File.WriteAllText(Path.Combine(_old, "drivers", "x.exe"), "MZ");

        Assert.True(LegacySettingsMigration.Run(_old, _target));

        Assert.Equal(new[] { "settings.json" }, Directory.GetFileSystemEntries(Path.GetDirectoryName(_target)!).Select(Path.GetFileName));
        string json = File.ReadAllText(_target);
        Assert.DoesNotContain(@"C:\\mal", json);
        var s = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Null(s.PendingDriver);
        Assert.Equal(1.5, s.OverlayScale);
    }

    [Fact]
    public void Run_aceita_arquivo_com_marca_utf8()
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new AppSettings { OverlayScale = 2 });
        File.WriteAllBytes(Path.Combine(_old, "settings.json"), new byte[] { 0xEF, 0xBB, 0xBF }.Concat(json).ToArray());
        Assert.True(LegacySettingsMigration.Run(_old, _target));
        Assert.Equal(2, JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_target))!.OverlayScale);
    }

    [Fact]
    public void Run_ignora_arquivo_de_1_MB_ou_mais()
    {
        File.WriteAllText(Path.Combine(_old, "settings.json"), new string(' ', 1 << 20) + "{}");
        Assert.False(LegacySettingsMigration.Run(_old, _target));
        Assert.False(File.Exists(_target));
    }

    [Fact]
    public void Run_sem_arquivo_nao_faz_nada()
    {
        Assert.False(LegacySettingsMigration.Run(_old, _target));
        Assert.False(LegacySettingsMigration.Run(Path.Combine(_base, "nao-existe"), _target));
    }

    [Fact]
    public void Run_recusa_pasta_que_e_atalho()
    {
        File.WriteAllText(Path.Combine(_old, "settings.json"), "{}");
        string link = Path.Combine(_base, "link");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_old}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using (var p = Process.Start(psi)!) { p.WaitForExit(); Assert.Equal(0, p.ExitCode); }

        Assert.False(LegacySettingsMigration.Run(link, _target));
        Assert.False(File.Exists(_target));
    }

    [Fact]
    public void Run_nao_sobrescreve_o_que_ja_existe()
    {
        File.WriteAllText(Path.Combine(_old, "settings.json"), "{}");
        Directory.CreateDirectory(Path.GetDirectoryName(_target)!);
        File.WriteAllText(_target, "original");
        Assert.Throws<IOException>(() => LegacySettingsMigration.Run(_old, _target));
        Assert.Equal("original", File.ReadAllText(_target));
    }
}
