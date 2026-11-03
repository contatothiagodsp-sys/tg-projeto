using System.Diagnostics;
using System.IO;
using LibreHardwareMonitor.Hardware;
using ThgOptimization.Infrastructure;

namespace ThgOptimization.Tests;

public class LcdPortConflictTests
{
    private static ProcessSnapshot P(string name, int pid = 100) => new(pid, name, 0);

    [Theory]
    [InlineData("TURZX")]
    [InlineData("aida64")]
    [InlineData("HWiNFO64")]
    [InlineData("iCUE")]
    [InlineData("Corsair.Service")]
    [InlineData("Razer Synapse 3")]
    public void Known_programs_are_detected(string name)
    {
        Assert.Equal(new[] { name }, TuringConflictDetector.Match(new[] { P(name) }, _ => false));
    }

    [Fact]
    public void Same_program_twice_is_listed_once_and_others_ignored()
    {
        Assert.Equal(new[] { "iCUE" }, TuringConflictDetector.Match(new[] { P("iCUE"), P("icue", 104), P("notepad", 108) }, _ => false));
    }

    [Fact]
    public void Windows_smartscreen_is_not_blamed()
    {
        Assert.Empty(TuringConflictDetector.Match(new[] { P("smartscreen") }, _ => true));
        Assert.Equal(new[] { "SmartScreen" }, TuringConflictDetector.Match(new[] { P("SmartScreen") }, _ => false)); // o app da tela, fora do Windows
    }

    [Fact]
    public void Image_under_windows_counts_as_system()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(TuringConflictDetector.IsWindowsImage(Path.Combine(windows, "System32", "smartscreen.exe")));
        Assert.True(TuringConflictDetector.IsWindowsImage(null));
        Assert.False(TuringConflictDetector.IsWindowsImage(@"D:\Apps\SmartScreen\SmartScreen.exe"));
        Assert.False(TuringConflictDetector.IsWindowsImage(windows + "Evil\\smartscreen.exe"));
    }

    [Fact]
    public void Message_names_the_program_and_the_button()
    {
        var ex = new LcdPortLockedException("COM7", new[] { "TURZX", "aida64" });
        Assert.Equal("COM7", ex.PortName);
        Assert.Contains("TURZX, aida64", ex.Message);
        Assert.Contains("RECONECTAR", ex.Message);
        Assert.IsAssignableFrom<UnauthorizedAccessException>(ex);
        Assert.Contains("RECONECTAR", new LcdPortLockedException("COM7", Array.Empty<string>()).Message);
    }

    [Fact]
    public void Three_attempts_with_growing_waits_then_a_locked_exception()
    {
        var waits = new List<int>();
        int opens = 0;
        var ex = Assert.Throws<LcdPortLockedException>(() => LcdPortOpener.Open<object>("COM9", () => { opens++; throw new UnauthorizedAccessException(); }, () => new[] { "TURZX" }, waits.Add));
        Assert.Equal(3, opens);
        Assert.Equal(new[] { 250, 500 }, waits);
        Assert.Equal(new[] { "TURZX" }, ex.ConflictingProcesses);
    }

    [Fact]
    public void Second_attempt_can_succeed()
    {
        int opens = 0;
        string result = LcdPortOpener.Open("COM9", () => ++opens == 1 ? throw new IOException("reenumerando") : "aberta", null, _ => { });
        Assert.Equal("aberta", result);
    }

    [Fact]
    public void Handshake_failure_does_not_reopen_the_port()
    {
        int opens = 0;
        Assert.Throws<InvalidDataException>(() => LcdPortOpener.Open<object>("COM9", () => { opens++; throw new InvalidDataException(); }, null, _ => { }));
        Assert.Equal(1, opens);
    }

    [Fact]
    public void Candidate_port_is_disposed_when_the_connection_fails()
    {
        using var port = new System.IO.Ports.SerialPort("COM250");
        bool disposed = false;
        port.Disposed += (_, _) => disposed = true;
        Assert.Throws<IOException>(() => LcdPortOpener.Using<object>(port, _ => throw new IOException("handshake")));
        Assert.True(disposed);
    }

    [Fact]
    public void Port_lock_is_recognized_and_other_errors_are_not()
    {
        var locked = new LcdPortLockedException("COM1", new[] { "a" });
        Assert.Same(locked, MainWindow.AsPortLock(locked, "COM1", () => throw new InvalidOperationException("não deveria consultar")));
        var plain = MainWindow.AsPortLock(new UnauthorizedAccessException(), "COM2", () => new[] { "TURZX" });
        Assert.Equal(("COM2", "TURZX"), (plain!.PortName, plain.ConflictingProcesses[0]));
        Assert.Null(MainWindow.AsPortLock(new IOException(), "COM2", () => new[] { "x" }));
    }
}

public class SensorResilienceTests
{
    [Theory]
    [InlineData(SensorType.Clock, null, false, true)]
    [InlineData(SensorType.Clock, 0f, false, true)]
    [InlineData(SensorType.Clock, -1f, false, true)]
    [InlineData(SensorType.Clock, float.NaN, false, true)]
    [InlineData(SensorType.Clock, 4500f, false, false)]
    [InlineData(SensorType.Clock, 4500f, true, true)]
    [InlineData(SensorType.Load, null, false, true)]
    [InlineData(SensorType.Load, float.PositiveInfinity, false, true)]
    [InlineData(SensorType.Load, 0f, false, false)] // CPU parada é leitura válida
    [InlineData(SensorType.Temperature, null, false, false)] // os outros tipos seguem como antes
    public void Clock_and_load_without_a_reading_are_unavailable(SensorType type, float? value, bool failed, bool expected)
    {
        Assert.Equal(expected, SensorMonitor.IsUnavailable(type, value, failed));
    }

    [Fact]
    public void Row_says_unavailable_and_recovers()
    {
        var row = new SensorRow { Type = SensorType.Clock };
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        row.Update(null, null, null, unavailable: true);
        Assert.Equal("Indisponível", row.Value);
        Assert.Null(row.RawValue);
        Assert.Contains(nameof(SensorRow.Value), changes);
        row.Update(4000, null, null);
        Assert.Equal("4000 MHz", row.Value);
    }

    [Fact]
    public void Load_comes_from_the_system_time_delta()
    {
        // Kernel (que já inclui o ocioso) + usuário somam 300 no intervalo, 50 deles ociosos.
        float? load = NativeCpuSensors.LoadPercent((100, 200, 100), (150, 400, 200));
        Assert.Equal(100f * 250 / 300, load!.Value, 2);
    }

    [Fact]
    public void Load_of_an_empty_interval_has_no_value()
    {
        Assert.Null(NativeCpuSensors.LoadPercent((1, 2, 3), (1, 2, 3)));
        Assert.Equal(0f, NativeCpuSensors.LoadPercent((0, 0, 0), (100, 100, 0)));
    }

    [Fact]
    public void Clock_summary_averages_current_and_takes_the_highest_maximum()
    {
        var cpus = new[]
        {
            new NativeCpuSensors.ProcessorPowerInformation { CurrentMhz = 3000, MaxMhz = 4000 },
            new NativeCpuSensors.ProcessorPowerInformation { CurrentMhz = 5000, MaxMhz = 4500 },
        };
        var (avg, max) = NativeCpuSensors.Summarize(cpus);
        Assert.Equal(4000f, avg);
        Assert.Equal(4500f, max);
    }

    [Fact]
    public void Clock_summary_without_values_is_a_failure()
    {
        Assert.Null(NativeCpuSensors.Summarize(new NativeCpuSensors.ProcessorPowerInformation[2]).Avg);
        Assert.Null(NativeCpuSensors.Summarize(ReadOnlySpan<NativeCpuSensors.ProcessorPowerInformation>.Empty).Avg);
    }

    [Fact]
    public void Native_reading_works_on_this_machine()
    {
        var native = new NativeCpuSensors();
        native.Read(wantClock: false, wantLoad: true);
        Thread.Sleep(50);
        var r = native.Read(wantClock: true, wantLoad: true);
        Assert.False(r.ClockFailed);
        Assert.True(r.ClockAvgMhz > 0);
        Assert.False(r.LoadFailed);
    }

    [Fact]
    public void Missing_device_does_not_respond() => Assert.False(NativeCpuSensors.DeviceResponds(@"\\.\NaoExisteTHg"));

    [Fact]
    public void Banner_without_the_driver_asks_to_reinstall()
    {
        string? banner = SensorMonitor.BuildBanner(false, 0, false, "", "", false);
        Assert.Contains("PawnIO", banner);
        Assert.Contains("Reinstale", banner);
    }

    [Fact]
    public void No_banner_when_everything_answers()
    {
        // Ryzen 9000 com HVCI ligado, mas sem falha nenhuma: nada a avisar.
        Assert.Null(SensorMonitor.BuildBanner(true, 0, false, "AMD Ryzen 9 9950X", "AMD Radeon RX 9070", true));
    }

    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D", "", false, "Ryzen 9000")]
    [InlineData("", "AMD Radeon RX 9070 XT", false, "RX 9000")]
    [InlineData("Intel Core i7", "", true, "HVCI")]
    public void Banner_cites_the_known_cause_of_a_real_failure(string cpu, string gpu, bool hvci, string expected)
    {
        Assert.Contains(expected, SensorMonitor.BuildBanner(true, 1, false, cpu, gpu, hvci));
    }

    [Fact]
    public void Older_hardware_does_not_get_the_new_architecture_message()
    {
        string? banner = SensorMonitor.BuildBanner(true, 1, false, "AMD Ryzen 7 5800X", "NVIDIA GeForce RTX 3080", false);
        Assert.DoesNotContain("9000", banner);
    }
}

public class SilentDriverInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "thg-silent-" + Guid.NewGuid().ToString("N"));

    public SilentDriverInstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SilentDriverInstaller Installer(Func<string, string, string?> verify, Func<string, string, CancellationToken, Task<int>> run, string? amdRoot = null) =>
        new(_dir, amdRoot ?? Path.Combine(_dir, "sem-amd"), (_, file, _, _) => File.WriteAllTextAsync(file, "exe"), verify, run);

    private const string Url = "https://exemplo.invalid/driver.exe";

    [Theory]
    [InlineData("NVIDIA", "-s -noreboot")]
    [InlineData("AMD", "-install -boot=false")]
    [InlineData("Intel", "-s -norestart")]
    [InlineData("Realtek", "/quiet /norestart")]
    public void Silent_flags_by_vendor(string vendor, string args) => Assert.Equal(args, SilentDriverInstaller.SilentArgs(vendor));

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(1641, true, true)]
    [InlineData(1, false, false)]
    [InlineData(1603, false, false)]
    public void Exit_codes(int code, bool success, bool reboot)
    {
        Assert.Equal(success, SilentDriverInstaller.IsSuccessExitCode(code));
        Assert.Equal(reboot, SilentDriverInstaller.RequiresReboot(code));
    }

    [Theory]
    [InlineData("CN=NVIDIA Corporation, O=NVIDIA Corporation, C=US", true)]
    [InlineData("CN=\"Advanced Micro Devices, Inc.\", O=\"Advanced Micro Devices, Inc.\"", true)]
    [InlineData("CN=AMD, O=AMD", true)]
    [InlineData("CN=Intel Corporation", true)]
    [InlineData("CN=Microsoft Corporation", true)]
    [InlineData("CN=Realtek Semiconductor Corp.", true)]
    [InlineData("CN=Amdocs Ltd, O=Amdocs", false)] // "AMD" é o nome inteiro, não um pedaço
    [InlineData("CN=NVIDIA Corporation Falsa", false)]
    [InlineData("CN=Fulano, O=Fulano, OU=NVIDIA Corporation", false)]
    [InlineData("", false)]
    public void Trusted_publisher_compares_the_whole_name(string subject, bool expected)
    {
        Assert.Equal(expected, SilentDriverInstaller.IsTrustedPublisher(subject));
    }

    [Theory]
    [InlineData("CN=NVIDIA Corporation", "NVIDIA", true)]
    [InlineData("CN=Microsoft Corporation", "NVIDIA", false)] // cmd.exe também é assinado: não serve como "instalador NVIDIA"
    [InlineData("CN=NVIDIA Corporation", "AMD", false)]
    [InlineData("CN=Realtek Semiconductor Corp.", "Realtek", true)]
    public void Installer_must_be_signed_by_its_own_vendor(string subject, string vendor, bool expected)
    {
        Assert.Equal(expected, SilentDriverInstaller.IsPublisherOf(subject, vendor));
    }

    [Fact]
    public void Unsigned_file_fails_verification()
    {
        string file = Path.Combine(_dir, "sem-assinatura.exe");
        File.WriteAllText(file, "nada");
        Assert.False(SilentDriverInstaller.VerifyAuthenticodeSignature(file, out string summary));
        Assert.NotEmpty(summary);
    }

    [Fact]
    public async Task Refused_signature_never_runs_and_deletes_the_file()
    {
        bool ran = false;
        var r = await Installer((_, _) => "assinatura ruim", (_, _, _) => { ran = true; return Task.FromResult(0); }).InstallAsync("NVIDIA", Url, "NVIDIA 1.0", null, CancellationToken.None);
        Assert.False(ran);
        Assert.False(r.Success);
        Assert.Contains("assinatura ruim", r.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Http_address_is_refused_before_downloading()
    {
        var r = await Installer((_, _) => null, (_, _, _) => Task.FromResult(0)).InstallAsync("NVIDIA", "http://exemplo.invalid/d.exe", "x", null, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Contains("HTTPS", r.Message);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(1, false, false)]
    public async Task Result_follows_the_exit_code_and_the_temp_file_goes_away(int code, bool success, bool reboot)
    {
        string? args = null;
        var r = await Installer((_, _) => null, (_, a, _) => { args = a; return Task.FromResult(code); }).InstallAsync("NVIDIA", Url, "NVIDIA 1.0", null, CancellationToken.None);
        Assert.Equal((success, reboot, (int?)code), (r.Success, r.RebootRequired, r.ExitCode));
        Assert.Equal("-s -noreboot", args);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Stages_are_reported_in_order()
    {
        var stages = new List<string>();
        await Installer((_, _) => null, (_, _, _) => Task.FromResult(0)).InstallAsync("NVIDIA", Url, "x", new Sync(p => stages.Add(p.Stage)), CancellationToken.None);
        Assert.Equal(new[] { "Baixando", "Validando assinatura", "Instalando" }, stages);
    }

    private sealed class Sync : IProgress<DriverInstallProgress>
    {
        private readonly Action<DriverInstallProgress> _report;
        public Sync(Action<DriverInstallProgress> report) => _report = report;
        public void Report(DriverInstallProgress value) => _report(value);
    }

    [Fact]
    public async Task Cancel_propagates_and_cleans_up()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Installer((_, _) => null, (_, _, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(0); }).InstallAsync("NVIDIA", Url, "x", null, cts.Token));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Amd_package_that_only_extracts_runs_the_extracted_setup_after_checking_it()
    {
        string amd = Path.Combine(_dir, "AMD");
        var verified = new List<string>();
        var ran = new List<string>();
        string extracted = Path.Combine(amd, "Bin64", "ATISetup.exe");
        var r = await Installer((f, _) => { verified.Add(f); return null; }, (f, a, _) =>
        {
            ran.Add(f);
            if (ran.Count > 1) return Task.FromResult(0);
            Directory.CreateDirectory(Path.GetDirectoryName(extracted)!);
            File.WriteAllText(extracted, "setup");
            return Task.FromResult(1); // só extraiu
        }, amd).InstallAsync("AMD", Url, "AMD 1.0", null, CancellationToken.None);
        Assert.True(r.Success);
        Assert.Equal(2, ran.Count);
        Assert.Equal(extracted, ran[1], ignoreCase: true);
        Assert.Contains(verified, f => f.Equals(extracted, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Extracted_setup_with_a_bad_signature_never_runs()
    {
        string amd = Path.Combine(_dir, "AMD");
        string extracted = Path.Combine(amd, "Setup.exe");
        int runs = 0;
        var r = await Installer((f, _) => f.Equals(extracted, StringComparison.OrdinalIgnoreCase) ? "plantado" : null, (_, _, _) =>
        {
            runs++;
            Directory.CreateDirectory(amd);
            File.WriteAllText(extracted, "setup");
            return Task.FromResult(1);
        }, amd).InstallAsync("AMD", Url, "AMD 1.0", null, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Equal(1, runs);
        Assert.False(File.Exists(extracted));
    }

    [Fact]
    public void Find_prefers_atisetup_and_ignores_older_files()
    {
        string amd = Path.Combine(_dir, "AMD");
        Directory.CreateDirectory(Path.Combine(amd, "x"));
        string setup = Path.Combine(amd, "Setup.exe"), ati = Path.Combine(amd, "x", "atisetup.exe");
        File.WriteAllText(setup, "s");
        Assert.Equal(setup, SilentDriverInstaller.FindAmdSetup(amd, DateTime.UtcNow.AddMinutes(-1)));
        File.WriteAllText(ati, "a");
        Assert.Equal(ati, SilentDriverInstaller.FindAmdSetup(amd, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Null(SilentDriverInstaller.FindAmdSetup(amd, DateTime.UtcNow.AddMinutes(1))); // versões antigas deixadas lá não valem
        Assert.Null(SilentDriverInstaller.FindAmdSetup(Path.Combine(_dir, "nao-existe"), DateTime.MinValue));
    }

    [Fact]
    public void Install_all_takes_only_what_has_something_new()
    {
        var gpus = new List<GpuDriverStatus>
        {
            new() { Vendor = "NVIDIA", DownloadUrl = "https://a", Installed = "1.0", Latest = "2.0", GpuName = "novo" },
            new() { Vendor = "NVIDIA", DownloadUrl = "https://a", Installed = "2.0", Latest = "2.0", GpuName = "igual" },
            new() { Vendor = "AMD", DownloadUrl = "https://c", Installed = "32.0", Latest = "25.1", GpuName = "amd" },
            new() { Vendor = "Intel", DownloadUrl = "https://b", Installed = "1", Latest = "2", GpuName = "sem automático" },
            new() { Vendor = "AMD", DownloadUrl = "", Installed = "1", Latest = "2", GpuName = "sem link" },
        };
        Assert.Equal(new[] { "novo", "amd" }, MainWindow.DriversToInstall(gpus).Select(g => g.GpuName));
    }
}

public class ShaderCacheCleanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "thg-shader-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "thg-fora-" + Guid.NewGuid().ToString("N"));

    public ShaderCacheCleanTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        foreach (string d in new[] { _root, _outside })
            try { Directory.Delete(d, true); } catch (IOException) { }
    }

    [Fact]
    public void Deletes_files_at_every_level_and_counts_the_bytes()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));
        File.WriteAllText(Path.Combine(_root, "1.bin"), "12345");
        File.WriteAllText(Path.Combine(_root, "a", "b", "2.bin"), "123");
        Assert.Equal(8, ShaderCache.Clean(new[] { _root }));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Junction_in_a_subfolder_is_never_followed()
    {
        string victim = Path.Combine(_outside, "importante.txt");
        File.WriteAllText(victim, "não apagar");
        string link = Path.Combine(_root, "atalho");
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_outside}\"") { CreateNoWindow = true, UseShellExecute = false };
        using (var p = Process.Start(psi)!) p.WaitForExit();
        Assert.True(Directory.Exists(link), "o Windows não criou a junction");
        File.WriteAllText(Path.Combine(_root, "cache.bin"), "12");

        Assert.Equal(2, ShaderCache.Clean(new[] { _root }));
        Assert.True(File.Exists(victim));
        Directory.Delete(link);
    }

    [Fact]
    public void Missing_folder_does_not_throw() => Assert.Equal(0, ShaderCache.Clean(new[] { Path.Combine(_root, "nao-existe") }));
}

public class AdjustedProcessRestoreTests
{
    private sealed class Child : IDisposable
    {
        public Process P { get; } = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul") { CreateNoWindow = true, UseShellExecute = false })!;
        public int Id => P.Id;
        public ProcessPriorityClass Priority { get { P.Refresh(); return P.PriorityClass; } }
        public void Dispose() { try { P.Kill(true); } catch (InvalidOperationException) { } P.Dispose(); }
    }

    private static long Busy => (long)(Environment.ProcessorCount * 3 * 0.5 * TimeSpan.TicksPerSecond);
    private static List<ProcessSnapshot> One(int pid, string name, int step) => new() { new ProcessSnapshot(pid, name, step * Busy) };

    private static void Restrain(BackgroundBalancer b, int pid)
    {
        b.Evaluate(One(pid, "fake", 0), -1, 0, null);
        b.Evaluate(One(pid, "fake", 1), -1, 3, null);
        b.Evaluate(One(pid, "fake", 2), -1, 3, null);
    }

    [Fact]
    public void Balancer_releases_the_process_object_right_after_adjusting()
    {
        using var child = new Child();
        var b = new BackgroundBalancer();
        Process? opened = null;
        b.Open = id => opened = Process.GetProcessById(id);
        Restrain(b, child.Id);
        Assert.Equal(ProcessPriorityClass.BelowNormal, child.Priority);
        Assert.Throws<InvalidOperationException>(() => opened!.Id); // já descartado: nenhum handle fica aberto durante a partida
        b.Open = Process.GetProcessById;
        Assert.Equal(1, b.RestoreAll());
        Assert.Equal(ProcessPriorityClass.Normal, child.Priority);
    }

    [Fact]
    public void Reused_pid_with_another_name_is_left_alone()
    {
        using var child = new Child();
        var b = new BackgroundBalancer();
        Restrain(b, child.Id);
        // No lugar do PID, agora há outro programa.
        b.Open = _ => Process.GetCurrentProcess();
        Assert.Equal(1, b.RestoreAll());
        Assert.Equal(ProcessPriorityClass.BelowNormal, child.Priority);
    }

    [Fact]
    public void Process_that_exited_during_the_session_does_not_break_the_restore()
    {
        var b = new BackgroundBalancer();
        using (var child = new Child())
        {
            Restrain(b, child.Id);
            child.P.Kill(true);
            child.P.WaitForExit();
        }
        Assert.Equal(1, b.RestoreAll());
    }

    [Fact]
    public void Eco_and_cores_keep_no_process_open_either()
    {
        using var child = new Child();
        var eco = new BackgroundEcoQos();
        Process? opened = null;
        eco.Open = id => opened = Process.GetProcessById(id);
        Assert.Equal(1, eco.Apply(One(child.Id, "chrome", 0), -1, null));
        Assert.Throws<InvalidOperationException>(() => opened!.Id);
        eco.Open = Process.GetProcessById;
        Assert.Equal(1, eco.RestoreAll());

        var settings = new AppSettings();
        var cores = new BackgroundCores(settings);
        cores.Open = id => opened = Process.GetProcessById(id);
        child.P.Refresh();
        IntPtr affinity = child.P.ProcessorAffinity;
        Assert.Equal(1, cores.Apply(One(child.Id, "chrome", 0), -1, 0b11, (ulong)(long)affinity, null));
        Assert.Throws<InvalidOperationException>(() => opened!.Id);
        settings.Mutate(settings.BgCorePids.Clear); // Recover gravaria as configurações reais do usuário
        cores.Open = _ => Process.GetCurrentProcess(); // PID reaproveitado: nada volta
        Assert.Equal(1, cores.RestoreAll());
        Assert.Equal(ProcessPriorityClass.BelowNormal, child.Priority);
    }

    [Fact]
    public void Image_path_reads_this_process_and_answers_null_for_a_missing_one()
    {
        Assert.Equal(Environment.ProcessPath, ProcessUtil.ImagePath(Environment.ProcessId), ignoreCase: true);
        Assert.Null(ProcessUtil.ImagePath(0x7FFFFFF0));
    }
}
