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
new("intel", "tHg Intelligence", "\uE945", new[]
        {
            new AreaTab("PageIntel", "Diagnóstico", "inteligência ia diagnosticar gargalo recomendação otimização automática confiança evidência rollback desfazer aprendizado"),
            new AreaTab("PageIntelGames", "Perfis de jogos", "perfil jogo baseline referência competitivo equilibrado silencioso sessões habitual"),
            new AreaTab("PageIntelPc", "Este PC", "inventário hardware capacidades sensores privacidade exportar importar apagar dados"),
        }),
                new("pc", "Meu PC", "", new[]
        {
            new AreaTab("PageSystem", "Sistema", "especificações cpu placa-mãe memória windows bios"),
            new AreaTab("PageSensors", "Sensores", "temperatura clock tensão consumo rpm"),
            new AreaTab("PageCpu", "Processador", "cpu núcleos threads cache microcódigo vid vcore clock core parking ryzen intel"),
            new AreaTab("PageGpu", "Placa de vídeo", "gpu vram nvidia amd radeon"),
            new AreaTab("PageDisks", "Discos", "ssd hd smart saúde armazenamento espaço"),
            new AreaTab("PageEnergy", "Energia", "bateria fonte notebook plano"),
            new AreaTab("PageNet", "Rede", "ping internet dns wi-fi servidores latência"),
        }),
        new("devices", "Fans, telas e RGB", "", new[]
        {
            new AreaTab("PageFans", "Fans", "ventoinha curva rotação histerese lian li"),
            new AreaTab("PageLcd", "Telas LCD", "turzx lian li imagem gif lcd cooler"),
            new AreaTab("PageInteg", "Iluminação RGB", "rgb openrgb corsair icue razer chroma signalrgb integração iluminação dinâmica lamparray logitech asrock polychrome"),
        }),
        new("games", "Jogos", "", new[]
        {
            new AreaTab("PageGameMode", "Modo Jogo", "jogo automático prioridade perfis driver nvidia amd adrenalin hdr oled"),
            new AreaTab("PageHistory", "Histórico", "sessões linha do tempo frametime engasgos travou gráfico csv presentmon capframex"),
            new AreaTab("PageComp", "Competitivo", "valorant cs2 lol league fivem gta latência input lag nagle irq afinidade polling mouse hpet timer mpo mmcss msi 1% low"),
            new AreaTab("PageLauncher", "Biblioteca", "jogos steam epic riot iniciar perfil"),
            new AreaTab("PageGames", "FPS estimado", "estimativa quantos fps"),
            new AreaTab("PageBottleneck", "Gargalo", "gargalo cpu gpu limitado"),
            new AreaTab("PageOverlay", "Overlay", "overlay fps na tela atalho"),
        }),
        new("perf", "Desempenho", "", new[]
        {
            new AreaTab("PageBench", "Benchmark", "teste pontuação ray tracing memória disco performance lab a/b comparar frametime"),
            new AreaTab("PageStress", "Stress test", "estabilidade temperatura carga máxima"),
            new AreaTab("PageLatency", "Engasgos", "latência dpc latencymon stutter microengasgo"),
            new AreaTab("PageOc", "Overclock", "oc limite energia clock perfil undervolt pbo curve optimizer garantia analise setup"),
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
        new("settings", "Configurações", "\uE713", new[] { new AreaTab("PageSettings", "Configurações", "tema escuro claro aparência bandeja iniciar com o windows atualização versão falhas pasta") }),
    };

    private readonly Dictionary<string, RadioButton> _areaButtons = new();
    private readonly Dictionary<string, string> _areaLastPage = new();
    private string _currentArea = "";
    /// <summary>Páginas escondidas porque o hardware delas não existe neste PC (ver ApplyHardwareVisibility).</summary>
    private readonly HashSet<string> _hiddenPages = new();
    private string _currentPage = "PageDash";
    private TextBlock? _areaScoreTurbo, _areaScoreShield;

    private void InitAreas()
    {
        foreach (var a in Areas)
        {
            var icon = new TextBlock { Text = a.Icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 16, Width = 28, VerticalAlignment = VerticalAlignment.Center };
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

    /// <summary>
    /// Esconde as abas (e a área inteira, se todas as abas sumirem) cujo hardware o último scan não achou: Telas LCD sem
    /// tela compatível, Iluminação RGB sem nenhum periférico/sistema de RGB suportado, e dentro dela o teclado AULA e a
    /// Corsair. Sem scan ainda, ou com "Mostrar todos os módulos", nada fica escondido.
    /// </summary>
    private void ApplyHardwareVisibility()
    {
        var caps = _settings.HardwareCaps;
        bool all = _settings.ShowAllHardwareModules || caps == null;
        _hiddenPages.Clear();
        if (!all && !caps!.HasCompatibleLcd) _hiddenPages.Add("PageLcd");
        if (!all && !caps!.HasRgbPeripheral) _hiddenPages.Add("PageInteg");
        foreach (var a in Areas)
            if (_areaButtons.TryGetValue(a.Key, out var rb))
                rb.Visibility = a.Tabs.All(t => _hiddenPages.Contains(t.Page)) ? Visibility.Collapsed : Visibility.Visible;
        IntegAulaCard.Visibility = all || caps!.HasCompatibleKeyboard ? Visibility.Visible : Visibility.Collapsed;
        IntegCorsairBox.Visibility = all || caps!.HasCorsairDevice ? Visibility.Visible : Visibility.Collapsed;
        // PC de mesa: a página Energia fica só com fonte, plano e consumo (os cartões de bateria nem aparecem).
        if (caps is { HasBattery: false } && !_settings.ShowAllHardwareModules) EnergyStatus.Text = "Fonte de energia, plano e consumo (PC de mesa)";
        _currentArea = ""; // remonta as abas da área aberta
        SyncArea(_currentPage);
    }

    private static Area AreaOf(string page) => Areas.FirstOrDefault(a => a.Tabs.Any(t => t.Page == page)) ?? Areas[0];

    private void OpenArea(string key)
    {
        var a = Areas.First(x => x.Key == key);
        string page = _areaLastPage.GetValueOrDefault(key) is string last && !_hiddenPages.Contains(last) ? last
                    : a.Tabs.FirstOrDefault(t => !_hiddenPages.Contains(t.Page))?.Page ?? a.Tabs[0].Page;
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
        _currentPage = page;
        _areaLastPage[area.Key] = page;
        if (_areaButtons.TryGetValue(area.Key, out var ab)) ab.IsChecked = true;
        if (_currentArea != area.Key)
        {
            _currentArea = area.Key;
            AreaTabs.Children.Clear();
            // Uma página escondida só ganha aba quando é aberta por um atalho (ex.: o painel).
            foreach (var t in area.Tabs.Where(t => !_hiddenPages.Contains(t.Page) || t.Page == page))
            {
                var tab = new RadioButton { Style = (Style)FindResource("TopTab"), GroupName = "AreaTabs", Content = t.Label, Tag = t.Page };
                string target = t.Page;
                tab.Click += (_, _) => NavigateTo(target);
                AreaTabs.Children.Add(tab);
            }
        }
        foreach (RadioButton t in AreaTabs.Children) t.IsChecked = t.Tag as string == page;
        AreaTabsBar.Visibility = AreaTabs.Children.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
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
        var hits = Infrastructure.SearchIndex.Search(q, SearchEntries().Where(x => !_hiddenPages.Contains(x.Page)));
        foreach (var hit in hits)
        {
            var b = new Button { Style = (Style)FindResource("NavResult"), Tag = hit,
                                 Content = new StackPanel { Children = {
                                     new TextBlock { Text = hit.Label, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = hit.Label },
                                     new TextBlock { Text = hit.Context, FontSize = 11, Opacity = 0.6 } } } };
            b.Click += (_, _) => { NavSearch.Text = ""; GoToSearchHit(hit); };
            NavSearchResults.Children.Add(b);
        }
        if (hits.Count == 0) NavSearchResults.Children.Add(new TextBlock { Text = "Nada encontrado.", Margin = new Thickness(16, 4, 0, 4), Opacity = 0.6 });
        NavSearchResults.Visibility = Visibility.Visible;
        AreaNav.Visibility = Visibility.Collapsed;
    }

    private List<Infrastructure.SearchEntry>? _searchEntries;

    /// <summary>Abas e ajustes, montado na primeira busca. Os títulos não mudam, então uma vez basta.</summary>
    private List<Infrastructure.SearchEntry> SearchEntries()
    {
        if (_searchEntries != null) return _searchEntries;
        var list = Areas.SelectMany(a => a.Tabs.Select(t => new Infrastructure.SearchEntry(t.Page, t.Label, a.Label, t.Keywords))).ToList();
        void AddTweaks(string page, string context, IEnumerable<Tweak> tweaks)
        {
            foreach (var t in tweaks)
                if (!list.Any(e => e.Label == t.Title))
                    list.Add(new Infrastructure.SearchEntry(page, t.Title, context, t.Id, t.Id));
        }
        try
        {
            AddTweaks("PageOptimize", "Otimizar · Ajustes e memória", _tweaks);
            AddTweaks("PageClean", "Otimizar · Limpeza", _cleanTweaks);
            AddTweaks("PageComp", "Jogos · Competitivo", (_compGroups ?? WindowsTweaks.CompetitiveGroups(_tweaks, _settings)).Values.SelectMany(g => g));
        }
        catch (Exception ex) { Infrastructure.AppLog.Error("busca", ex); }
        return _searchEntries = list;
    }

    private void NavSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { NavSearch.Text = ""; e.Handled = true; }
        else if (e.Key == Key.Enter && NavSearchResults.Children.OfType<Button>().FirstOrDefault() is { Tag: Infrastructure.SearchEntry hit })
        {
            NavSearch.Text = "";
            GoToSearchHit(hit);
            e.Handled = true;
        }
    }

    private void GoToSearchHit(Infrastructure.SearchEntry hit)
    {
        NavigateTo(hit.Page);
        if (hit.TargetId.Length > 0) _ = FocusTweakAsync(hit.Page, hit.TargetId);
    }

    /// <summary>Listas de ajustes de cada página que a busca alcança.</summary>
    private ItemsControl[] TweakLists(string page) => page switch
    {
        "PageOptimize" => new ItemsControl[] { OptTweaks },
        "PageClean" => new ItemsControl[] { CleanTweaks },
        "PageComp" => new ItemsControl[] { CompLatency, CompNet, CompGames, CompSystem },
        _ => Array.Empty<ItemsControl>(),
    };

    /// <summary>Rola até o ajuste e o destaca por um instante. A página pode montar a lista só ao abrir: espera ela aparecer, com limite.</summary>
    private Task FocusTweakAsync(string page, string id) => Infrastructure.UiGuard.RunAsync("busca", async () =>
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            foreach (var list in TweakLists(page))
            {
                if (list.Items.OfType<Tweak>().FirstOrDefault(t => t.Id == id) is not Tweak tweak) continue;
                if (UI.ListFocus.Realize(list, tweak) is not FrameworkElement row) continue;
                row.BringIntoView();
                var border = UI.ListFocus.FindChild<Border>(row);
                if (border == null) return;
                border.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
                await Task.Delay(1500);
                border.ClearValue(Border.BackgroundProperty); // o elemento pode ser reaproveitado para outro ajuste
                return;
            }
            await Task.Delay(100);
        }
    }, _ => { });
}
