using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ThgOptimization.Core.Intelligence;
using ThgOptimization.Infrastructure.Intelligence;

namespace ThgOptimization;

/// <summary>
/// tHg Intelligence na janela: acompanha a sessão de jogo, mostra diagnóstico, recomendações e histórico, perfis de jogos e o inventário
/// do PC. A lógica está em Core/Intelligence; aqui só se desenha e se encaminha o clique para o executor de ações.
/// </summary>
public partial class MainWindow
{
    private static readonly CultureInfo IntelPt = CultureInfo.GetCultureInfo("pt-BR");
    private IntelligenceService? _intel;
    private bool _intelFailed;
    private int _intelTicks, _intelSelfLevel;
    private bool _intelPausedTimers;
    private DiagnosisReport? _intelReport;
    private IReadOnlyList<Recommendation> _intelRecs = Array.Empty<Recommendation>();
    private string _intelSource = "";
    private ActionScope _intelScope = ActionScope.Global;
    private int _intelOnboardStep;
    private List<LibGame>? _intelLibrary;
    private string? _intelGameKey;
    private string _dashIntelShown = "";
    private DateTime _intelMachineRetryAt;

    /// <summary>O serviço, criado no primeiro uso. Se a pasta protegida não puder ser usada, o resto do app segue sem o Intelligence.</summary>
    private IntelligenceService? Intel
    {
        get
        {
            if (_intel != null || _intelFailed || _closed) return _intel;
            try
            {
                var service = new IntelligenceService(_settings);
                service.Analyzed += a => Dispatcher.BeginInvoke(() => IntelSafe(() => OnIntelAnalyzed(a)));
                service.SelfImpactChanged += OnIntelSelfImpact;
                service.Controller.StateChanged += (_, _) => _dashIntelShown = "";
                _intel = service;
                if (service.Store.LoadWarning is string warning) AddEvent("", "tHg Intelligence", warning, "");
            }
            catch (Exception ex)
            {
                _intelFailed = true;
                Infrastructure.AppLog.Error("intelligence", ex);
            }
            return _intel;
        }
    }

    // ---------------------------------------------------------------- tique

    /// <summary>Uma vez por segundo. Só observa o Modo Jogo e, a cada 10 s, mede o consumo do próprio tHg.</summary>
    private void IntelTick()
    {
        if (_gm == null && !_settings.AutoGameMode && _intel == null) return; // nada para observar: o serviço nem é criado
        if (Intel is not { } intel) return;
        if (intel.Machine == null || (DateTime.Now - intel.Machine.ScannedAt).TotalMinutes > 30) RefreshIntelMachine();
        intel.Observe(_gm, capturing: _labStop != null);
        if (++_intelTicks % 10 == 0) intel.SelfTick(_gm?.Active == true);
        if (PageDash.IsVisible) UpdateDashIntel();
        if (PageIntel.IsVisible) UpdateIntelState();
    }

    /// <summary>Monta o inventário com o que o app já leu. Sem a lista do sistema ainda, fica para o próximo tique.</summary>
    private void RefreshIntelMachine()
    {
        if (_intel == null || DateTime.Now < _intelMachineRetryAt || InfoCategories.ItemsSource is not IEnumerable<InfoNode> system) return;
        _intelMachineRetryAt = DateTime.Now.AddSeconds(30); // uma falha aqui não pode se repetir a cada tique
        try
        {
            _intel.Machine = MachineProfileBuilder.Build(system.ToList(), _gpus, Infrastructure.CpuTopologySnapshot.Current, _monitor.IsOpen ? _monitor : null,
                                                         GamerAudit.Power(), _cpuName, _frames.Running, DateTime.Now);
        }
        catch (Exception ex) { Infrastructure.AppLog.Warn("intelligence", ex); }
    }

    /// <summary>O governador de consumo mudou de nível: reduz ou devolve a coleta não essencial, e conta ao usuário o que mudou.</summary>
    private void OnIntelSelfImpact(int level, double cpuPct)
    {
        int before = _intelSelfLevel;
        _intelSelfLevel = level;
        _ovEffective = null; // o overlay recalcula os itens pelo perfil em vigor
        if (level >= 2 && !_intelPausedTimers)
        {
            _intelPausedTimers = Infrastructure.TimerRegistry.PauseNonEssential() > 0;
        }
        else if (level < 2 && _intelPausedTimers)
        {
            Infrastructure.TimerRegistry.ResumePaused();
            _intelPausedTimers = false;
        }
        if (level > before)
            Toast("tHg reduziu a própria coleta", $"O tHg estava usando {cpuPct.ToString("0.0", IntelPt)}% da CPU durante o jogo. " +
                  (level >= 2 ? "O overlay ficou no perfil econômico e as tarefas não essenciais (luzes, telas LCD) foram pausadas." : "O overlay ficou no perfil econômico: menos itens, atualizados a cada 1 s.") +
                  " Volta ao normal quando o consumo cair ou o jogo fechar.", "", quietInGame: true);
    }

    private string IntelOverlayText()
    {
        if (_intel == null) return "—";
        string state = GameSessionController.Label(_intel.Controller.State);
        if (_gm?.ActiveGameKey is not string key) return state;
        string preset = UsagePreset.Parse(_intel.Store.Read(d => d.Games.GetValueOrDefault(key)?.Preset)).Name;
        return $"{preset} · {state}";
    }

    // ---------------------------------------------------------------- painel inicial

    private void BtnDashIntel_Click(object sender, RoutedEventArgs e) => NavigateTo("PageIntel");

    /// <summary>Tudo o que a interface do Intelligence faz fora do tique passa por aqui: uma falha vira registro e aviso, nunca fecha o app.</summary>
    private void IntelSafe(Action body)
    {
        try { body(); }
        catch (Exception ex)
        {
            Infrastructure.AppLog.Error("intelligence", ex);
            Toast("tHg Intelligence", "A operação falhou: " + Err(ex), "");
        }
    }

    private void UpdateDashIntel()
    {
        if (_intel is not { } intel) return;
        var state = intel.Controller.State;
        var last = intel.Store.Read(d => d.History.FirstOrDefault());
        string headline, sub, rec = "";
        if (intel.Controller.Current is { } playing)
        {
            headline = $"{GameSessionController.Label(state)} · {playing.GameName}";
            sub = $"Sessão em andamento há {SessionReport.Duration(playing.Duration(DateTime.Now).TotalSeconds)}. O diagnóstico sai quando o jogo fechar.";
        }
        else if (last != null)
        {
            headline = $"{last.GameName}: {last.Headline}";
            sub = $"Última sessão analisada em {last.At.ToString("dd/MM HH:mm", IntelPt)} · {last.Minutes.ToString("0", IntelPt)} min · {last.Learning}";
            if (last.Recommendations.Count > 0) rec = "Recomendação principal: " + last.Recommendations[0];
        }
        else
        {
            headline = _settings.AutoGameMode ? "Aguardando a primeira sessão de jogo" : "Diagnóstico inteligente deste PC";
            sub = _settings.AutoGameMode
                ? "Abra um jogo da sua biblioteca. Ao fechá-lo, o tHg diagnostica a sessão e explica o que encontrou."
                : "O Modo Jogo automático está desligado. Sem ele não há sessão para analisar: ligue em tHg Intelligence ou em Jogos › Modo Jogo.";
        }
        string shown = headline + "|" + sub + "|" + rec;
        if (shown == _dashIntelShown) return;
        _dashIntelShown = shown;
        DashIntelHeadline.Text = headline;
        DashIntelSub.Text = sub;
        DashIntelRec.Text = rec;
        DashIntelRec.Visibility = rec.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- página Diagnóstico

    private void InitIntel()
    {
        if (Intel is not { } intel)
        {
            IntelHeadline.Text = "tHg Intelligence indisponível";
            IntelHeadlineSub.Text = "A pasta protegida de dados não pôde ser usada. O restante do app funciona normalmente. Veja o registro em Configurações.";
            BtnIntelDiagnose.IsEnabled = false;
            return;
        }
        RefreshIntelMachine();
        SyncIntelLevel();
        UpdateIntelState();
        ShowIntelOnboarding();
        if (_intelReport == null && intel.LastAnalysis is { } a) SetIntelResult(a.Report, a.Recommendations, "Sessão mais recente", IntelScopeOf(a.Report.Context.GameKey, a.Report.Context.Game));
        RenderIntelResult();
        RenderIntelHistory();
    }

    private void UpdateIntelState()
    {
        if (_intel is not { } intel) return;
        var c = intel.Controller;
        IntelState.Text = c.Current is { } g ? $"{GameSessionController.Label(c.State)} · {g.GameName}" : GameSessionController.Label(c.State);
        string sub = c.State switch
        {
            SessionState.Idle => "O Modo Jogo automático está desligado: nenhuma sessão é reconhecida. Ligue em Jogos › Modo Jogo.",
            SessionState.Detecting => "Esperando um jogo da sua biblioteca ir para a tela. A detecção usa só o nome e o caminho do programa: não lê a memória do jogo nem injeta nada.",
            SessionState.Gaming or SessionState.Capturing => "Coletando uma amostra por segundo e os frames (ETW). O tHg reduz as próprias tarefas enquanto você joga.",
            _ => "",
        };
        if (c.LastError is string error) sub = error;
        else if (intel.LastSkipReason is string skip && c.Current == null) sub += (sub.Length > 0 ? " " : "") + skip;
        if (intel.SelfImpactLevel > 0) sub += $" Coleta reduzida (o tHg estava em {intel.SelfCpuPercent.ToString("0.0", IntelPt)}% da CPU).";
        IntelStateSub.Text = sub;
    }

    private void SyncIntelLevel()
    {
        if (_intel is not { } intel) return;
        var level = intel.Level;
        IntelLevelDiag.IsChecked = level == AutomationLevel.DiagnosisOnly;
        IntelLevelRec.IsChecked = level == AutomationLevel.Recommend;
        IntelLevelAuto.IsChecked = level == AutomationLevel.SafeAuto;
        IntelLevelText.Text = level switch
        {
            AutomationLevel.DiagnosisOnly => "O tHg diagnostica e explica. Nenhuma ação do Intelligence é aplicada, nem com clique.",
            AutomationLevel.SafeAuto => "O tHg aplica sozinho só as ações de baixo risco marcadas abaixo, sempre com registro e com medição depois. Ajustes avançados continuam pedindo a sua autorização.",
            _ => "O tHg recomenda e explica; você decide cada ação. Toda ação aplicada fica no histórico e pode ser desfeita.",
        };
        IntelAutoList.Children.Clear();
        if (level != AutomationLevel.SafeAuto) return;
        var approved = _settings.Read(s => s.IntelligenceAutoActions.ToHashSet());
        foreach (var spec in intel.Executor.Specs.Where(s => s.Risk == RiskClass.B))
        {
            var box = new CheckBox { Content = spec.Name, IsChecked = approved.Contains(spec.Id), Tag = spec.Id, Margin = new Thickness(0, 4, 0, 0), ToolTip = spec.Description + "\n" + spec.Changes };
            box.Click += IntelAutoAction_Click;
            IntelAutoList.Children.Add(box);
        }
    }

    private void IntelAutoAction_Click(object sender, RoutedEventArgs e) => IntelSafe(() =>
    {
        if (sender is not CheckBox { Tag: string id } box) return;
        _settings.Mutate(() =>
        {
            _settings.IntelligenceAutoActions.Remove(id);
            if (box.IsChecked == true) _settings.IntelligenceAutoActions.Add(id);
        });
        _settings.Save();
    });

    private void IntelLevel_Click(object sender, RoutedEventArgs e) => IntelSafe(() =>
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        _settings.IntelligenceAutomation = key;
        _settings.Save();
        SyncIntelLevel();
        RenderIntelResult(); // os botões APLICAR dependem do nível
    });

    private async void BtnIntelDiagnose_Click(object sender, RoutedEventArgs e) =>
        await Infrastructure.UiGuard.RunButtonAsync(BtnIntelDiagnose, "intelligence", DiagnoseNowAsync,
            ex => Toast("tHg Intelligence", "O diagnóstico falhou: " + Err(ex), ""));

    /// <summary>Analisa a sessão gravada mais recente com o motor atual. Não aprende nem grava: é uma releitura.</summary>
    private async Task DiagnoseNowAsync()
    {
        if (Intel is not { } intel) return;
        RefreshIntelMachine();
        IntelHeadline.Text = "Analisando…";
        IntelHeadlineSub.Text = "Lendo a sessão mais recente e conferindo o que este PC consegue medir.";
        var result = await Task.Run(() =>
        {
            var store = Core.Benchmark.SessionStore.Default;
            var first = store.List().FirstOrDefault();
            if (first.Path == null || Core.Benchmark.SessionStore.Load(first.Path) is not { } timeline) return null;
            string key = intel.Store.Read(d => d.Games.Values.FirstOrDefault(g => g.Name == timeline.Header.Game)?.Key) ?? timeline.Header.Game;
            var (report, recs) = intel.Orchestrator.Inspect(intel.Telemetry(timeline, key), intel.Machine);
            return new { report, recs, key };
        });
        if (_closed) return;
        if (result == null)
        {
            _intelReport = null;
            _intelRecs = Array.Empty<Recommendation>();
            RenderIntelResult();
            IntelHeadline.Text = "Ainda não há sessão de jogo para analisar";
            IntelHeadlineSub.Text = _settings.AutoGameMode
                ? "Abra um jogo da sua biblioteca e jogue pelo menos 5 minutos. Ao fechar, a sessão é analisada. Não inventamos um diagnóstico sem dados."
                : "O Modo Jogo automático está desligado, então nenhuma sessão foi gravada. Ligue abaixo e jogue pelo menos 5 minutos.";
            if (!_settings.AutoGameMode && intel.Executor.Spec(ActionIds.AutoGameMode) is { } spec)
                IntelFindings.Children.Add(IntelButton("LIGAR O MODO JOGO AUTOMÁTICO", spec.Description, primary: true, () => ApplyIntelAction(ActionIds.AutoGameMode, ActionScope.Global, "Sem sessão para analisar.")));
            return;
        }
        SetIntelResult(result.report, result.recs, $"Sessão de {result.report.Context.Start.ToString("dd/MM HH:mm", IntelPt)} · {result.report.Context.Game}",
                       IntelScopeOf(result.key, result.report.Context.Game));
        RenderIntelResult();
    }

    private ActionScope IntelScopeOf(string key, string name) => new(key, name, _intel?.Store.Read(d => d.Games.GetValueOrDefault(key)?.Exe));

    private void SetIntelResult(DiagnosisReport report, IReadOnlyList<Recommendation> recs, string source, ActionScope scope)
    {
        _intelReport = report;
        _intelRecs = recs;
        _intelSource = source;
        _intelScope = scope;
    }

    /// <summary>A sessão que acabou foi analisada: guarda, avisa e atualiza o que estiver na tela.</summary>
    private void OnIntelAnalyzed(SessionAnalysis a)
    {
        if (_closed) return;
        SetIntelResult(a.Report, a.Recommendations, $"Sessão de {a.Report.Context.Start.ToString("dd/MM HH:mm", IntelPt)} · {a.Report.Context.Game}",
                       IntelScopeOf(a.Report.Context.GameKey, a.Report.Context.Game));
        _dashIntelShown = "";
        string extra = a.ActionNotes.Count > 0 ? "\n" + a.ActionNotes[0] : a.Recommendations.Count > 0 ? $"\n{a.Recommendations.Count} recomendação(ões) em tHg Intelligence." : "";
        Toast($"Sessão analisada · {a.Report.Context.Game}", a.Report.Headline + extra, "");
        if (PageIntel.IsVisible) { RenderIntelResult(); RenderIntelHistory(); }
        if (PageIntelGames.IsVisible) InitIntelGames();
    }

    private void RenderIntelResult()
    {
        IntelFindings.Children.Clear();
        IntelRecs.Children.Clear();
        if (_intelReport is not { } r)
        {
            IntelRecsEmpty.Visibility = Visibility.Visible;
            return;
        }
        IntelHeadline.Text = r.Headline;
        IntelHeadlineSub.Text = $"{_intelSource} · {SessionReport.Duration(r.Quality.Seconds)} de coleta" +
                                (r.Frames is { Origin: MetricOrigin.Captured } f ? $" · {f.Frames.ToString("N0", IntelPt)} frames medidos" : " · sem gravação de frames");

        if (r.Frames is { } fr)
        {
            var facts = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            bool captured = fr.Origin == MetricOrigin.Captured;
            facts.Children.Add(IntelFact("FPS MÉDIO", fr.AvgFps.ToString("0", IntelPt), captured ? "medido" : "estimado"));
            facts.Children.Add(IntelFact("1% LOW", fr.Low1Fps is double l1 ? l1.ToString("0", IntelPt) : "—", fr.Low1Fps != null ? "medido" : "indisponível"));
            facts.Children.Add(IntelFact("0,1% LOW", fr.Low01Fps is double l01 ? l01.ToString("0", IntelPt) : "—", fr.Low01Fps != null ? "medido" : "amostra pequena"));
            facts.Children.Add(IntelFact("FRAMETIME P99", captured ? fr.P99Ms.ToString("0.0", IntelPt) + " ms" : "—", captured ? "medido" : "indisponível"));
            facts.Children.Add(IntelFact("ENGASGOS/MIN", captured ? fr.StuttersPerMinute.ToString("0.#", IntelPt) : "—", captured ? "medido" : "indisponível"));
            IntelFindings.Children.Add(facts);
        }

        foreach (var x in r.Findings.OrderByDescending(x => x.Severity).ThenByDescending(x => x.Confidence.Score)) IntelFindings.Children.Add(IntelFindingCard(x));
        if (r.RuledOut.Count > 0) IntelFindings.Children.Add(IntelList("VERIFICADO E SEM PROBLEMA", r.RuledOut, "Good"));
        if (r.Limitations.Count > 0) IntelFindings.Children.Add(IntelList("LIMITAÇÕES DESTA ANÁLISE", r.Limitations, "Faint"));
        var copy = IntelButton("COPIAR O RELATÓRIO", "Copia o relatório completo desta sessão em texto", primary: false, () =>
        {
            try { Clipboard.SetText(SessionReport.Text(r, null, _intelRecs)); Toast("tHg Intelligence", "Relatório copiado.", ""); }
            catch (System.Runtime.InteropServices.ExternalException ex) { Toast("tHg Intelligence", "Não foi possível copiar: " + Err(ex), ""); }
        });
        copy.Margin = new Thickness(0, 12, 0, 0);
        IntelFindings.Children.Add(copy);

        IntelRecsEmpty.Visibility = _intelRecs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IntelRecsEmpty.Text = "Nenhuma otimização recomendada: não há evidência de problema que justifique uma mudança.";
        foreach (var rec in _intelRecs) IntelRecs.Children.Add(IntelRecCard(rec));
    }

    private Border IntelFindingCard(Finding x)
    {
        var body = new StackPanel();
        var head = new DockPanel();
        var pill = IntelPill(x.Kind == DiagnosisKind.Inconclusive ? "inconclusivo" : $"confiança {x.Confidence.Text} · {(x.Confidence.Score * 100).ToString("0", IntelPt)}%",
                             x.Severity == Severity.Problem ? "Bad" : x.Severity == Severity.Attention ? "Warn" : "Good");
        DockPanel.SetDock(pill, Dock.Right);
        head.Children.Add(pill);
        head.Children.Add(new TextBlock { Text = x.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(head);
        body.Children.Add(IntelText(x.Interpretation, "Muted", 13, new Thickness(0, 6, 0, 0)));
        foreach (var e in x.Evidence)
            body.Children.Add(IntelText($"• {e.Metric}: {e.Value}  ·  {e.Meaning}" + (e.Origin == MetricOrigin.Estimated ? "  (estimado)" : ""), "Fg", 12, new Thickness(0, 4, 0, 0)));
        foreach (string l in x.Limitations) body.Children.Add(IntelText("Limitação: " + l, "Faint", 11, new Thickness(0, 4, 0, 0)));
        if (x.Confidence.Reasons.Count > 0 && x.Kind != DiagnosisKind.Inconclusive)
            body.Children.Add(IntelText("Por que esta confiança: " + string.Join("; ", x.Confidence.Reasons) + ".", "Faint", 11, new Thickness(0, 4, 0, 0)));
        var card = new Border { Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = body };
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        System.Windows.Automation.AutomationProperties.SetName(card, $"{x.Title}, confiança {x.Confidence.Text}");
        return card;
    }

    private Border IntelRecCard(Recommendation rec)
    {
        var body = new StackPanel();
        var head = new DockPanel();
        var pill = IntelPill(rec.ActionId != null ? $"ação · risco {rec.Risk}" : "orientação", rec.Risk == RiskClass.C ? "Warn" : "Good");
        DockPanel.SetDock(pill, Dock.Right);
        head.Children.Add(pill);
        head.Children.Add(new TextBlock { Text = rec.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        body.Children.Add(head);
        body.Children.Add(IntelText($"Problema: {rec.Problem} (confiança {rec.Confidence.Text})", "Muted", 12, new Thickness(0, 6, 0, 0)));
        body.Children.Add(IntelText(rec.Proposal, "Fg", 13, new Thickness(0, 6, 0, 0)));
        if (rec.Benefit.Length > 0) body.Children.Add(IntelText("Benefício possível: " + rec.Benefit, "Muted", 12, new Thickness(0, 6, 0, 0)));
        body.Children.Add(IntelText("Risco: " + rec.RiskText, "Muted", 12, new Thickness(0, 2, 0, 0)));
        if (rec.ActionId != null)
        {
            var spec = _intel?.Executor.Spec(rec.ActionId);
            if (spec is { Changes.Length: > 0 }) body.Children.Add(IntelText("O que muda: " + spec.Changes, "Muted", 12, new Thickness(0, 2, 0, 0)));
            if (rec.Compatibility.Length > 0) body.Children.Add(IntelText("Compatibilidade: " + rec.Compatibility, "Muted", 12, new Thickness(0, 2, 0, 0)));
            body.Children.Add(IntelText("Reiniciar: " + (rec.NeedsRestart ? "sim" : "não é preciso") + " · Como desfazer: " + rec.HowToUndo, "Muted", 12, new Thickness(0, 2, 0, 0)));
            body.Children.Add(IntelText("Como o resultado é medido: " + rec.HowToMeasure, "Muted", 12, new Thickness(0, 2, 0, 0)));
        }
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (rec.ActionId is string actionId)
        {
            bool off = _intel?.Level == AutomationLevel.DiagnosisOnly;
            var apply = IntelButton("APLICAR", off ? "A automação está em \"Só diagnóstico\": mude o nível acima para aplicar." : "Aplica esta ação. Fica registrada e pode ser desfeita.",
                                    primary: true, () => ApplyIntelAction(actionId, _intelScope, rec.Problem));
            apply.IsEnabled = !off;
            buttons.Children.Add(apply);
        }
        if (rec.Destination is string destination && Destinations.All.Contains(destination))
            buttons.Children.Add(IntelButton(Destinations.Label(destination), "Abre a tela relacionada", primary: rec.ActionId == null, () => OpenIntelDestination(destination)));
        buttons.Children.Add(IntelButton("DISPENSAR", "Não mostrar mais esta recomendação para este jogo", primary: false, () => DismissIntelRec(rec)));
        body.Children.Add(buttons);
        var card = new Border { Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = body };
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        return card;
    }

    /// <summary>Destinos vêm de uma lista fechada (Destinations): uma página do app ou uma tela de Configurações do Windows.</summary>
    private void OpenIntelDestination(string destination)
    {
        if (!Destinations.All.Contains(destination)) return;
        if (destination.StartsWith("page:", StringComparison.Ordinal)) { NavigateTo(destination[5..]); return; }
        var opened = Unelevated.OpenInShell(destination[4..]);
        if (!opened.Started) Toast("tHg Intelligence", "Não foi possível abrir a tela do Windows: " + opened.Error, "");
    }

    private void DismissIntelRec(Recommendation rec)
    {
        if (_intel is not { } intel || _intelScope.GameKey is not string key) return;
        var profile = intel.Store.Game(key, _intelScope.GameName ?? key);
        intel.Store.Mutate(_ => { if (!profile.Dismissed.Contains(rec.Id)) profile.Dismissed.Add(rec.Id); });
        _intelRecs = _intelRecs.Where(r => r.Id != rec.Id).ToList();
        RenderIntelResult();
    }

    /// <summary>Aplica uma ação do catálogo com a autorização dada agora pelo clique. Ajuste avançado (classe C) pede confirmação com a explicação.</summary>
    private void ApplyIntelAction(string actionId, ActionScope scope, string reason)
    {
        if (_intel is not { } intel || intel.Executor.Spec(actionId) is not { } spec) return;
        if (spec.Risk == RiskClass.C && !ThgDialog.Confirm(spec.Name,
                $"{spec.Description}\n\nO que muda: {spec.Changes}\nCompatibilidade: {spec.Compatibility}\n\nÉ um ajuste avançado: o efeito varia por jogo. " +
                "O tHg mede as próximas sessões e desfaz sozinho se houver piora ou nenhum ganho.", "Aplicar", "Cancelar", MessageBoxImage.Question)) return;
        string? signature = intel.Machine is { } m ? new SessionContext("", "", DateTime.Now) { HardwareId = m.HardwareId, GpuDriver = m.GpuDriver, Resolution = m.Resolution, OnBattery = m.OnBattery }.Signature : null;
        var result = intel.Executor.Apply(actionId, scope, intel.Policy, userApprovedNow: true, reason, signature: signature);
        Toast("tHg Intelligence", result.Message, "");
        if (result.Ok) _intelRecs = _intelRecs.Where(r => r.ActionId != actionId).ToList();
        _dashIntelShown = "";
        if (PageIntel.IsVisible) { RenderIntelResult(); RenderIntelHistory(); UpdateIntelState(); }
        if (actionId == ActionIds.AutoGameMode && result.Ok) StartGameModeEngine(); // o motor só existe com a opção ligada na abertura
    }

    private void RenderIntelHistory()
    {
        IntelActions.Children.Clear();
        IntelHistory.Children.Clear();
        if (_intel is not { } intel) return;
        var journal = intel.Store.Read(d => d.Journal.Take(12).ToList());
        var history = intel.Store.Read(d => d.History.Take(15).ToList());
        IntelHistoryEmpty.Visibility = journal.Count == 0 && history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var rec in journal)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            if (rec.IsActive)
            {
                var undo = IntelButton("DESFAZER", "Devolve o valor anterior. Se outro programa mudou o valor depois, nada é sobrescrito.", primary: false, () =>
                {
                    var r = intel.Executor.Revert(rec, "Desfeita pelo usuário.");
                    Toast("tHg Intelligence", r.Message, "");
                    RenderIntelHistory();
                });
                DockPanel.SetDock(undo, Dock.Right);
                row.Children.Add(undo);
            }
            string status = rec.Status switch
            {
                ActionStatus.Applied => rec.Evaluation == "kept" ? "aplicada · mantida após medição" : "aplicada · medindo o resultado",
                ActionStatus.Reverted => "desfeita",
                ActionStatus.Conflict => "conflito: valor alterado por fora, não sobrescrito",
                ActionStatus.RolledBack => "falhou · estado anterior restaurado",
                ActionStatus.RollbackFailed => "falhou · confira o valor original",
                ActionStatus.Cancelled => "cancelada",
                _ => rec.Status.ToString(),
            };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"{rec.ActionName} · {status}", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            text.Children.Add(IntelText($"{rec.At.ToString("dd/MM/yyyy HH:mm", IntelPt)} · {(rec.GameName ?? "todo o PC")} · {(rec.Automatic ? "automática" : "autorizada por você")} · {rec.Detail}", "Muted", 12, default));
            if (!string.IsNullOrEmpty(rec.EvaluationText)) text.Children.Add(IntelText(rec.EvaluationText!, "Faint", 11, new Thickness(0, 2, 0, 0)));
            row.Children.Add(text);
            IntelActions.Children.Add(row);
        }

        foreach (var h in history)
        {
            var exp = new Expander { Style = (Style)FindResource("ThgExpander"), Margin = new Thickness(0, 0, 0, 4),
                                     Header = $"{h.At.ToString("dd/MM HH:mm", IntelPt)} · {h.GameName} · {h.Minutes.ToString("0", IntelPt)} min · {h.Headline}" };
            var inner = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            if (h.Learning.Length > 0) inner.Children.Add(IntelText("Aprendizado: " + h.Learning, "Muted", 12, default));
            if (h.Report is string report)
            {
                var box = new TextBox { Text = report, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MaxHeight = 320, FontSize = 12,
                                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0), FontFamily = (FontFamily)FindResource("Mono") };
                System.Windows.Automation.AutomationProperties.SetName(box, "Relatório da sessão");
                inner.Children.Add(box);
            }
            else inner.Children.Add(IntelText(string.Join(" · ", h.Findings.Select(f => f.Title)), "Muted", 12, new Thickness(0, 4, 0, 0)));
            exp.Content = inner;
            IntelHistory.Children.Add(exp);
        }
    }

    // ---------------------------------------------------------------- primeiro uso

    private void ShowIntelOnboarding()
    {
        if (_settings.IntelligenceOnboardingDone) { IntelOnboard.Visibility = Visibility.Collapsed; return; }
        IntelOnboard.Visibility = Visibility.Visible;
        IntelOnboardBody.Children.Clear();
        var m = _intel?.Machine;
        switch (_intelOnboardStep)
        {
            case 0:
                IntelOnboardStep.Text = "PRIMEIRO USO · 1 DE 3";
                IntelOnboardTitle.Text = "O tHg agora diagnostica antes de otimizar";
                IntelOnboardText.Text = "O tHg Intelligence acompanha cada sessão de jogo, identifica o que limitou o desempenho, explica com as evidências, recomenda só o que se justifica, " +
                                        "mede o resultado e desfaz o que não ajudar. Tudo roda neste PC: nenhum dado é enviado.";
                IntelOnboardBody.Children.Add(IntelText(m != null
                    ? $"Hardware detectado: {m.HardwareId}. {m.Blind().Count} medida(s) não estão disponíveis neste PC; veja em Este PC."
                    : "O hardware ainda está sendo lido. Você pode continuar.", "Fg", 13, default));
                BtnIntelOnboardNext.Content = "CONTINUAR";
                break;
            case 1:
                IntelOnboardStep.Text = "PRIMEIRO USO · 2 DE 3";
                IntelOnboardTitle.Text = "Quanto o tHg pode fazer sozinho?";
                IntelOnboardText.Text = "Você escolhe e pode mudar a qualquer momento no cartão abaixo. O padrão é Recomendar: o tHg explica e você decide cada ação. " +
                                        "Overclock, tensão e limites de potência nunca são alterados pelo Intelligence.";
                foreach (var (key, title, text) in new[]
                {
                    ("diagnosis", "Só diagnóstico", "Nenhuma ação é aplicada."),
                    ("recommend", "Recomendar (padrão)", "Você autoriza cada ação."),
                    ("safe-auto", "Automático seguro", "Aplica sozinho só as ações de baixo risco que você marcar."),
                })
                {
                    var choice = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "IntelOnboardLevel", Content = title, Tag = key, ToolTip = text,
                                                   IsChecked = _settings.IntelligenceAutomation == key, Margin = new Thickness(0, 0, 8, 0) };
                    choice.Click += IntelLevel_Click;
                    if (IntelOnboardBody.Children.Count == 0) IntelOnboardBody.Children.Add(new WrapPanel());
                    ((WrapPanel)IntelOnboardBody.Children[0]).Children.Add(choice);
                }
                BtnIntelOnboardNext.Content = "CONTINUAR";
                break;
            default:
                IntelOnboardStep.Text = "PRIMEIRO USO · 3 DE 3";
                IntelOnboardTitle.Text = "Reconhecer os jogos e fazer o primeiro diagnóstico";
                IntelOnboardText.Text = _settings.AutoGameMode
                    ? "O Modo Jogo automático já está ligado: o tHg reconhece os jogos da sua biblioteca sem ler a memória deles nem injetar nada. Concluir faz o primeiro diagnóstico com o que já existe."
                    : "Para reconhecer os jogos e gravar as sessões, o Modo Jogo automático precisa estar ligado. Ele usa só o nome e o caminho do programa: não lê a memória do jogo nem injeta nada. Você pode ligar agora ou depois.";
                if (!_settings.AutoGameMode)
                    IntelOnboardBody.Children.Add(IntelButton("LIGAR O MODO JOGO AUTOMÁTICO", "Liga a detecção de jogos e a gravação das sessões", primary: false,
                        () => { ApplyIntelAction(ActionIds.AutoGameMode, ActionScope.Global, "Primeiro uso do tHg Intelligence."); ShowIntelOnboarding(); }));
                BtnIntelOnboardNext.Content = "CONCLUIR E DIAGNOSTICAR";
                break;
        }
    }

    private async void BtnIntelOnboardNext_Click(object sender, RoutedEventArgs e) =>
        await Infrastructure.UiGuard.RunAsync("intelligence", async () =>
        {
            if (_intelOnboardStep < 2) { _intelOnboardStep++; ShowIntelOnboarding(); return; }
            FinishIntelOnboarding();
            await Infrastructure.UiGuard.RunButtonAsync(BtnIntelDiagnose, "intelligence", DiagnoseNowAsync, ex => Toast("tHg Intelligence", "O diagnóstico falhou: " + Err(ex), ""));
        }, ex => Toast("tHg Intelligence", "A operação falhou: " + Err(ex), ""));

    private void BtnIntelOnboardSkip_Click(object sender, RoutedEventArgs e) => IntelSafe(FinishIntelOnboarding);

    private void FinishIntelOnboarding()
    {
        _settings.IntelligenceOnboardingDone = true;
        _settings.Save();
        IntelOnboard.Visibility = Visibility.Collapsed;
        SyncIntelLevel();
    }

    // ---------------------------------------------------------------- perfis de jogos

    private sealed record IntelGameRow(string Key, string Name, string Store, string? Exe, string? InstallDir);

    private async void InitIntelGames() =>
        await Infrastructure.UiGuard.RunAsync("intelligence", LoadIntelGamesAsync, ex => Toast("tHg Intelligence", "Não foi possível montar a lista de jogos: " + Err(ex), ""));

    private async Task LoadIntelGamesAsync()
    {
        if (Intel is not { } intel) return;
        _intelLibrary ??= await Task.Run(() => GameLibrary.Scan(_settings.CopyList(s => s.CustomGames)));
        if (_closed) return;
        var rows = _intelLibrary.Select(g => new IntelGameRow(g.Key, g.Name, g.Store, g.ExePath, g.InstallDir)).ToList();
        foreach (var p in intel.Store.Read(d => d.Games.Values.ToList()))
            if (!rows.Any(r => r.Key == p.Key)) rows.Add(new IntelGameRow(p.Key, p.Name, p.Launcher ?? "", p.Exe, p.InstallDir));
        var profiles = intel.Store.Read(d => d.Games.ToDictionary(g => g.Key, g => (Played: g.Value.LastPlayed, Sessions: g.Value.History.Count)));
        rows = rows.OrderByDescending(r => profiles.GetValueOrDefault(r.Key).Played ?? DateTime.MinValue).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        string? keep = _intelGameKey;
        IntelGameList.Items.Clear();
        foreach (var r in rows)
        {
            var info = profiles.GetValueOrDefault(r.Key);
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = r.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(IntelText(info.Sessions > 0 ? $"{info.Sessions} sessão(ões) · última em {info.Played?.ToString("dd/MM", IntelPt)}" : $"{r.Store} · sem sessões ainda", "Muted", 11, default));
            var item = new ListBoxItem { Content = panel, Tag = r };
            System.Windows.Automation.AutomationProperties.SetName(item, r.Name);
            IntelGameList.Items.Add(item);
            if (r.Key == keep) IntelGameList.SelectedItem = item;
        }
        IntelGameListEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        IntelGameList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        IntelGamesSub.Text = $"{rows.Count} jogo(s) reconhecido(s), {profiles.Count(p => p.Value.Sessions > 0)} com sessões analisadas. Os dados ficam só neste PC.";
        if (IntelGameList.SelectedItem == null && IntelGameList.Items.Count > 0) IntelGameList.SelectedIndex = 0;
    }

    private void IntelGameList_SelectionChanged(object sender, SelectionChangedEventArgs e) => IntelSafe(() =>
    {
        if (IntelGameList.SelectedItem is ListBoxItem { Tag: IntelGameRow row }) RenderIntelGame(row);
    });

    private void RenderIntelGame(IntelGameRow row)
    {
        if (_intel is not { } intel) return;
        _intelGameKey = row.Key;
        var d = IntelGameDetail.Children;
        d.Clear();
        var p = intel.Store.Read(x => x.Games.GetValueOrDefault(row.Key));
        d.Add(new TextBlock { Text = row.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        d.Add(IntelText(string.Join(" · ", new[] { row.Store, row.Exe != null ? System.IO.Path.GetFileName(row.Exe) : "executável não identificado", row.InstallDir }.Where(s => !string.IsNullOrEmpty(s))),
                        "Muted", 12, new Thickness(0, 2, 0, 0)));
        d.Add(IntelText("Jogo não reconhecido, ou reconhecido errado? A associação de executáveis é feita em Jogos › Biblioteca (\"Adicionar jogo\"). Os dados aprendidos aqui podem ser apagados abaixo.",
                        "Faint", 11, new Thickness(0, 4, 0, 0)));

        d.Add(new TextBlock { Text = "PERFIL DE USO", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 16, 0, 6) });
        var presets = new WrapPanel();
        var current = UsagePreset.Parse(p?.Preset);
        var presetText = IntelText(current.Goal, "Muted", 12, new Thickness(0, 6, 0, 0));
        foreach (var preset in UsagePreset.All)
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "UsagePreset", Content = preset.Name, IsChecked = preset == current, ToolTip = preset.Goal, Margin = new Thickness(0, 0, 8, 4) };
            chip.Click += (_, _) =>
            {
                var profile = intel.Store.Game(row.Key, row.Name);
                intel.Store.Mutate(_ => { profile.Preset = preset.Key; profile.Exe ??= row.Exe; profile.Launcher ??= row.Store; profile.InstallDir ??= row.InstallDir; });
                presetText.Text = preset.Goal;
            };
            presets.Children.Add(chip);
        }
        d.Add(presets);
        d.Add(presetText);
        d.Add(IntelText("O perfil orienta quais recomendações aparecem para este jogo. Ele não aplica um pacote de ajustes.", "Faint", 11, new Thickness(0, 2, 0, 0)));

        d.Add(new TextBlock { Text = "COMPORTAMENTO HABITUAL NESTE PC", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 16, 0, 6) });
        if (p == null || p.Baselines.Count == 0)
            d.Add(IntelText("Ainda sem referência. Ela se forma com sessões de pelo menos 5 minutos, com o Modo Jogo automático ligado.", "Muted", 13, default));
        else
            foreach (var b in p.Baselines.OrderByDescending(b => b.Sessions.Count > 0 ? b.Sessions.Max(s => s.Start) : DateTime.MinValue).Take(3))
            {
                string[] parts = b.Signature.Split('|');
                string where = parts.Length >= 4 ? $"driver {(parts[1] == "?" ? "não lido" : parts[1])} · {(parts[2] == "?" ? "resolução não lida" : parts[2])} · {(parts[3] == "bat" ? "bateria" : "tomada")}" : b.Signature;
                string status = b.Established ? $"referência estabelecida com {b.Sessions.Count} sessões comparáveis" : $"em formação: {b.Sessions.Count} de {GameBaseline.MinEstablished} sessões";
                d.Add(IntelText($"{where} · {status}" + (b.Held.Count > 0 ? $" · {b.Held.Count} sessão(ões) anormal(is) retida(s)" : ""), "Fg", 12, new Thickness(0, 2, 0, 0)));
                if (!b.Established) continue;
                var facts = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
                void Fact(string label, Func<SessionSummary, double?> metric, string format, string unit)
                {
                    if (b.Stat(metric) is var (center, spread)) facts.Children.Add(IntelFact(label, center.ToString(format, IntelPt) + unit, $"± {spread.ToString(format, IntelPt)}"));
                }
                Fact("FPS MÉDIO", s => s.AvgFps, "0", "");
                Fact("1% LOW", s => s.Low1Fps, "0", "");
                Fact("FRAMETIME P99", s => s.P99Ms, "0.0", " ms");
                Fact("GPU (P95)", s => s.GpuTempP95, "0", " °C");
                Fact("CPU (P95)", s => s.CpuTempP95, "0", " °C");
                d.Add(facts);
            }

        d.Add(new TextBlock { Text = "ÚLTIMAS SESSÕES", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 16, 0, 6) });
        if (p == null || p.History.Count == 0) d.Add(IntelText("Nenhuma sessão analisada ainda.", "Muted", 13, default));
        else
            foreach (var s in p.History.Take(8))
                d.Add(IntelText($"{s.Start.ToString("dd/MM HH:mm", IntelPt)} · {s.Minutes.ToString("0", IntelPt)} min" +
                                (s.AvgFps is double fps ? $" · {fps.ToString("0", IntelPt)} FPS" : "") + (s.Low1Fps is double low ? $" · 1% low {low.ToString("0", IntelPt)}" : "") +
                                (s.Outlier ? " · fora do habitual" : "") + (s.Headline != null ? $" · {s.Headline}" : ""), "Fg", 12, new Thickness(0, 2, 0, 0)));

        var actions = intel.Store.Read(x => x.Journal.Where(j => j.GameKey == row.Key).Take(6).ToList());
        if (actions.Count > 0)
        {
            d.Add(new TextBlock { Text = "OTIMIZAÇÕES NESTE JOGO", Style = (Style)FindResource("Label"), Margin = new Thickness(0, 16, 0, 6) });
            foreach (var a in actions)
                d.Add(IntelText($"{a.At.ToString("dd/MM", IntelPt)} · {a.ActionName} · {(a.EvaluationText ?? (a.IsActive ? "aplicada, medindo o resultado nas próximas sessões" : a.Detail))}", "Fg", 12, new Thickness(0, 2, 0, 0)));
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(IntelButton("VER AS SESSÕES NA LINHA DO TEMPO", "Abre o Histórico, com frametime, temperaturas e clocks de cada sessão", primary: false, () => NavigateTo("PageHistory")));
        buttons.Children.Add(IntelButton("ABRIR A BIBLIOTECA", "Adicionar um jogo à mão ou corrigir o executável", primary: false, () => NavigateTo("PageLauncher")));
        if (p != null)
            buttons.Children.Add(IntelButton("APAGAR OS DADOS DESTE JOGO", "Apaga a referência e o histórico de inteligência deste jogo", primary: false, () =>
            {
                if (!ThgDialog.Confirm("Apagar os dados deste jogo", $"A referência e o histórico de inteligência de {row.Name} serão apagados. As gravações de sessão do Histórico não são afetadas.", "Apagar")) return;
                intel.Store.DeleteGame(row.Key);
                InitIntelGames();
            }));
        d.Add(buttons);
    }

    // ---------------------------------------------------------------- este PC

    private void InitIntelPc()
    {
        if (Intel is not { } intel) { IntelPcSummary.Text = "tHg Intelligence indisponível."; return; }
        RefreshIntelMachine();
        IntelPcFacts.Children.Clear();
        IntelPcSensors.Children.Clear();
        IntelPcGroups.Children.Clear();
        if (intel.Machine is not { } m)
            IntelPcSummary.Text = "O hardware ainda está sendo lido. Abra esta página de novo em alguns segundos.";
        else
        {
            IntelPcSummary.Text = m.HardwareId.Length > 0 ? m.HardwareId : "Hardware não identificado";
            var facts = new WrapPanel();
            facts.Children.Add(IntelFact("NÚCLEOS / THREADS", $"{m.PhysicalCores} / {m.LogicalProcessors}", m.HasSmt ? "com SMT" : "sem SMT"));
            facts.Children.Add(IntelFact("TOPOLOGIA", m.IsHybridCpu ? "híbrida" : m.HasAsymmetricCache ? "2 CCDs" : "uniforme", m.IsHybridCpu ? "P-cores e E-cores" : m.HasAsymmetricCache ? "cache diferente" : "núcleos iguais"));
            facts.Children.Add(IntelFact("TIPO", m.IsLaptop == true ? "notebook" : "desktop", m.OnBattery == true ? "na bateria" : "na tomada"));
            facts.Children.Add(IntelFact("TELA", m.Resolution ?? "—", m.Resolution != null ? "resolução @ Hz" : "indisponível"));
            facts.Children.Add(IntelFact("DRIVER DE VÍDEO", m.GpuDriver ?? "—", m.GpuDriver != null ? m.GpuVendor : "indisponível"));
            facts.Children.Add(IntelFact("GPUs", m.HasIntegratedAndDedicatedGpu ? "integrada + dedicada" : "uma", m.HasIntegratedAndDedicatedGpu ? "confira a preferência por jogo" : ""));
            IntelPcFacts.Children.Add(facts);
            foreach (var s in m.Sensors)
            {
                string tone = s.State == ReadingState.Supported ? "Good" : s.State == ReadingState.Unsupported ? "Faint" : "Warn";
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var pill = IntelPill(s.State switch { ReadingState.Supported => "disponível", ReadingState.Unsupported => "não suportado", ReadingState.Stale => "leitura antiga", _ => "indisponível" }, tone);
                DockPanel.SetDock(pill, Dock.Right);
                line.Children.Add(pill);
                line.Children.Add(IntelText(s.State == ReadingState.Supported ? $"{s.Name}: {s.Value}" : $"{s.Name}" + (s.Note.Length > 0 ? $" · {s.Note}" : ""), "Fg", 13, default));
                IntelPcSensors.Children.Add(line);
            }
            foreach (var g in m.Groups)
            {
                var exp = new Expander { Style = (Style)FindResource("ThgExpander"), Header = $"{g.Title} · {g.Items.Count} item(ns)", Margin = new Thickness(0, 0, 0, 4) };
                var inner = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
                foreach (var c in g.Items) inner.Children.Add(IntelText($"{c.Name}: {c.Display}", c.State == ReadingState.Supported ? "Fg" : "Faint", 12, new Thickness(0, 1, 0, 1)));
                exp.Content = inner;
                IntelPcGroups.Children.Add(exp);
            }
        }
        UpdateIntelDataText();
        int days = _settings.IntelligenceRetentionDays;
        IntelKeep90.IsChecked = days <= 90;
        IntelKeep180.IsChecked = days > 90 && days <= 180;
        IntelKeep365.IsChecked = days > 180;
        IntelNetProbe.IsChecked = _settings.IntelligenceNetProbe;
    }

    private void UpdateIntelDataText()
    {
        if (_intel is not { } intel) return;
        var (games, history, journal) = intel.Store.Read(d => (d.Games.Count, d.History.Count, d.Journal.Count));
        IntelDataText.Text = $"Perfis, referências e histórico ficam em {intel.Store.FilePath} ({intel.Store.SizeBytes / 1024.0:0} KB): {games} jogo(s), {history} diagnóstico(s), {journal} ação(ões). " +
                             "Nada é enviado para fora deste PC. O inventário não guarda números de série nem o nome do computador.";
    }

    private void BtnIntelExport_Click(object sender, RoutedEventArgs e) => IntelSafe(() =>
    {
        if (_intel is not { } intel) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = $"thg-intelligence-{DateTime.Now:yyyyMMdd}.json", Title = "Exportar os dados do tHg Intelligence" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            intel.Store.Export(dlg.FileName);
            Toast("tHg Intelligence", "Dados exportados para " + dlg.FileName, "");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Toast("tHg Intelligence", "Não foi possível exportar: " + Err(ex), ""); }
    });

    private void BtnIntelImport_Click(object sender, RoutedEventArgs e) => IntelSafe(() =>
    {
        if (_intel is not { } intel) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json", Title = "Importar dados do tHg Intelligence" };
        if (dlg.ShowDialog(this) != true) return;
        var (ok, message) = intel.Store.Import(dlg.FileName);
        Toast("tHg Intelligence", ok ? message + " O arquivo é tratado só como dados: nenhuma ação é aplicada pela importação." : message, "");
        UpdateIntelDataText();
    });

    private void BtnIntelDelete_Click(object sender, RoutedEventArgs e) => IntelSafe(() =>
    {
        if (_intel is not { } intel) return;
        if (!ThgDialog.Confirm("Apagar os dados do tHg Intelligence",
                "Perfis de jogos, referências e histórico de diagnósticos serão apagados. Ações ainda ativas continuam no histórico, para poderem ser desfeitas. " +
                "As gravações de sessão do Histórico e as configurações do app não são afetadas.", "Apagar tudo", icon: MessageBoxImage.Warning)) return;
        intel.Store.DeleteAll();
        _intelReport = null;
        _intelRecs = Array.Empty<Recommendation>();
        _dashIntelShown = "";
        UpdateIntelDataText();
        Toast("tHg Intelligence", "Dados apagados. O aprendizado recomeça do zero.", "");
    });

    private void IntelKeep_Click(object sender, RoutedEventArgs e) => IntelSafe(() =>
    {
        if (sender is not FrameworkElement { Tag: string days } || !int.TryParse(days, out int n)) return;
        _settings.IntelligenceRetentionDays = n;
        _settings.Save();
        int removed = _intel?.Store.Prune(TimeSpan.FromDays(n), DateTime.Now) ?? 0;
        UpdateIntelDataText();
        if (removed > 0) Toast("tHg Intelligence", $"{removed} registro(s) mais antigo(s) que {n} dias foram removidos.", "");
    });

    private void IntelNetProbe_Click(object sender, RoutedEventArgs e) => IntelSafe(() =>
    {
        _settings.IntelligenceNetProbe = IntelNetProbe.IsChecked == true;
        _settings.Save();
    });

    // ---------------------------------------------------------------- peças visuais

    private TextBlock IntelText(string text, string brush, double size, Thickness margin)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private Border IntelPill(string text, string tone)
    {
        var label = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, tone);
        var pill = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top, Child = label };
        if (tone is "Good" or "Warn" or "Bad") pill.SetResourceReference(Border.BackgroundProperty, tone + "Soft");
        else pill.SetResourceReference(Border.BackgroundProperty, "Hover");
        return pill;
    }

    private StackPanel IntelFact(string label, string value, string note)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 28, 8), MinWidth = 96 };
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)FindResource("Mono") });
        if (note.Length > 0) panel.Children.Add(IntelText(note, "Faint", 11, default));
        System.Windows.Automation.AutomationProperties.SetName(panel, $"{label}: {value} ({note})");
        return panel;
    }

    private StackPanel IntelList(string title, IReadOnlyList<string> lines, string brush)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 0, 0, 4) });
        foreach (string l in lines) panel.Children.Add(IntelText((brush == "Good" ? "✓ " : "• ") + l, brush == "Good" ? "Fg" : brush, 12, new Thickness(0, 2, 0, 0)));
        return panel;
    }

    /// <summary>Botão com ação protegida: uma exceção vira aviso, nunca sobe ao Dispatcher.</summary>
    private Button IntelButton(string text, string tip, bool primary, Action onClick)
    {
        var b = new Button { Content = text, ToolTip = tip, Style = (Style)FindResource(primary ? "PrimaryButton" : "OutlineButton"), Padding = new Thickness(16, 6, 16, 6),
                             FontSize = 11, Margin = new Thickness(0, 0, 8, 4), HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(b, text);
        b.Click += (_, _) =>
        {
            try { onClick(); }
            catch (Exception ex)
            {
                Infrastructure.AppLog.Error("intelligence", ex);
                Toast("tHg Intelligence", "A operação falhou: " + Err(ex), "");
            }
        };
        return b;
    }
}
