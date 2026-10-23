using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ThgOptimization;

/// <summary>
/// Navegação por áreas de tarefa. A barra lateral mostra só as áreas (Início, PC, Refrigeração e telas, Jogos,
/// Desempenho, Otimizar, Drivers e apps, Segurança, Avançado); as páginas de cada área viram abas no topo do conteúdo.
/// Cada aba aciona o destino de navegação original (escondido em LegacyNav), então toda a lógica de cada página,
/// carregamento sob demanda e atalhos existentes continua a mesma. A busca (Ctrl+K) acha qualquer função pelo nome
/// ou por palavras-chave.
/// </summary>
public partial class MainWindow
{
    private sealed record AreaTab(string Page, string Label, string Keywords = "");
    private sealed record Area(string Key, string Label, string Icon, AreaTab[] Tabs);

    private static readonly Area[] Areas =
    {
        new("home", "Início", "", new[] { new AreaTab("PageDash", "Visão geral", "inicio painel resumo status estado") }),
        new("pc", "Meu PC", "", new[]
        {
            new AreaTab("PageSystem", "Sistema", "especificações cpu placa-mãe memória windows bios"),
            new AreaTab("PageSensors", "Sensores", "temperatura clock tensão consumo rpm"),
            new AreaTab("PageGpu", "Placa de vídeo", "gpu vram nvidia amd radeon"),
            new AreaTab("PageDisks", "Discos", "ssd hd smart saúde armazenamento espaço"),
            new AreaTab("PageEnergy", "Energia", "bateria fonte notebook plano"),
            new AreaTab("PageNet", "Rede", "ping internet dns wi-fi servidores latência"),
        }),
        new("devices", "Fans, telas e RGB", "", new[]
        {
            new AreaTab("PageFans", "Fans", "ventoinha curva rotação histerese lian li"),
            new AreaTab("PageLcd", "Telas LCD", "turzx lian li imagem gif lcd cooler"),
            new AreaTab("PageInteg", "Iluminação RGB", "rgb openrgb corsair icue razer chroma signalrgb integração"),
        }),
        new("games", "Jogos", "", new[]
        {
            new AreaTab("PageGameMode", "Modo Jogo", "jogo automático prioridade perfis driver nvidia amd adrenalin hdr oled"),
            new AreaTab("PageLauncher", "Biblioteca", "jogos steam epic riot iniciar perfil"),
            new AreaTab("PageGames", "FPS estimado", "estimativa quantos fps"),
            new AreaTab("PageBottleneck", "Gargalo", "gargalo cpu gpu limitado"),
            new AreaTab("PageOverlay", "Overlay", "overlay fps na tela atalho"),
        }),
        new("perf", "Desempenho", "", new[]
        {
            new AreaTab("PageBench", "Benchmark", "teste pontuação ray tracing memória disco"),
            new AreaTab("PageStress", "Stress test", "estabilidade temperatura carga máxima"),
            new AreaTab("PageLatency", "Engasgos", "latência dpc latencymon stutter microengasgo"),
            new AreaTab("PageOc", "Overclock", "oc limite energia clock perfil undervolt"),
        }),
        new("optimize", "Otimizar", "", new[]
        {
            new AreaTab("PageTurbo", "TURBO", "diagnóstico nota corrigir tudo xmp bios"),
            new AreaTab("PageOptimize", "Ajustes e memória", "otimiza ajustes windows ram fechar programas msi mpo"),
            new AreaTab("PageClean", "Limpeza", "copilot recall widgets telemetria temporários bloatware apps"),
            new AreaTab("PageStartup", "Inicialização", "programas iniciar com o windows startup"),
        }),
        new("drivers", "Drivers e apps", "", new[]
        {
            new AreaTab("PageDriver", "Drivers", "atualizar driver vídeo chipset baixar"),
            new AreaTab("PageDdu", "DDU", "remover driver limpo modo de segurança"),
            new AreaTab("PageApps", "Instalar apps", "winget steam discord chrome formatar"),
        }),
        new("security", "Segurança", "", new[] { new AreaTab("PageShield", "SHIELD", "segurança antivírus firewall blindar secure boot tpm") }),
        new("advanced", "Avançado", "", new[]
        {
            new AreaTab("PageUsb", "Diagnóstico USB", "vid pid usb hid dispositivos relatório"),
            new AreaTab("PageMakerPorts", "Portas COM", "arduino esp32 com serial fantasma"),
            new AreaTab("PageMakerDrv", "Drivers seriais", "ch340 cp210x ftdi prolific arduino ide"),
        }),
    };

    private readonly Dictionary<string, RadioButton> _areaButtons = new();
    private readonly Dictionary<string, string> _areaLastPage = new();
    private string _currentArea = "";
    private TextBlock? _areaScoreTurbo, _areaScoreShield;

    private void InitAreas()
    {
        foreach (var a in Areas)
        {
            var icon = new TextBlock { Text = a.Icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14, Width = 28, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = a.Label, VerticalAlignment = VerticalAlignment.Center };
            var row = new Grid();
            row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } });
            if (a.Key is "optimize" or "security")
            {
                var score = new TextBlock { FontFamily = (FontFamily)FindResource("Mono"), FontSize = 11, FontWeight = FontWeights.Bold,
                                            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(score);
                if (a.Key == "optimize") _areaScoreTurbo = score; else _areaScoreShield = score;
            }
            var rb = new RadioButton { Style = (Style)FindResource("NavItem"), GroupName = "Areas", Content = row, Tag = a.Key };
            string key = a.Key;
            rb.Click += (_, _) => OpenArea(key);
            _areaButtons[a.Key] = rb;
            AreaNav.Children.Add(rb);
        }
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control) { NavSearch.Focus(); NavSearch.SelectAll(); e.Handled = true; }
        };
        SyncArea("PageDash");
    }

    private static Area AreaOf(string page) => Areas.FirstOrDefault(a => a.Tabs.Any(t => t.Page == page)) ?? Areas[0];

    private void OpenArea(string key)
    {
        var a = Areas.First(x => x.Key == key);
        string page = _areaLastPage.GetValueOrDefault(key) ?? a.Tabs[0].Page;
        NavigateTo(page);
    }

    /// <summary>Abre uma página pelo destino original (mesmo efeito de clicar no item antigo do menu).</summary>
    private void NavigateTo(string page)
    {
        var rb = LegacyNav.Children.OfType<FrameworkElement>()
            .SelectMany(e => e is Panel p ? p.Children.OfType<FrameworkElement>() : new[] { e })
            .OfType<RadioButton>().FirstOrDefault(r => r.Tag as string == page);
        if (rb == null) return;
        if (rb.IsChecked == true) SyncArea(page); else rb.IsChecked = true;
    }

    /// <summary>Marca a área da página aberta, monta as abas e o título "Área · Página".</summary>
    private void SyncArea(string page)
    {
        if (AreaTabs == null || _areaButtons.Count == 0) return;
        var area = AreaOf(page);
        _areaLastPage[area.Key] = page;
        if (_areaButtons.TryGetValue(area.Key, out var ab)) ab.IsChecked = true;
        if (_currentArea != area.Key)
        {
            _currentArea = area.Key;
            AreaTabs.Children.Clear();
            foreach (var t in area.Tabs)
            {
                var tab = new RadioButton { Style = (Style)FindResource("TopTab"), GroupName = "AreaTabs", Content = t.Label, Tag = t.Page };
                string target = t.Page;
                tab.Click += (_, _) => NavigateTo(target);
                AreaTabs.Children.Add(tab);
            }
        }
        foreach (RadioButton t in AreaTabs.Children) t.IsChecked = t.Tag as string == page;
        AreaTabsBar.Visibility = area.Tabs.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        var label = area.Tabs.FirstOrDefault(t => t.Page == page)?.Label ?? "";
        TitlePage.Text = area.Tabs.Length > 1 ? $"{area.Label} · {label}" : label;
    }

    /// <summary>Notas do TURBO e do SHIELD também nas áreas Otimizar e Segurança.</summary>
    private void UpdateAreaScores()
    {
        void Set(TextBlock? t, Border pill, TextBlock src)
        {
            if (t == null) return;
            bool has = pill.Visibility == Visibility.Visible && int.TryParse(src.Text, out _);
            t.Text = has ? src.Text : "";
            if (has) t.SetResourceReference(TextBlock.ForegroundProperty, int.Parse(src.Text) >= 90 ? "Good" : int.Parse(src.Text) >= 60 ? "Warn" : "Bad");
        }
        Set(_areaScoreTurbo, NavTurboScorePill, NavTurboScore);
        Set(_areaScoreShield, NavShieldScorePill, NavShieldScore);
    }

    // ---------------- busca de funções ----------------

    private void NavSearch_Changed(object sender, TextChangedEventArgs e)
    {
        string q = NavSearch.Text.Trim().ToLowerInvariant();
        NavSearchResults.Children.Clear();
        if (q.Length == 0) { NavSearchResults.Visibility = Visibility.Collapsed; AreaNav.Visibility = Visibility.Visible; return; }
        var hits = Areas.SelectMany(a => a.Tabs.Select(t => (Area: a, Tab: t)))
            .Select(x => (x.Area, x.Tab, Score: ScoreOf(q, x.Tab.Label, x.Tab.Keywords + " " + x.Area.Label)))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(8).ToList();
        foreach (var (area, tab, _) in hits)
        {
            var b = new Button { Style = (Style)FindResource("NavResult"), Tag = tab.Page,
                                 Content = new StackPanel { Children = {
                                     new TextBlock { Text = tab.Label },
                                     new TextBlock { Text = area.Label, FontSize = 11, Opacity = 0.6 } } } };
            string page = tab.Page;
            b.Click += (_, _) => { NavSearch.Text = ""; NavigateTo(page); };
            NavSearchResults.Children.Add(b);
        }
        if (hits.Count == 0) NavSearchResults.Children.Add(new TextBlock { Text = "Nada encontrado.", Margin = new Thickness(16, 4, 0, 4), Opacity = 0.6 });
        NavSearchResults.Visibility = Visibility.Visible;
        AreaNav.Visibility = Visibility.Collapsed;
    }

    private static int ScoreOf(string q, string label, string keywords)
    {
        string l = label.ToLowerInvariant(), k = keywords.ToLowerInvariant();
        if (l.StartsWith(q)) return 3;
        if (l.Contains(q)) return 2;
        return q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => k.Contains(w)) ? 1 : 0;
    }

    private void NavSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { NavSearch.Text = ""; e.Handled = true; }
        else if (e.Key == Key.Enter && NavSearchResults.Children.OfType<Button>().FirstOrDefault() is { Tag: string page })
        {
            NavSearch.Text = "";
            NavigateTo(page);
            e.Handled = true;
        }
    }
}
