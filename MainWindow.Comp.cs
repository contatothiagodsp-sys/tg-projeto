using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace ThgOptimization;

/// <summary>JOGOS · Competitivo: latência e frametime, rede e input, perfis por jogo e ajustes de baixo nível.</summary>
public partial class MainWindow
{
    private Dictionary<string, List<Tweak>>? _compGroups;
    private List<IrqDevice> _irqDevices = new();
    private bool _compBusy;

    private IEnumerable<Tweak> CompTweaks => _compGroups?.Values.SelectMany(g => g) ?? Enumerable.Empty<Tweak>();

    private async void InitComp()
    {
        if (_compGroups == null)
        {
            _compGroups = WindowsTweaks.CompetitiveGroups(_tweaks, _settings);
            CompLatency.ItemsSource = _compGroups["latency"];
            CompNet.ItemsSource = _compGroups["net"];
            CompGames.ItemsSource = _compGroups["games"];
            CompSystem.ItemsSource = _compGroups["system"];
            CompIrqCore.ItemsSource = new[] { "Padrão do Windows" }
                .Concat(Enumerable.Range(0, Math.Min(Environment.ProcessorCount, 64)).Select(i => $"CPU {i}")).ToList();
        }
        // Leitura (WMI, bcdedit, bibliotecas da Steam) fora da janela.
        var tweaks = CompTweaks.ToList();
        var states = await Task.Run(() => tweaks.Select(t => { try { return (t.Read() ?? false, (string?)null); } catch (Exception ex) { return (false, Err(ex)); } }).ToList());
        for (int i = 0; i < tweaks.Count; i++)
        {
            tweaks[i].IsOn = states[i].Item1;
            if (states[i].Item2 is string err) tweaks[i].Status = $"Não foi possível ler: {err}";
        }
        _irqDevices = await Task.Run(WindowsTweaks.IrqDevices);
        CompIrqDevice.ItemsSource = _irqDevices;
        if (CompIrqDevice.SelectedIndex < 0 && _irqDevices.Count > 0) CompIrqDevice.SelectedIndex = 0;
        if (_irqDevices.Count == 0) CompIrqStatus.Text = "Nenhuma placa de vídeo ou de rede PCI encontrada.";
        var shaders = await Task.Run(WindowsTweaks.Cs2ShaderFolders);
        CompCs2Shader.Text = shaders.Count == 0 ? "Cache do CS2 não encontrado (a Steam cria depois da primeira partida)."
                                                : $"Cache atual: {WindowsCleanup.Size(shaders.Sum(FolderSize))}";
    }

    private async void CompTweak_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Tweak t } box) return;
        bool turnOn = box.IsChecked == true;
        if (!RequireAdmin()) { box.IsChecked = t.IsOn; return; }
        box.IsEnabled = false;
        t.Status = turnOn ? "Aplicando…" : "Desligando…";
        try
        {
            await Task.Run(() => t.Write(turnOn, _settings));
            _settings.Save();
            bool now = await Task.Run(() => t.Read() ?? false);
            t.IsOn = now;
            t.Status = now == turnOn
                ? (turnOn ? "Aplicado." : "Desligado.") + (t.NeedsRestart ? " Reinicie o PC para valer." : "")
                : "O Windows não aceitou a mudança (pode exigir administrador).";
        }
        catch (Exception ex)
        {
            try { t.IsOn = await Task.Run(() => t.Read() ?? false); } catch { }
            t.Status = $"Falhou: {Err(ex)}";
        }
        finally { box.IsEnabled = true; }
        // O timer de 0,5 ms é aplicado pelo Modo Jogo automático: liga o motor se ele ainda não roda.
        if (t.Id == "comp.timer" && _settings.AutoGameMode) StartGameModeEngine();
    }

    private async void BtnCompRestore_Click(object sender, RoutedEventArgs e)
    {
        var tweaks = CompTweaks.ToList();
        var ids = tweaks.Select(t => t.Id).ToList();
        if (!_settings.TweakBackup.Keys.Any(k => k.StartsWith("comp.") || ids.Any(id => k == id || k.StartsWith(id + "."))))
        {
            Toast("Competitivo", "Nenhum ajuste desta página foi alterado ainda.", "");
            return;
        }
        if (!RequireAdmin() || _compBusy) return;
        if (ThgDialog.Show("Voltar todos os ajustes desta página (inclusive a afinidade de interrupções) para como estavam antes do tHg?",
                            "Competitivo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _compBusy = true;
        try
        {
            await Task.Run(() => WindowsTweaks.RestoreCompetitive(tweaks, _settings));
            foreach (var t in tweaks) { try { t.IsOn = await Task.Run(() => t.Read() ?? false); } catch { } t.Status = "Restaurado."; }
            UpdateIrqStatus();
            Toast("Competitivo", "Ajustes restaurados. Reinicie o PC para os que pedem reinício voltarem também.", "");
        }
        catch (Exception ex) { Toast("Competitivo", $"Não foi possível restaurar tudo: {Err(ex)}", ""); }
        finally { _compBusy = false; }
    }

    // ---------------- afinidade de interrupções ----------------

    private void CompIrqDevice_Changed(object sender, SelectionChangedEventArgs e) => UpdateIrqStatus();

    private void UpdateIrqStatus()
    {
        if (CompIrqDevice.SelectedItem is not IrqDevice d) return;
        int? core = null;
        try { core = WindowsTweaks.IrqCore(d); } catch { }
        CompIrqCore.SelectedIndex = core is int c ? c + 1 : 0;
        CompIrqStatus.Text = core is int cc ? $"Interrupções presas na CPU {cc}." : "O Windows escolhe o núcleo (padrão).";
    }

    private void BtnIrqApply_Click(object sender, RoutedEventArgs e)
    {
        if (CompIrqDevice.SelectedItem is not IrqDevice d || CompIrqCore.SelectedIndex < 0 || !RequireAdmin()) return;
        int? core = CompIrqCore.SelectedIndex == 0 ? null : CompIrqCore.SelectedIndex - 1;
        try
        {
            WindowsTweaks.IrqSet(d, core, _settings);
            _settings.Save();
            CompIrqStatus.Text = (core is int c ? $"{d.Name}: interrupções na CPU {c}." : $"{d.Name}: de volta ao padrão do Windows.") +
                                 " Reinicie o PC para valer.";
        }
        catch (Exception ex) { CompIrqStatus.Text = $"Falhou: {Err(ex)}"; }
    }

    // ---------------- cache de shaders do CS2 ----------------

    private static long FolderSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length); }
        catch { return 0; }
    }

    private async void BtnCs2Shaders_Click(object sender, RoutedEventArgs e)
    {
        if (Infrastructure.ProcessUtil.IsRunning("cs2") || SteamInfo.IsRunning)
        {
            CompCs2Shader.Text = "Feche o CS2 e a Steam antes de limpar (eles mantêm o cache aberto).";
            return;
        }
        var folders = await Task.Run(WindowsTweaks.Cs2ShaderFolders);
        if (folders.Count == 0) { CompCs2Shader.Text = "Cache do CS2 não encontrado: nada a limpar."; return; }
        if (ThgDialog.Show("Apagar o cache de shaders do CS2? A Steam baixa ou recompila de novo na próxima vez que o jogo abrir " +
                           "(as primeiras partidas podem engasgar um pouco enquanto isso). Não há como desfazer, mas também não se perde nada.",
                           "CS2", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (sender is Button b) b.IsEnabled = false;
        long freed = await Task.Run(() =>
        {
            long total = 0;
            foreach (var d in folders)
                foreach (var f in new DirectoryInfo(d).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    try { long l = f.Length; f.Delete(); total += l; } catch { }
            return total;
        });
        if (sender is Button b2) b2.IsEnabled = true;
        CompCs2Shader.Text = $"{WindowsCleanup.Size(freed)} apagados. A Steam refaz o cache na próxima abertura do CS2.";
    }

    // ---------------- medidor de polling do mouse (Raw Input) ----------------

    private readonly List<long> _rawInputTicks = new();
    private bool _pollingTest;

    private async void BtnPolling_Click(object sender, RoutedEventArgs e)
    {
        if (_pollingTest) return;
        _pollingTest = true;
        BtnPolling.IsEnabled = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(hwnd);
        _rawInputTicks.Clear();
        src.AddHook(RawInputHook);
        var dev = new[] { new RawInputDevice { UsagePage = 1, Usage = 2, Flags = RidevInputSink, Target = hwnd } };
        bool ok = RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RawInputDevice>());
        try
        {
            if (!ok) { CompPolling.Text = "O Windows não deixou ler o mouse agora."; return; }
            for (int s = 5; s > 0; s--)
            {
                CompPolling.Text = $"Mexa o mouse em círculos rápidos, sem parar… {s}";
                await Task.Delay(1000);
            }
        }
        finally
        {
            dev[0].Flags = RidevRemove; dev[0].Target = IntPtr.Zero;
            RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RawInputDevice>());
            src.RemoveHook(RawInputHook);
            BtnPolling.IsEnabled = true;
            _pollingTest = false;
        }
        CompPolling.Text = PollingVerdict(_rawInputTicks);
    }

    private IntPtr RawInputHook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x00FF /* WM_INPUT */ && _pollingTest) _rawInputTicks.Add(Stopwatch.GetTimestamp());
        return IntPtr.Zero; // segue para o DefWindowProc, que libera a mensagem
    }

    /// <summary>Taxa em janelas de 100 ms com movimento; o percentil 90 descarta as pausas e as curvas lentas.</summary>
    private static string PollingVerdict(List<long> ticks)
    {
        if (ticks.Count < 50) return "Quase nenhum movimento lido. Clique em MEDIR e mexa o mouse sem parar durante os 5 segundos.";
        long bucket = Stopwatch.Frequency / 10;
        var rates = ticks.GroupBy(t => (t - ticks[0]) / bucket).Select(g => g.Count() * 10).Where(r => r >= 50).OrderBy(r => r).ToList();
        if (rates.Count == 0) return "Movimento insuficiente para medir. Tente de novo mexendo mais rápido.";
        int p90 = rates[Math.Min((int)(rates.Count * 0.9), rates.Count - 1)];
        int[] std = { 125, 250, 500, 1000, 2000, 4000, 8000 };
        int nearest = std.OrderBy(v => Math.Abs(v - p90)).First();
        string head = $"Seu mouse: cerca de {nearest} Hz (medido {p90} Hz).";
        return nearest >= 1000
            ? head + " Já está em 1000 Hz ou mais."
            : head + " Para 1000 Hz, mude no programa do fabricante (Logitech G HUB, Razer Synapse, SteelSeries GG…) ou no botão embaixo do mouse. " +
                     "Mouses de escritório costumam ficar em 125 Hz.";
    }

    private const uint RidevInputSink = 0x00000100, RidevRemove = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
}
