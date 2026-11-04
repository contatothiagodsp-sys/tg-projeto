using System.Windows;
using System.Windows.Controls;

namespace ThgOptimization;

/// <summary>Página Limpeza do Windows: ajustes reversíveis, arquivos temporários e apps pré-instalados.</summary>
public partial class MainWindow
{
    private readonly List<Tweak> _cleanTweaks = WindowsTweaks.CleanupTweaks();
    private List<CleanTarget> _cleanTargets = new();
    private List<BloatApp> _bloat = new();
    private bool _cleanInit, _cleanBusy;

    private async void InitClean()
    {
        Task? read = null;
        if (!_cleanInit)
        {
            _cleanInit = true;
            CleanTweaks.ItemsSource = _cleanTweaks;
            WatchAppliedCount(_cleanTweaks, CleanTweaksSummary);
            CleanTweaks.IsEnabled = false; // travado até o estado real chegar
            read = RefreshTweaksAsync("clean", _cleanTweaks);
        }
        BtnAppsRestore.Visibility = _settings.RemovedApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_cleanBusy) _ = Infrastructure.UiGuard.RunAsync("limpeza", RescanCleanAsync, ex => CleanLog.Text = "Não foi possível medir: " + Err(ex));
        if (read == null) return;
        await Infrastructure.UiGuard.RunAsync("limpeza", () => read, ex => CleanLog.Text = "Não foi possível ler os ajustes: " + Err(ex));
        CleanTweaks.IsEnabled = true;
    }

    private async Task RescanCleanAsync()
    {
        _cleanTargets = WindowsCleanup.Targets();
        CleanTargets.ItemsSource = _cleanTargets;
        CleanTotal.Text = "medindo…";
        await Task.Run(() => { foreach (var t in _cleanTargets) t.Bytes = WindowsCleanup.Measure(t); });
        UpdateCleanTotal();

        BloatEmpty.Text = "Procurando…";
        BloatEmpty.Visibility = Visibility.Visible;
        _bloat = await Task.Run(WindowsCleanup.InstalledBloat);
        BloatList.ItemsSource = _bloat;
        BloatEmpty.Visibility = _bloat.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BloatEmpty.Text = "Nenhum app desnecessário encontrado. 👍";
        BtnBloat.IsEnabled = _bloat.Count > 0;
    }

    private void UpdateCleanTotal()
    {
        long sel = _cleanTargets.Where(t => t.Selected && t.Bytes > 0).Sum(t => t.Bytes);
        CleanTotal.Text = $"{WindowsCleanup.Size(sel)} selecionados";
    }

    private void CleanTarget_Click(object sender, RoutedEventArgs e) => UpdateCleanTotal();

    private void CleanLogLine(string line) => Dispatcher.Invoke(() => CleanLog.Text = (CleanLog.Text.Length > 0 ? CleanLog.Text + "\n" : "") + line);

    /// <summary>Liga os ajustes com peso (o de apps de fundo da Store fica de fora, porque corta notificações).</summary>
    private async void BtnCleanAll_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin() || _cleanBusy) return;
        _cleanBusy = true;
        CleanLog.Text = "";
        await Infrastructure.UiGuard.RunButtonAsync(BtnCleanAll, "limpeza", async () =>
        {
            var todo = _cleanTweaks.Where(t => t.Weight > 0 && !t.IsOn).ToList();
            foreach (var t in todo)
            {
                try
                {
                    await Task.Run(() => t.Write(true, _settings));
                    t.IsOn = await Task.Run(() => t.Read() ?? false);
                    t.Status = t.IsOn ? "Aplicado." : "O Windows não aceitou a mudança.";
                }
                catch (Exception ex) { t.Status = $"Falhou: {Err(ex)}"; }
            }
            _settings.Save();
            CleanLogLine(todo.Count == 0 ? "Os ajustes recomendados já estavam aplicados." :
                $"{todo.Count(t => t.IsOn)} de {todo.Count} ajustes aplicados. Alguns (Widgets, Copilot) somem da barra depois de sair e entrar no Windows.");
        }, ex => CleanLogLine("Falhou: " + Err(ex)));
        _cleanBusy = false;
    }

    private async void BtnCleanRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.TweakBackup.Keys.Any(k => k.StartsWith("clean.")))
        {
            Toast("Limpeza do Windows", "Nenhum ajuste desta página foi alterado ainda.", "");
            return;
        }
        if (ThgDialog.Show("Voltar todos os ajustes desta página para como estavam antes do tHg?", "Limpeza do Windows",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Infrastructure.UiGuard.RunButtonAsync(sender as UIElement, "limpeza", async () =>
        {
            await Task.Run(() => WindowsTweaks.RestoreCleanup(_cleanTweaks, _settings));
            await RefreshTweaksAsync("clean", _cleanTweaks, errorPrefix: null);
            foreach (var t in _cleanTweaks) t.Status = "Restaurado.";
            CleanLog.Text = "Ajustes restaurados.";
        }, ex => CleanLog.Text = "Não foi possível restaurar: " + Err(ex));
    }

    private async void BtnCleanFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin() || _cleanBusy) return;
        var sel = _cleanTargets.Where(t => t.Selected).ToList();
        if (sel.Count == 0) return;
        if (sel.Any(t => t.Id == "recycle") &&
            ThgDialog.Show("Esvaziar a lixeira? Os arquivos dela não poderão ser recuperados.", "Limpeza do Windows",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _cleanBusy = true; BtnCleanFiles.Content = "LIMPANDO…";
        CleanLog.Text = "";
        await Infrastructure.UiGuard.RunButtonAsync(BtnCleanFiles, "limpeza", async () =>
        {
            long total = 0;
            foreach (var t in sel)
            {
                total += await Task.Run(() => WindowsCleanup.Clean(t, CleanLogLine));
                t.Bytes = await Task.Run(() => WindowsCleanup.Measure(t));
            }
            CleanLogLine($"Total liberado: {WindowsCleanup.Size(total)}.");
            UpdateCleanTotal();
        }, ex => CleanLogLine("A limpeza parou: " + Err(ex)));
        _cleanBusy = false; BtnCleanFiles.Content = "LIMPAR SELECIONADOS";
    }

    private async void BtnBloat_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin() || _cleanBusy) return;
        var sel = _bloat.Where(b => b.Selected).ToList();
        if (sel.Count == 0) return;
        if (ThgDialog.Show($"Remover {sel.Count} app(s) da sua conta?\n\n{string.Join("\n", sel.Select(b => "• " + b.Name))}\n\n" +
                            "Dá para trazer de volta com \"Reinstalar removidos\" ou pela Microsoft Store.",
                            "Limpeza do Windows", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _cleanBusy = true; BtnBloat.Content = "REMOVENDO…";
        CleanLog.Text = "";
        await Infrastructure.UiGuard.RunButtonAsync(BtnBloat, "limpeza", async () =>
        {
            var done = await Task.Run(() => WindowsCleanup.Remove(sel, CleanLogLine));
            _settings.Mutate(() => { foreach (var p in done) if (!_settings.RemovedApps.Contains(p)) _settings.RemovedApps.Add(p); });
            _settings.Save();
            CleanLogLine($"{done.Count} de {sel.Count} removidos.");
        }, ex => CleanLogLine("A remoção parou: " + Err(ex)));
        BtnBloat.Content = "REMOVER SELECIONADOS";
        _cleanBusy = false;
        InitClean();
    }

    private async void BtnBloatRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin() || _cleanBusy || _settings.RemovedApps.Count == 0) return;
        _cleanBusy = true;
        CleanLog.Text = "";
        await Infrastructure.UiGuard.RunButtonAsync(BtnAppsRestore, "limpeza", async () =>
        {
            var list = _settings.RemovedApps.ToList();
            var back = await Task.Run(() => WindowsCleanup.Reinstall(list, CleanLogLine));
            _settings.Mutate(() => _settings.RemovedApps.RemoveAll(back.Contains));
            _settings.Save();
        }, ex => CleanLogLine("A reinstalação parou: " + Err(ex)));
        _cleanBusy = false;
        InitClean();
    }
}
