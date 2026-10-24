using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;

namespace ThgOptimization;

/// <summary>
/// Bandeja do sistema: fechar a janela deixa o tHg rodando em segundo plano (Modo Jogo, overlay, atalho e avisos
/// continuam); clique no ícone abre; botão direito mostra o menu com Overlay, Modo Jogo, opções e Sair.
/// Também: atalho do overlay configurável e "Iniciar com o Windows" (tarefa agendada, porque o app roda como administrador).
/// </summary>
public partial class MainWindow
{
    private TrayIcon? _tray;
    private bool _exiting;
    private const string StartupTask = "tHg Optimization";

    private void InitTray()
    {
        try
        {
            _tray = new TrayIcon("tHg Optimization");
            _tray.Click += ShowFromTray;
            _tray.RightClick += ShowTrayMenu;
        }
        catch { _tray = null; } // sem bandeja, o X fecha normalmente
        _ = Task.Run(() => { try { RepointStartupTask(); } catch { } }).ContinueWith(_ => RefreshStartsWithWindowsAsync(), TaskScheduler.FromCurrentSynchronizationContext());

        Closing += OnWindowClosing;
        // Desligar ou sair da conta do Windows: fecha de verdade (não segura o desligamento).
        Application.Current.SessionEnding += (_, _) => _exiting = true;
        Closed += (_, _) => _tray?.Dispose();
    }

    /// <summary>X ou Alt+F4: vai para a bandeja (se ligado). Sair de verdade só pelo menu da bandeja ou com a opção desligada.</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || _tray == null || !_settings.CloseToTray) return;
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        if (!_settings.TrayHintShown)
        {
            _settings.TrayHintShown = true; _settings.Save();
            Toast("O tHg continua rodando", "Ele está na bandeja, ao lado do relógio. Clique no ícone para abrir ou use o botão direito para sair.", "");
        }
    }

    /// <summary>Abre a janela (clique na bandeja, segunda abertura do app ou atalho).</summary>
    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            if (Left < -10000) { WindowStartupLocation = WindowStartupLocation.Manual; CenterOnScreen(); }
            Show();
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false; // traz para frente mesmo se outro app estiver em foco
        Focus();
    }

    private void CenterOnScreen()
    {
        var a = SystemParameters.WorkArea;
        Left = a.Left + Math.Max(0, (a.Width - Width) / 2);
        Top = a.Top + Math.Max(0, (a.Height - Height) / 2);
    }

    /// <summary>Abre sem mostrar a janela (iniciar com o Windows): carrega tudo e fica só na bandeja.</summary>
    public void StartInTray()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; Top = -32000;
        ShowActivated = false;
        ShowInTaskbar = false;
        Show();
        Hide();
        ShowInTaskbar = true;
        ShowActivated = true;
    }

    public void ExitApp()
    {
        _exiting = true;
        try { FanCurves.ReleaseAll(_fanViews.Where(v => v.Config.Preset != "bios")); } catch { } // fans de volta ao padrão
        try { _tray?.Dispose(); _tray = null; } catch { } // o ícone some na hora, mesmo se algo segurar o fechamento
        // Garantia: se alguma tarefa em segundo plano segurar o processo, encerra em 5 s.
        new Thread(() => { Thread.Sleep(5000); Environment.Exit(0); }) { IsBackground = true }.Start();
        try { Close(); } catch { }
        Application.Current.Shutdown();
    }

    // ---------------- menu da bandeja ----------------

    private void ShowTrayMenu()
    {
        if (_tray == null) return;
        var actions = new Dictionary<int, Action>();
        var items = new List<TrayIcon.MenuEntry>();
        int next = 1;
        void Item(string text, Action click, bool bold = false, string gesture = "", bool check = false)
        {
            int id = next++;
            actions[id] = click;
            items.Add(new TrayIcon.MenuEntry(id, text, check, bold, gesture));
        }
        void Sep() => items.Add(new TrayIcon.MenuEntry(0, ""));

        Item("Abrir tHg Optimization", ShowFromTray, bold: true);
        Sep();
        Item("Overlay de FPS", () => ChkOverlay.IsChecked = ChkOverlay.IsChecked != true, gesture: HotkeyText(), check: ChkOverlay.IsChecked == true);
        Item("Modo Jogo automático", ToggleGameModeFromTray, check: _settings.AutoGameMode);
        Item("Gravar 60 s de frametime", () => _ = CaptureAsync());
        Sep();
        Item("Fechar o X para a bandeja", () => { _settings.CloseToTray = !_settings.CloseToTray; _settings.Save(); }, check: _settings.CloseToTray);
        Item("Iniciar com o Windows", () => _ = ToggleStartWithWindowsAsync(), check: _startsWithWindows);
        Item(_update != null ? $"Atualizar para a versão {_update.Version}" : "Procurar atualização",
             () => { ShowFromTray(); _ = CheckUpdateAsync(manual: true); }, bold: false);
        Sep();
        Item("Sair do tHg", ExitApp);

        int cmd = _tray.ShowMenu(items);
        // Executa depois de o menu fechar, fora da mensagem do ícone.
        if (actions.TryGetValue(cmd, out var act)) Dispatcher.BeginInvoke(act);
    }

    private void ToggleGameModeFromTray()
    {
        _settings.AutoGameMode = !_settings.AutoGameMode;
        _settings.Save();
        StartGameModeEngine();
        Toast("Modo Jogo", _settings.AutoGameMode ? "Ligado: o PC se prepara sozinho quando um jogo abrir." : "Desligado.", "");
    }

    // ---------------- iniciar com o Windows ----------------

    /// <summary>Tarefa agendada ao entrar no Windows, com privilégio máximo (a chave Run do registro não abre apps que pedem administrador).</summary>
    private static bool StartsWithWindows() => SecurityAudit.Run("schtasks.exe", $"/Query /TN \"{StartupTask}\"", 10_000).Code == 0;

    /// <summary>Último estado lido da tarefa agendada (o menu da bandeja abre na hora, sem esperar o schtasks).</summary>
    private bool _startsWithWindows;

    private async Task<bool> RefreshStartsWithWindowsAsync()
    {
        try { _startsWithWindows = await Task.Run(StartsWithWindows); } catch { }
        return _startsWithWindows;
    }

    private static string StartupTaskCreateArgs(string exe) =>
        $"/Create /TN \"{StartupTask}\" /TR \"\\\"{exe}\\\" --tray\" /SC ONLOGON /RL HIGHEST /DELAY 0000:20 /F";

    /// <summary>
    /// A tarefa de inicialização guarda o caminho do .exe que a criou. Se foi criada por uma cópia antiga (ex.: um .exe
    /// portátil em Downloads), o Windows abre essa cópia a cada boot e a versão instalada/atualizada nunca roda.
    /// Reaponta para este .exe quando o da tarefa sumiu ou é de versão menor; uma cópia mais velha não toma o lugar.
    /// </summary>
    private static void RepointStartupTask()
    {
        var (code, xml) = SecurityAudit.Run("schtasks.exe", $"/Query /TN \"{StartupTask}\" /XML", 10_000);
        if (code != 0 || Environment.ProcessPath is not string self) return;
        var m = System.Text.RegularExpressions.Regex.Match(xml, @"<Command>\s*""?([^""<]+?)""?\s*</Command>");
        if (!m.Success) return;
        string target = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        if (string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase)) return;
        if (System.IO.File.Exists(target))
        {
            var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(target);
            var theirs = new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
            if (theirs >= AppUpdater.Current) return;
        }
        SecurityAudit.Run("schtasks.exe", StartupTaskCreateArgs(self), 15_000);
    }

    /// <summary>Liga/desliga a tarefa agendada fora da thread da janela. Devolve o estado final.</summary>
    private async Task<bool> ToggleStartWithWindowsAsync()
    {
        if (await RefreshStartsWithWindowsAsync())
        {
            await Task.Run(() => SecurityAudit.Run("schtasks.exe", $"/Delete /TN \"{StartupTask}\" /F", 10_000));
            Toast("Iniciar com o Windows", "Desligado.", "");
            return await RefreshStartsWithWindowsAsync();
        }
        var (code, output) = await Task.Run(() => SecurityAudit.Run("schtasks.exe", StartupTaskCreateArgs(Environment.ProcessPath!), 15_000));
        Toast("Iniciar com o Windows", code == 0 ? "Ligado: o tHg abre na bandeja quando você entrar no Windows."
                                                 : $"O Windows não deixou criar a tarefa de inicialização. Feche e abra o tHg de novo e tente outra vez. ({output.Trim()})", "");
        return await RefreshStartsWithWindowsAsync();
    }

    // ---------------- atalho do overlay ----------------

    // 0 = padrão (Ctrl+Shift); 0x8000 = nenhum modificador (tecla F sozinha).
    private uint HotkeyMods => _settings.OverlayHotkeyMods switch { 0 => 0x0002u | 0x0004u, 0x8000 => 0u, var m => m };
    private uint HotkeyVk => _settings.OverlayHotkeyVk == 0 ? 0x4Fu : _settings.OverlayHotkeyVk;

    private string HotkeyText() => HotkeyText(HotkeyMods, HotkeyVk);

    private static string HotkeyText(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & 0x0002) != 0) parts.Add("Ctrl");
        if ((mods & 0x0001) != 0) parts.Add("Alt");
        if ((mods & 0x0004) != 0) parts.Add("Shift");
        if ((mods & 0x0008) != 0) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        string name = key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(key - Key.NumPad0),
            Key.OemTilde => "'", Key.Insert => "Insert", Key.Home => "Home", Key.PageUp => "PgUp", Key.PageDown => "PgDn",
            _ => key.ToString(),
        };
        parts.Add(name);
        return string.Join(" + ", parts);
    }

    /// <summary>Registra o atalho salvo. Devolve false se outro programa já usa esse atalho.</summary>
    private bool RegisterOverlayHotkey()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        UnregisterHotKey(hwnd, HotkeyId);
        bool ok = RegisterHotKey(hwnd, HotkeyId, HotkeyMods | 0x4000 /* MOD_NOREPEAT */, HotkeyVk);
        if (HotkeyLabel != null) HotkeyLabel.Content = HotkeyText();
        return ok;
    }

    private bool _capturingHotkey;

    private void HotkeyLabel_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyLabel.Content = "Pressione o atalho…  (Esc cancela)";
        HotkeyHint.Text = "Use Ctrl, Alt ou Shift com uma letra ou número, ou uma tecla F1–F12 sozinha.";
        PreviewKeyDown += CaptureHotkey;
    }

    private void CaptureHotkey(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { EndCapture(""); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        uint mods = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= 0x0002;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= 0x0001;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= 0x0004;
        bool fKey = key is >= Key.F1 and <= Key.F24;
        if (mods == 0 && !fKey) { HotkeyHint.Text = "Junte Ctrl, Alt ou Shift com a tecla (ou use F1–F12)."; return; }

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        uint oldMods = _settings.OverlayHotkeyMods, oldVk = _settings.OverlayHotkeyVk;
        _settings.OverlayHotkeyMods = mods == 0 ? 0x8000 /* nenhum modificador */ : mods;
        _settings.OverlayHotkeyVk = vk;
        if (!RegisterOverlayHotkey())
        {
            _settings.OverlayHotkeyMods = oldMods; _settings.OverlayHotkeyVk = oldVk;
            RegisterOverlayHotkey();
            EndCapture($"{HotkeyText(mods == 0 ? 0 : mods, vk)} já é usado por outro programa. Escolha outro.");
            return;
        }
        _settings.Save();
        EndCapture("Atalho salvo. Funciona dentro do jogo e com o tHg na bandeja.");
    }

    private void EndCapture(string hint)
    {
        _capturingHotkey = false;
        PreviewKeyDown -= CaptureHotkey;
        HotkeyLabel.Content = HotkeyText();
        HotkeyHint.Text = hint.Length > 0 ? hint : "Liga e desliga o overlay, inclusive dentro do jogo e com o tHg na bandeja.";
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
}
