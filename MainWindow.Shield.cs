using System.Windows;
using System.Windows.Controls;

namespace ThgOptimization;

/// <summary>SHIELD: diagnóstico de segurança do Windows com nota, correção individual e "Blindar" (corrige tudo).</summary>
public partial class MainWindow
{
    private List<GamerCheck> _secChecks = new();
    private string _shieldFilter = "problems";
    private bool _shieldBusy;

    private void InitShield()
    {
        if (_secChecks.Count == 0 && !_shieldBusy) _ = RunShieldAuditAsync();
    }

    private async Task RunShieldAuditAsync()
    {
        if (_shieldBusy) return;
        _shieldBusy = true;
        BtnShield.IsEnabled = BtnShieldScan.IsEnabled = false;
        BtnShieldScan.Content = "VERIFICANDO…";
        ShieldVerdict.Text = "Verificando a segurança…";
        try
        {
            _secChecks = await Task.Run(SecurityAudit.Run);
            UpdateAntiCheatCard(await Task.Run(SecurityAudit.AntiCheat));
        }
        catch (Exception ex)
        {
            ShieldVerdict.Text = "Não foi possível verificar";
            ShieldSummary.Text = Err(ex);
            return;
        }
        finally
        {
            _shieldBusy = false;
            BtnShieldScan.IsEnabled = true;
            BtnShieldScan.Content = "VERIFICAR";
        }
        UpdateShieldView();
    }

    private void UpdateShieldView()
    {
        var score = GamerAudit.Score(_secChecks, security: true);
        ShieldScore.Text = score.Total.ToString();
        ShieldScore.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, score.Total >= 90 ? "Good" : score.Total >= 60 ? "Warn" : "Bad");
        SetNavScore(NavShieldScorePill, NavShieldScore, score.Total);
        UpdateAreaScores();
        if (PageDash.IsVisible) UpdateDashHealth();
        ShieldVerdict.Text = score.Verdict;
        ShieldSummary.Text = score.Problems == 0
            ? "Nenhuma brecha encontrada. Verifique de novo de vez em quando."
            : $"{score.Problems} {(score.Problems == 1 ? "ponto aberto" : "pontos abertos")} · {score.AutoFixable} o tHg fecha sozinho";
        ShieldAreas.ItemsSource = new[] { "PROTEÇÃO", "SISTEMA", "REDE", "CONTAS E JOGOS" }
            .Where(score.ByArea.ContainsKey)
            .Select(a => new MeterItem(a, $"{score.ByArea[a]}", score.ByArea[a] / 100.0))
            .ToList();
        BtnShield.IsEnabled = score.AutoFixable > 0 && !_shieldBusy;
        BtnShield.Content = score.AutoFixable > 0 ? $"BLINDAR: CORRIGIR {score.AutoFixable}" : "BLINDADO";

        var shown = _shieldFilter switch
        {
            "problems" => _secChecks.Where(c => c.IsProblem).ToList(),
            "all" => _secChecks,
            var area => _secChecks.Where(c => c.Area == area).ToList(),
        };
        ShieldList.ItemsSource = shown;
        ShieldEmpty.Text = _shieldFilter == "problems" && _secChecks.Count > 0 ? "Nenhuma brecha encontrada." : "Nenhum item aqui.";
        ShieldEmpty.Visibility = shown.Count == 0 && !_shieldBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Resumo de compatibilidade com anti-cheats de kernel: o que falta para eles abrirem e o estado do VBS/HVCI.</summary>
    private void UpdateAntiCheatCard(SecurityAudit.AntiCheatStatus ac)
    {
        ShieldAcRows.Children.Clear();
        void Row(bool ok, bool required, string title, string value)
        {
            var mark = new TextBlock { Text = ok ? "✓" : required ? "✕" : "•", Width = 22, FontWeight = FontWeights.Bold };
            mark.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Good" : required ? "Bad" : "Warn");
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
            label.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold });
            label.Inlines.Add(new System.Windows.Documents.Run("  " + value) { Foreground = (System.Windows.Media.Brush)FindResource("Muted") });
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            line.Children.Add(mark);
            line.Children.Add(label);
            ShieldAcRows.Children.Add(line);
        }
        Row(ac.SecureBoot, true, "Secure Boot", ac.SecureBoot ? "ligado" : ac.Uefi ? "desligado na BIOS" : "Windows em modo legado (BIOS/CSM)");
        Row(ac.Tpm20, true, "TPM 2.0", !ac.TpmPresent ? "não encontrado (fTPM/PTT desligado na BIOS?)" : $"TPM {ac.TpmSpec}{(ac.TpmEnabled ? "" : " desativado")}");
        Row(ac.VbsStatus == 2, false, "VBS (segurança baseada em virtualização)", ac.VbsStatus switch { 2 => "rodando", 1 => "ligado, mas não está rodando", _ => "desligado" });
        Row(ac.Hvci, false, "Integridade de Memória (HVCI)", ac.Hvci ? "ligada: mais segurança, cerca de 5 a 8% menos FPS em jogos limitados pela CPU" : "desligada: máximo de FPS, menos proteção contra drivers maliciosos");
        ShieldAcVerdict.Text = ac.KernelAcReady
            ? "Pronto para anti-cheats de kernel (Vanguard, Javelin e afins)"
            : "Falta " + string.Join(" e ", new[] { ac.SecureBoot ? null : "Secure Boot", ac.Tpm20 ? null : "TPM 2.0" }.OfType<string>()) + ": jogos como Valorant (Windows 11) e Battlefield 6 não abrem";
        ShieldAcVerdict.SetResourceReference(TextBlock.ForegroundProperty, ac.KernelAcReady ? "Good" : "Bad");
    }

    private void ShieldFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || ShieldList == null) return; // disparado no InitializeComponent
        _shieldFilter = tag;
        UpdateShieldView();
    }

    private async void BtnShieldScan_Click(object sender, RoutedEventArgs e)
    {
        ShieldResult.Visibility = Visibility.Collapsed;
        await RunShieldAuditAsync();
    }

    private async void BtnShield_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        var todo = _secChecks.Where(c => c.InTurbo).ToList();
        if (todo.Count == 0) return;
        var manual = _secChecks.Where(c => c.IsProblem && c.Fix is FixKind.Bios or FixKind.Settings or FixKind.Page).ToList();
        string msg = "O SHIELD vai ligar:\n\n" + string.Join("\n", todo.Select(c => $"  • {c.Title}")) +
                     (manual.Count > 0 ? $"\n\nOutros {manual.Count} itens dependem de você (BIOS, Windows Update, contas): veja o guia de cada um." : "") +
                     "\n\nNada é desligado e tudo pode ser desfeito em \"Desfazer\". Continuar?";
        if (!ThgDialog.Confirm("SHIELD", msg, "Blindar", "Agora não")) return;

        BtnShield.IsEnabled = BtnShieldScan.IsEnabled = false;
        BtnShield.Content = "BLINDANDO…";
        ShieldResult.Visibility = Visibility.Visible;
        int ok = 0;
        foreach (var c in todo)
        {
            ShieldResult.Text = $"Aplicando: {c.Title}…";
            if (await ApplyCheckAsync(c)) ok++;
        }
        var failed = todo.Where(c => c.Level != CheckLevel.Ok).ToList();
        bool restart = todo.Any(c => c.NeedsRestart && c.Level == CheckLevel.Ok);
        await RunShieldAuditAsync();
        ShieldResult.Text = $"{ok} de {todo.Count} proteções ligadas." +
                            (failed.Count > 0 ? " Não aplicados: " + string.Join("; ", failed.Select(c => $"{c.Title} ({c.Status})")) + "." : "") +
                            (restart ? " Reinicie o PC para todas valerem." : "");
        ShieldResult.Visibility = Visibility.Visible;
    }

    private async void BtnShieldUndo_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.TweakBackup.Keys.Any(k => k.StartsWith("sec.")))
        {
            ShieldResult.Text = "Nada para desfazer: o SHIELD ainda não alterou nenhuma configuração."; ShieldResult.Visibility = Visibility.Visible;
            return;
        }
        if (!ThgDialog.Confirm("Desfazer proteções", "As configurações de segurança que o SHIELD alterou voltam para como estavam antes.\n\nIsso pode reabrir brechas (ex.: firewall desligado). Use só se algum programa parou de funcionar.", "Desfazer", "Manter proteções", MessageBoxImage.Warning)) return;
        await Task.Run(() => SecurityAudit.RestoreAll(_settings));
        await RunShieldAuditAsync();
        ShieldResult.Text = "Configurações de segurança restauradas. Alguns itens só voltam depois de reiniciar.";
        ShieldResult.Visibility = Visibility.Visible;
    }
}
