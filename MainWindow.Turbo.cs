using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace ThgOptimization;

/// <summary>
/// TURBO: diagnóstico de PC gamer (hardware, BIOS, Windows, rede) com nota, correção individual e
/// "corrigir tudo" com ponto de restauração e desfazer.
/// </summary>
public partial class MainWindow
{
    private List<GamerCheck> _checks = new();
    private string _turboFilter = "problems";
    private bool _turboBusy;

    private void InitTurbo()
    {
        if (_checks.Count == 0 && !_turboBusy) _ = RunTurboAuditAsync();
    }

    private async Task RunTurboAuditAsync()
    {
        if (_turboBusy) return;
        _turboBusy = true;
        BtnTurbo.IsEnabled = BtnTurboScan.IsEnabled = false;
        BtnTurboScan.Content = "ANALISANDO…";
        TurboVerdict.Text = "Analisando o PC…";
        try
        {
            bool? nv = _gpus.Any(g => g.Vendor == "NVIDIA") ? _settings.TweakBackup.Keys.Any(k => k.StartsWith("nv.") && !k.StartsWith("nv.profile.")) : null;
            var ctx = new GamerAudit.Context { Gpus = _gpus, Tweaks = _tweaks, CpuName = _cpuName, NvProfilesApplied = nv, AutoGameMode = _settings.AutoGameMode, LastLatency = _settings.LastLatency };
            _checks = await Task.Run(() => GamerAudit.Run(ctx));
        }
        catch (Exception ex)
        {
            TurboVerdict.Text = "Não foi possível analisar";
            TurboSummary.Text = Err(ex);
            return;
        }
        finally
        {
            _turboBusy = false;
            BtnTurboScan.IsEnabled = true;
            BtnTurboScan.Content = "ANALISAR";
        }
        UpdateTurboView();
    }

    private void UpdateTurboView()
    {
        var score = GamerAudit.Score(_checks);
        TurboScore.Text = score.Total.ToString();
        TurboScore.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, score.Total >= 90 ? "Good" : score.Total >= 60 ? "Warn" : "Bad");
        SetNavScore(NavTurboScorePill, NavTurboScore, score.Total);
        UpdateAreaScores();
        if (PageDash.IsVisible) UpdateDashHealth();
        TurboVerdict.Text = score.Verdict;
        TurboSummary.Text = score.Problems == 0
            ? "Nada pendente. Rode de novo depois de trocar peças ou atualizar a BIOS."
            : $"{score.Problems} {(score.Problems == 1 ? "item" : "itens")} para melhorar · {score.AutoFixable} o TURBO corrige sozinho" +
              (_gpus.Count == 0 ? " · placa de vídeo ainda sendo detectada" : "");
        TurboAreas.ItemsSource = new[] { "HARDWARE", "BIOS", "WINDOWS", "REDE" }
            .Where(score.ByArea.ContainsKey)
            .Select(a => new MeterItem(a, $"{score.ByArea[a]}", score.ByArea[a] / 100.0))
            .ToList();
        BtnTurbo.IsEnabled = score.AutoFixable > 0 && !_turboBusy;
        BtnTurbo.Content = score.AutoFixable > 0 ? $"TURBO: CORRIGIR {score.AutoFixable}" : "TUDO CORRIGIDO";
        UpdateTurboList();
    }

    private void UpdateTurboList()
    {
        var shown = _turboFilter switch
        {
            "problems" => _checks.Where(c => c.IsProblem).ToList(),
            "all" => _checks,
            var area => _checks.Where(c => c.Area == area).ToList(),
        };
        TurboList.ItemsSource = shown;
        TurboEmpty.Text = _turboFilter == "problems" && _checks.Count > 0 ? "Nenhum problema encontrado. PC pronto para jogar." : "Nenhum item aqui.";
        TurboEmpty.Visibility = shown.Count == 0 && !_turboBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TurboFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || TurboList == null) return; // disparado no InitializeComponent
        _turboFilter = tag;
        UpdateTurboList();
    }

    private async void BtnTurboScan_Click(object sender, RoutedEventArgs e)
    {
        TurboResult.Visibility = Visibility.Collapsed;
        await RunTurboAuditAsync();
    }

    // ---------------- correção de um item ----------------

    private async void CheckFix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GamerCheck c } btn) return;
        switch (c.Fix)
        {
            case FixKind.Auto:
                if (c.TurboSkip.Length > 0 && c.Id != "sec.downloads" &&
                    !ThgDialog.Confirm(c.Title, c.TurboSkip, "Aplicar mesmo assim", "Cancelar")) return;
                btn.IsEnabled = false;
                c.Status = "Aplicando…";
                await ApplyCheckAsync(c);
                btn.IsEnabled = true;
                await RefreshTweakStatesAsync();
                if (c.Id.StartsWith("sec.")) UpdateShieldView(); else UpdateTurboView();
                break;

            case FixKind.Settings:
                try { Process.Start(new ProcessStartInfo(c.FixTarget!) { UseShellExecute = true }); }
                catch (Exception ex) { c.Status = $"Não foi possível abrir: {Err(ex)}"; }
                break;

            case FixKind.Page:
                GoToPage(c.FixTarget);
                break;

            case FixKind.Bios:
                ShowBiosGuide(c);
                break;
        }
    }

    /// <summary>Aplica a correção automática fora da thread da janela e atualiza o item (sem refazer o diagnóstico inteiro).</summary>
    private async Task<bool> ApplyCheckAsync(GamerCheck c)
    {
        string err = await Task.Run(() => { try { return c.AutoFix!(_settings); } catch (Exception ex) { return Err(ex); } });
        _settings.Save();
        if (err.Length == 0)
        {
            c.Level = CheckLevel.Ok;
            c.Found = "Corrigido agora";
            c.Status = "Corrigido." + (c.NeedsRestart ? " Vale depois de reiniciar o PC." : "");
            if (!c.Id.StartsWith("sec.")) _settings.TurboAppliedAt ??= DateTime.Now;
            return true;
        }
        c.Status = $"Não aplicado: {err}";
        return false;
    }

    private Task RefreshTweakStatesAsync() => RefreshTweaksAsync("turbo", _tweaks, errorPrefix: null);

    private void GoToPage(string? tag)
    {
        RadioButton? nav = tag switch
        {
            "PageDriver" => NavDriver, "PageStartup" => NavStartup, "PageNet" => NavNet,
            "PageOptimize" => NavOptimize, "PageStress" => NavStress, "PageGameMode" => NavGameMode, "PageShield" => NavShield,
            "PageTurbo" => NavTurbo, "PageLatency" => NavLatency, "PageClean" => NavClean, "PageDdu" => NavDdu, _ => null,
        };
        if (nav != null) nav.IsChecked = true;
    }

    private void ShowBiosGuide(GamerCheck c)
    {
        string text = c.FixTarget ?? "";
        if (c.Area != "BIOS")
        {
            ThgDialog.Info(c.Title, text);
            return;
        }
        if (!ThgDialog.Confirm(c.Title, text + "\n\nSalve o que estiver aberto antes de reiniciar.", "Reiniciar na BIOS", "Fechar",
                               MessageBoxImage.Information)) return;
        string err = GamerAudit.RebootToFirmware();
        if (err.Length > 0) ThgDialog.Info("Reiniciar na BIOS", err, MessageBoxImage.Warning);
    }

    // ---------------- TURBO: corrigir tudo ----------------

    private async void BtnTurbo_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        // Ordem dos ajustes do Windows: a mesma da lista (o plano de energia vem antes das opções do plano). Monitores por último.
        int Order(GamerCheck c) => c.Id.StartsWith("tweak.") ? _tweaks.FindIndex(t => "tweak." + t.Id == c.Id) : 1000;
        var todo = _checks.Where(c => c.InTurbo).OrderBy(Order).ToList();
        if (todo.Count == 0) return;

        var skipped = _checks.Where(c => c.Fix == FixKind.Auto && c.IsProblem && c.TurboSkip.Length > 0).ToList();
        var manual = _checks.Where(c => c.IsProblem && c.Fix is FixKind.Bios or FixKind.Settings or FixKind.Page).ToList();
        string msg = "O TURBO vai aplicar:\n\n" + string.Join("\n", todo.Select(c => $"  • {c.Title}")) +
                     (skipped.Count > 0 ? "\n\nFicam de fora (gastam mais bateria no notebook):\n" + string.Join("\n", skipped.Select(c => $"  • {c.Title}")) : "") +
                     (manual.Count > 0 ? $"\n\nOutros {manual.Count} itens precisam de você (BIOS, cabos, peças): veja o guia de cada um na lista." : "") +
                     "\n\nTudo pode ser desfeito em \"Desfazer tudo\". Continuar?";
        if (!ThgDialog.Confirm("TURBO", msg, "Aplicar correções", "Agora não")) return;

        BtnTurbo.IsEnabled = BtnTurboScan.IsEnabled = false;
        TurboResult.Visibility = Visibility.Visible;
        var notes = new List<string>();

        if (TurboRestorePoint.IsChecked == true)
        {
            BtnTurbo.Content = "CRIANDO PONTO…";
            TurboResult.Text = "Criando ponto de restauração do Windows (pode levar um minuto)…";
            string rp = await GamerAudit.CreateRestorePointAsync();
            notes.Add(rp.Length == 0 ? "ponto de restauração criado" : $"sem ponto de restauração ({rp})");
        }

        BtnTurbo.Content = "APLICANDO…";
        int ok = 0;
        foreach (var c in todo)
        {
            TurboResult.Text = $"Aplicando: {c.Title}…";
            // Registro e powercfg fora da thread da janela; cada item espera o anterior (a ordem importa).
            var item = c;
            string err = await Task.Run(() => { try { return item.AutoFix!(_settings); } catch (Exception ex) { return Err(ex); } });
            if (err.Length == 0) { ok++; c.Status = "Corrigido pelo TURBO."; }
            else c.Status = $"Não aplicado: {err}";
        }
        if (ok > 0) _settings.TurboAppliedAt ??= DateTime.Now;
        _settings.Save();
        await RefreshTweakStatesAsync();

        bool restart = todo.Any(c => c.NeedsRestart && c.Status.StartsWith("Corrigido"));
        notes.Insert(0, $"{ok} de {todo.Count} correções aplicadas");
        var failed = todo.Where(c => !c.Status.StartsWith("Corrigido")).ToList();
        if (failed.Count > 0) notes.Add("não aplicados: " + string.Join("; ", failed.Select(c => $"{c.Title} ({c.Status[14..]})")));
        if (restart) notes.Add("reinicie o PC para todas valerem");
        await RunTurboAuditAsync(); // mostra o estado real depois das mudanças
        TurboResult.Text = string.Join(" · ", notes) + ".";
        TurboResult.Visibility = Visibility.Visible;
    }

    private async void BtnPurgeStandby_Click(object sender, RoutedEventArgs e)
    {
        BtnPurgeStandby.IsEnabled = false;
        var (ok, freed, error) = await Task.Run(Optimizer.PurgeMemoryLists);
        BtnPurgeStandby.IsEnabled = true;
        TurboResult.Text = ok ? $"Memória em espera limpa: {freed / 1048576.0:N0} MB livres a mais."
                              : $"A memória em espera não foi limpa: {error}.";
        TurboResult.Visibility = Visibility.Visible;
        UpdateRamStatus();
    }

    private async void BtnTurboUndo_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.TweakBackup.Keys.Any(k => !WindowsTweaks.IsOtherPageKey(k)))
        {
            TurboResult.Text = "Nada para desfazer: o tHg Optimization ainda não alterou nenhum ajuste."; TurboResult.Visibility = Visibility.Visible;
            return;
        }
        if (!ThgDialog.Confirm("Desfazer tudo", "Os ajustes do Windows e a taxa dos monitores voltam para como estavam antes do tHg Optimization.", "Desfazer tudo", "Manter")) return;
        bool restart = _tweaks.Any(t => t.NeedsRestart && _settings.TweakBackup.Keys.Any(k => k == t.Id || k.StartsWith(t.Id + ".")));
        await Task.Run(() => WindowsTweaks.RestoreAll(_tweaks, _settings));
        await RefreshTweakStatesAsync();
        await RunTurboAuditAsync();
        TurboResult.Text = "Tudo restaurado." + (restart ? " Alguns ajustes só voltam depois de reiniciar o PC." : "");
        TurboResult.Visibility = Visibility.Visible;
    }
}
