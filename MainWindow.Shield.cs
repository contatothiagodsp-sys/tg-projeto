using System.Windows;

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
        try { _secChecks = await Task.Run(SecurityAudit.Run); }
        catch (Exception ex)
        {
            ShieldVerdict.Text = "Não foi possível verificar";
            ShieldSummary.Text = ex.Message;
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
