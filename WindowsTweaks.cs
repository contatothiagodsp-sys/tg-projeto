using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ThgOptimization;

/// <summary>
/// Ajuste do Windows para jogos: lê o estado atual, aplica e guarda o valor original (em AppSettings.TweakBackup)
/// na primeira vez que é alterado, para "Restaurar originais" desfazer tudo.
/// Só ajustes documentados e reversíveis; nada que reduza a segurança (Defender, VBS/HVCI, UAC, Windows Update).
/// </summary>
public sealed class Tweak : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Effect { get; init; } = "";
    public bool NeedsRestart { get; init; }
    /// <summary>Gasta mais energia: o TURBO não aplica sozinho em notebooks.</summary>
    public bool LaptopCaution { get; init; }
    /// <summary>Peso no TURBO (0 = só informativo, 10 = grande impacto em jogos).</summary>
    public int Weight { get; init; } = 3;
    /// <summary>Existe neste Windows (ex.: recursos só do Windows 11).</summary>
    internal Func<bool> Available { get; init; } = () => true;
    /// <summary>Motivo para NÃO aplicar neste PC (ex.: Ryzen X3D com dois CCDs), ou null.</summary>
    internal Func<string, string?> AvoidFor { get; init; } = _ => null;
    internal Func<bool?> Read { get; init; } = () => null;
    internal Action<bool, AppSettings> Write { get; init; } = (_, _) => { };
    internal Action<AppSettings> Restore { get; init; } = _ => { };

    private bool _isOn;
    public bool IsOn { get => _isOn; set { _isOn = value; Changed(nameof(IsOn)); } }
    private string _status = "";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public string Badge => NeedsRestart ? "REQUER REINICIAR" : "";

    public void Refresh() => IsOn = Read() ?? false;

    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static partial class WindowsTweaks
{
    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    // Subgrupos e configurações do plano de energia (GUIDs documentados pelo powercfg)
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string SubPcie = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string PcieAspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string CoreParkingMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";

    private const string MmcssPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    /// <summary>
    /// Ryzen X3D com dois CCDs (7900X3D, 7950X3D, 9900X3D, 9950X3D): o driver da AMD estaciona o CCD sem 3D V-Cache
    /// durante o jogo. Alto desempenho e núcleos sempre acordados atrapalham isso; a AMD recomenda o plano Equilibrado.
    /// </summary>
    public static bool IsDualCcdX3D(string cpu) =>
        System.Text.RegularExpressions.Regex.IsMatch(cpu ?? "", @"\b(7900|7950|9900|9950)X3D\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private const string X3DReason = "Ryzen X3D com dois CCDs: a AMD recomenda o plano Equilibrado para o driver mandar o jogo ao CCD com 3D V-Cache.";

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Todos os ajustes disponíveis neste Windows, na ordem em que o TURBO aplica (plano antes das opções do plano).</summary>
    public static List<Tweak> All() => Definitions().Where(t => { try { return t.Available(); } catch { return false; } }).ToList();

    private static List<Tweak> Definitions() => new()
    {
        new Tweak
        {
            Id = "power", Title = "Plano de energia para jogos", Weight = 6, LaptopCaution = true,
            AvoidFor = cpu => IsDualCcdX3D(cpu) ? X3DReason : null,
            Description = "Desktop: Desempenho Máximo (Ultimate Performance), o plano que tira até as micro-economias de energia. " +
                          "Notebook: Alto desempenho. A CPU fica pronta para subir o clock na hora.",
            Effect = "Menos engasgos e 1% low melhor, principalmente em jogos que dependem da CPU. Gasta um pouco mais de energia em repouso.",
            Read = () => PowerPlan.IsGamingPlan(ActiveScheme()),
            Write = (on, s) =>
            {
                if (!s.TweakBackup.ContainsKey("power")) s.TweakBackup["power"] = ActiveScheme();
                if (on)
                {
                    string err = PowerPlan.ActivateBest();
                    if (err.Length > 0) throw new InvalidOperationException(err);
                }
                else if (s.TweakBackup["power"] is string prev && !PowerPlan.IsGamingPlan(prev))
                    PowerCfg($"/setactive {SafeGuid(prev)}");
                else PowerCfg($"/setactive {PowerPlan.Balanced}");
            },
            Restore = s => { if (SafeGuid(s.TweakBackup.GetValueOrDefault("power")) is string prev) PowerCfg($"/setactive {prev}"); },
        },
        RegTweak("gamemode", "Modo de Jogo",
            "Dá prioridade ao jogo e evita que o Windows Update instale ou reinicie durante a partida.",
            "Recomendado ligado (é o padrão do Windows 11).",
            Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", on: 1, off: 0, absentMeansOn: true, weight: 5),
        new Tweak
        {
            Id = "gamedvr", Weight = 6, Title = "Desativar gravação em segundo plano (Game DVR)",
            Description = "Desliga a captura contínua do Xbox Game Bar, que grava os últimos minutos de jogo o tempo todo.",
            Effect = "Recupera alguns FPS e reduz uso de GPU/disco. Você perde o \"gravar o que aconteceu\" do Game Bar.",
            Read = () => (ReadDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") ?? 1) == 0 &&
                         (ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled") ?? 1) == 0,
            Write = (on, s) =>
            {
                SetDword(s, "gamedvr.a", Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", on ? 0 : 1);
                SetDword(s, "gamedvr.b", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", on ? 0 : 1);
            },
            Restore = s =>
            {
                RestoreDword(s, "gamedvr.a", Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled");
                RestoreDword(s, "gamedvr.b", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
            },
        },
        RegTweak("hags", "Agendamento de GPU acelerado por hardware",
            "A própria placa de vídeo gerencia sua fila de trabalho, em vez da CPU.",
            "Necessário para o Frame Generation do DLSS 3/4. Pode reduzir latência. Requer GPU e driver compatíveis.",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", on: 2, off: 1,
            absentMeansOn: false, needsRestart: true, weight: 5),
        new Tweak
        {
            Id = "windowed", Weight = 4, Available = () => IsWindows11, Title = "Otimizações para jogos em janela",
            Description = "Faz jogos DirectX 10/11 em janela ou \"sem bordas\" usarem o modelo de apresentação moderno (flip).",
            Effect = "Menor latência e suporte a VRR/G-Sync em janela. Recurso do Windows 11.",
            Read = () => DxSettings().GetValueOrDefault("SwapEffectUpgradeEnable") == "1",
            Write = (on, s) =>
            {
                if (!s.TweakBackup.ContainsKey("windowed"))
                    s.TweakBackup["windowed"] = Registry.CurrentUser.OpenSubKey(DxPath)?.GetValue("DirectXUserGlobalSettings") as string ?? "<ausente>";
                var d = DxSettings();
                d["SwapEffectUpgradeEnable"] = on ? "1" : "0";
                using var k = Registry.CurrentUser.CreateSubKey(DxPath);
                k.SetValue("DirectXUserGlobalSettings", string.Concat(d.Select(kv => $"{kv.Key}={kv.Value};")));
            },
            Restore = s =>
            {
                if (s.TweakBackup.GetValueOrDefault("windowed") is not string prev) return;
                using var k = Registry.CurrentUser.CreateSubKey(DxPath);
                if (prev == "<ausente>") k.DeleteValue("DirectXUserGlobalSettings", false);
                else k.SetValue("DirectXUserGlobalSettings", prev);
            },
        },
        new Tweak
        {
            Id = "mouse", Weight = 3, Title = "Desligar aceleração do mouse",
            Description = "Desmarca \"Aprimorar precisão do ponteiro\": o cursor anda sempre a mesma distância para o mesmo movimento.",
            Effect = "Não muda FPS, mas deixa a mira consistente em jogos de tiro.",
            Read = () => ReadString(Registry.CurrentUser, @"Control Panel\Mouse", "MouseSpeed") == "0",
            Write = (on, s) =>
            {
                foreach (var n in new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" })
                    if (!s.TweakBackup.ContainsKey($"mouse.{n}")) s.TweakBackup[$"mouse.{n}"] = ReadString(Registry.CurrentUser, @"Control Panel\Mouse", n);
                var v = on ? new[] { "0", "0", "0" } : new[] { "1", "6", "10" }; // padrão do Windows
                ApplyMouse(v);
            },
            Restore = s =>
            {
                if (!s.TweakBackup.ContainsKey("mouse.MouseSpeed")) return;
                ApplyMouse(new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" }
                    .Select(n => s.TweakBackup.GetValueOrDefault($"mouse.{n}") ?? "0").ToArray());
            },
        },
        // ---------------- v0.17 · TURBO ----------------

        PowerSettingTweak("usbsuspend", "Desligar suspensão seletiva de USB",
            "Impede o Windows de pôr portas USB para dormir para economizar energia (vale para o plano de energia ativo).",
            "Evita mouse, teclado ou headset sem fio \"travando\" ou sumindo por um instante no meio da partida.",
            SubUsb, UsbSelectiveSuspend, on: 0, weight: 3),
        PowerSettingTweak("pcieaspm", "Desligar economia de energia do PCI Express",
            "Desliga o Link State Power Management: a placa de vídeo e o SSD NVMe não entram em economia no barramento.",
            "Tira pequenas latências ao sair do repouso (engasgos curtos em alguns PCs). Gasta um pouco mais em repouso.",
            SubPcie, PcieAspm, on: 0, weight: 3, laptopCaution: true),
        PowerSettingTweak("coreparking", "Núcleos sempre acordados (sem core parking)",
            "Mantém todos os núcleos da CPU disponíveis no plano de energia ativo, em vez de \"estacionar\" os ociosos.",
            "Ajuda o 1% low em jogos que acordam e dormem threads o tempo todo. Mais consumo em repouso.",
            SubProcessor, CoreParkingMinCores, on: 100, weight: 3, laptopCaution: true,
            avoidFor: cpu => IsDualCcdX3D(cpu) ? X3DReason + " O core parking é justamente o que faz isso funcionar." : null),
        MultiRegTweak("mmcss", "Prioridade multimídia para jogos (MMCSS)",
            "Diz ao agendador multimídia do Windows para dar mais tempo de CPU e prioridade de E/S às tarefas marcadas como jogo.",
            "Efeito pequeno, mas sem custo: ajuda jogos e áudio que usam o MMCSS a não perderem a vez para tarefas de fundo.",
            weight: 2, needsRestart: true, values: new[]
            {
                new RegValue(Registry.LocalMachine, MmcssPath, "SystemResponsiveness", 10, 20),
                new RegValue(Registry.LocalMachine, MmcssPath + @"\Tasks\Games", "GPU Priority", 8, 8),
                new RegValue(Registry.LocalMachine, MmcssPath + @"\Tasks\Games", "Priority", 6, 2),
                new RegValue(Registry.LocalMachine, MmcssPath + @"\Tasks\Games", "Scheduling Category", "High", "Medium"),
                new RegValue(Registry.LocalMachine, MmcssPath + @"\Tasks\Games", "SFIO Priority", "High", "Normal"),
            }),
        MultiRegTweak("netthrottle", "Sem limite de rede durante áudio e vídeo",
            "Desliga o NetworkThrottlingIndex, que limita pacotes de rede enquanto algo multimídia toca.",
            "Útil para quem joga com Discord, música ou live aberta. Não aumenta a velocidade da internet.",
            weight: 2, needsRestart: true, values: new[]
            {
                new RegValue(Registry.LocalMachine, MmcssPath, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), 10),
            }),
        MultiRegTweak("timerres", "Resolução de timer pedida pelo jogo vale sempre",
            "No Windows 11, um jogo minimizado ou atrás de outra janela perde a resolução de timer que pediu. Isso mantém o pedido.",
            "Frametime mais estável com o jogo em segundo monitor ou com o overlay/Discord por cima. Só Windows 11.",
            weight: 2, needsRestart: true, available: () => IsWindows11, values: new[]
            {
                new RegValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel",
                             "GlobalTimerResolutionRequests", 1, null),
            }),
        MultiRegTweak("powerthrottling", "Desligar o limitador de energia de processos",
            "Desliga o Power Throttling (EcoQoS), que joga processos em segundo plano para os núcleos mais lentos e econômicos.",
            "Evita o jogo, o Discord ou o OBS serem rebaixados quando a janela perde o foco. Mais consumo em notebooks.",
            weight: 2, needsRestart: true, laptopCaution: true, values: new[]
            {
                new RegValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, null),
            }),
        new Tweak
        {
            Id = "vrrwindowed", Weight = 3, Available = () => Environment.OSVersion.Version.Build >= 18362,
            Title = "Taxa de atualização variável em janela (VRR)",
            Description = "Liga a opção do Windows que estende o G-SYNC / FreeSync a jogos DirectX 11 em janela ou sem bordas que não têm suporte próprio.",
            Effect = "Sem tearing e sem o atraso do V-Sync nesses jogos, com monitor compatível. Sem monitor VRR, não muda nada.",
            Read = () => DxSettings().GetValueOrDefault("VRROptimizeEnable") == "1",
            Write = (on, s) => SetDxFlag(s, "vrrwindowed", "VRROptimizeEnable", on),
            Restore = s => RestoreDx(s, "vrrwindowed"),
        },
        GpuMsiTweak(),
        IoMsiTweak(),
        MultiRegTweak("mpo", "Desligar o MPO (Multiplane Overlay)",
            "Desliga as camadas de sobreposição da tela (MPO) do Windows, conhecida causa de piscadas e microengasgos em alguns PCs com monitores de taxas diferentes ou com G-SYNC/FreeSync.",
            "Só vale a pena se você tem tela piscando ou engasgos em janela sem borda. Nas versões mais novas do Windows 11 a Microsoft pode ignorar este ajuste. Precisa reiniciar.",
            weight: 0, needsRestart: true, values: new[]
            {
                new RegValue(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, null),
            }),
        MultiRegTweak("deliveryopt", "Windows Update sem enviar para outros PCs",
            "Desliga a Otimização de Entrega em modo ponto a ponto: o Windows para de usar a sua internet para mandar atualizações a outros computadores.",
            "Evita picos de upload que aumentam o ping no meio da partida. As atualizações continuam chegando normalmente pela Microsoft.",
            weight: 2, values: new[]
            {
                new RegValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, null),
            }),
        MultiRegTweak("stickykeys", "Desligar atalhos de teclas de aderência",
            "Tira o aviso que aparece ao apertar Shift 5 vezes (ou segurar Shift / Num Lock), que minimiza o jogo.",
            "Não muda FPS: acaba com a janela de \"Teclas de Aderência\" no meio da partida. Vale a partir do próximo login.",
            weight: 1, values: new[]
            {
                new RegValue(Registry.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", "506", "510"),
                new RegValue(Registry.CurrentUser, @"Control Panel\Accessibility\Keyboard Response", "Flags", "122", "126"),
                new RegValue(Registry.CurrentUser, @"Control Panel\Accessibility\ToggleKeys", "Flags", "58", "62"),
            }),
    };

    // ------------------------------------------------------------------ Limpeza do Windows (página própria)

    /// <summary>
    /// Limpeza do Windows no estilo do WinUtil, só com itens reversíveis por política do registro: o valor de antes fica
    /// guardado (prefixo "clean.") e "Desfazer" volta tudo. Nada aqui desliga segurança ou atualizações.
    /// </summary>
    public static List<Tweak> CleanupTweaks()
    {
        const string CDM = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        var list = new List<Tweak>
        {
            MultiRegTweak("clean.copilot", "Desligar o Copilot do Windows",
                "Tira o Copilot da barra de tarefas e o desliga no Windows (o Copilot no navegador continua disponível).",
                "Menos um processo em segundo plano e menos atalhos acidentais durante o jogo.", weight: 1, values: new[]
                {
                    new RegValue(Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, null),
                    new RegValue(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0, null, Optional: true),
                }),
            MultiRegTweak("clean.recall", "Desligar o Recall (capturas de tela da IA)",
                "Impede o Windows de tirar e guardar capturas da tela para a busca por IA (Copilot+ PCs com Windows 11 24H2 ou mais novo).",
                "Privacidade (senhas e conversas não ficam em capturas) e menos uso de disco e CPU.", weight: 1, values: new[]
                {
                    new RegValue(Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, null),
                    new RegValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, null),
                }),
            WidgetsTweak(),
            MultiRegTweak("clean.telemetry", "Telemetria do Windows no mínimo",
                "Envia à Microsoft só os dados obrigatórios (no Windows Home o mínimo já é o \"obrigatório\").",
                "Menos uso de disco, CPU e rede em segundo plano. Não afeta atualizações.", weight: 1, values: new[]
                {
                    new RegValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, null),
                }),
            ServiceTweak("clean.diagtrack", "DiagTrack", "Desligar o serviço de telemetria (DiagTrack)",
                "Para o serviço \"Experiências do Usuário Conectado e Telemetria\", que coleta e envia dados de uso.",
                "Um serviço a menos rodando; o Windows funciona normalmente sem ele."),
            MultiRegTweak("clean.ads", "Sem anúncios e sugestões do Windows",
                "Desliga apps instalados sozinhos, sugestões no Iniciar e nas Configurações, dicas e o ID de publicidade.",
                "Evita que o Windows instale jogos e apps de propaganda e use a internet para isso.", weight: 1, values: new[]
                {
                    new RegValue(Registry.CurrentUser, CDM, "SilentInstalledAppsEnabled", 0, null),
                    new RegValue(Registry.CurrentUser, CDM, "SystemPaneSuggestionsEnabled", 0, null),
                    new RegValue(Registry.CurrentUser, CDM, "SoftLandingEnabled", 0, null),
                    new RegValue(Registry.CurrentUser, CDM, "SubscribedContent-338388Enabled", 0, null),
                    new RegValue(Registry.CurrentUser, CDM, "SubscribedContent-338389Enabled", 0, null),
                    new RegValue(Registry.CurrentUser, CDM, "SubscribedContent-353694Enabled", 0, null),
                    new RegValue(Registry.CurrentUser, CDM, "SubscribedContent-353696Enabled", 0, null),
                    new RegValue(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, null),
                }),
            MultiRegTweak("clean.websearch", "Busca do Iniciar sem resultados da internet",
                "A busca do menu Iniciar procura só no PC, sem mandar o que você digita para o Bing.",
                "Busca mais rápida e privada.", weight: 1, values: new[]
                {
                    new RegValue(Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null),
                }),
            MultiRegTweak("clean.edge", "Edge sem rodar escondido",
                "Desliga o \"Aumento de desempenho na inicialização\" e o \"Continuar executando em segundo plano\" do Microsoft Edge.",
                "O Edge deixa de ficar aberto escondido usando memória enquanto você joga. Ele abre um pouco mais devagar.", weight: 1, values: new[]
                {
                    new RegValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0, null),
                    new RegValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0, null),
                }),
            MultiRegTweak("clean.bgapps", "Apps da Microsoft Store sem rodar em segundo plano",
                "Apps da Store (Clima, Fotos, Telefone…) só rodam quando abertos.",
                "Menos processos de fundo. Cuidado: apps da Store que você usa para notificações (WhatsApp da Store, por exemplo) deixam de avisar com a janela fechada.",
                weight: 0, values: new[]
                {
                    new RegValue(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2, null),
                }),
        };
        return list;
    }

    private const string DshPath = @"SOFTWARE\Policies\Microsoft\Dsh", FeedsPath = @"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds";

    /// <summary>App dos Widgets (Windows Web Experience Pack) registrado para este usuário? Leitura rápida pelo registro.</summary>
    private static bool WidgetsAppInstalled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        return k?.GetSubKeyNames().Any(n => n.StartsWith("MicrosoftWindows.Client.WebExperience_", StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>
    /// Widgets: primeiro pela política do Windows (como no Editor de Política de Grupo). Nas versões novas do Windows 11 a
    /// chave dessa política é protegida; aí o app dos Widgets (Windows Web Experience Pack) é removido desta conta, como faz
    /// o WinUtil. Desfazer devolve a política e reinstala o app (da cópia do Windows ou da Microsoft Store).
    /// </summary>
    private static Tweak WidgetsTweak() => new()
    {
        Id = "clean.widgets", Weight = 1,
        Title = "Desligar os Widgets e as notícias da barra de tarefas",
        Description = "Remove o painel de Widgets (Windows 11) e o de notícias e clima (Windows 10).",
        Effect = "O painel fica carregando notícias em segundo plano com um processo do Edge: sai memória e rede. O botão some da barra ao entrar de novo no Windows.",
        Read = () => ReadDword(Registry.LocalMachine, DshPath, "AllowNewsAndInterests") == 0
                     || ReadDword(Registry.LocalMachine, FeedsPath, "EnableFeeds") == 0 && !IsWindows11
                     || IsWindows11 && !WidgetsAppInstalled(),
        Write = (on, s) =>
        {
            if (!on) { RestoreWidgets(s); return; }
            bool policy = false;
            foreach (var (path, name) in new[] { (DshPath, "AllowNewsAndInterests"), (FeedsPath, "EnableFeeds") })
            {
                try
                {
                    string bk = $"clean.widgets.{name}";
                    if (!s.TweakBackup.ContainsKey(bk)) s.TweakBackup[bk] = EncodeAny(ReadAny(Registry.LocalMachine, path, name));
                    WriteAny(Registry.LocalMachine, path, name, 0);
                    if (path == DshPath) policy = true;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    s.TweakBackup.Remove($"clean.widgets.{name}");
                }
            }
            if (!IsWindows11 || policy || !WidgetsAppInstalled()) return;
            // Política bloqueada: remove o app dos Widgets desta conta.
            s.TweakBackup["clean.widgets.appx"] = "removed";
            string r = SecurityAudit.Ps("Get-Process Widgets, WidgetService -ErrorAction SilentlyContinue | Stop-Process -Force; " +
                                        "Get-AppxPackage *WebExperience* | Remove-AppxPackage -ErrorAction Stop; 'ok'", 120_000, returnOutput: true);
            if (!r.Contains("ok") && WidgetsAppInstalled())
            {
                s.TweakBackup.Remove("clean.widgets.appx");
                throw new InvalidOperationException("o Windows bloqueou a política e a remoção do app dos Widgets. " + r.Trim());
            }
        },
        Restore = RestoreWidgets,
    };

    private static void RestoreWidgets(AppSettings s)
    {
        foreach (var (path, name) in new[] { (DshPath, "AllowNewsAndInterests"), (FeedsPath, "EnableFeeds") })
            if (s.TweakBackup.TryGetValue($"clean.widgets.{name}", out var prev))
                try { WriteAny(Registry.LocalMachine, path, name, DecodeAny(prev)); s.TweakBackup.Remove($"clean.widgets.{name}"); } catch { }
        if (s.TweakBackup.Remove("clean.widgets.appx") && !WidgetsAppInstalled())
        {
            SecurityAudit.Ps("$p = Get-AppxPackage -AllUsers *WebExperience* | Where-Object InstallLocation | Select-Object -First 1; " +
                             "if ($p) { Add-AppxPackage -Register (Join-Path $p.InstallLocation 'AppXManifest.xml') -DisableDevelopmentMode }", 180_000);
            string winget = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe");
            if (!WidgetsAppInstalled() && System.IO.File.Exists(winget))
                SecurityAudit.Run(winget,
                                  "install --id 9MSSGKG348SP -s msstore --silent --accept-package-agreements --accept-source-agreements", 300_000);
        }
    }

    /// <summary>Serviço do Windows desativado (e parado); desfazer volta o tipo de início anterior.</summary>
    private static Tweak ServiceTweak(string id, string service, string title, string desc, string effect) => new()
    {
        Id = id, Title = title, Description = desc, Effect = effect, Weight = 1,
        Read = () => ReadDword(Registry.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{service}", "Start") == 4,
        Write = (on, s) =>
        {
            string path = $@"SYSTEM\CurrentControlSet\Services\{service}";
            if (!s.TweakBackup.ContainsKey(id)) s.TweakBackup[id] = ReadDword(Registry.LocalMachine, path, "Start")?.ToString() ?? "2";
            if (on)
            {
                SecurityAudit.Run("sc.exe", $"stop {service}", 20_000);
                SecurityAudit.Run("sc.exe", $"config {service} start= disabled", 20_000);
            }
            else
            {
                string prev = s.TweakBackup[id] ?? "2";
                SecurityAudit.Run("sc.exe", $"config {service} start= {(prev == "3" ? "demand" : prev == "4" ? "auto" : "auto")}", 20_000);
                SecurityAudit.Run("sc.exe", $"start {service}", 20_000);
            }
        },
        Restore = s =>
        {
            if (s.TweakBackup.GetValueOrDefault(id) is not string prev) return;
            SecurityAudit.Run("sc.exe", $"config {service} start= {(prev == "3" ? "demand" : prev == "4" ? "disabled" : "auto")}", 20_000);
            if (prev != "4") SecurityAudit.Run("sc.exe", $"start {service}", 20_000);
        },
    };

    /// <summary>Volta tudo que a página Limpeza do Windows alterou.</summary>
    public static void RestoreCleanup(IEnumerable<Tweak> tweaks, AppSettings s)
    {
        foreach (var t in tweaks.Reverse()) try { t.Restore(s); } catch { }
        foreach (var k in s.TweakBackup.Keys.Where(k => k.StartsWith("clean.")).ToList()) s.TweakBackup.Remove(k);
        s.Save();
    }

    /// <summary>Desfaz tudo que foi alterado pelo app (valores guardados antes da primeira alteração).</summary>
    public static void RestoreAll(IEnumerable<Tweak> tweaks, AppSettings s)
    {
        // Ordem inversa: as opções do plano de energia voltam antes de o plano original ser reativado.
        foreach (var t in tweaks.Reverse())
            try { t.Restore(s); } catch { }
        try { DisplayTuner.RestoreAll(s); } catch { }
        // Cópias de outras páginas ficam: SHIELD ("sec."), perfis do driver ("nv.") e DDU ("ddu.") têm o próprio desfazer.
        foreach (var key in s.TweakBackup.Keys.Where(k => !IsOtherPageKey(k)).ToList()) s.TweakBackup.Remove(key);
        s.Save();
    }

    public static bool IsOtherPageKey(string k) => k.StartsWith("sec.") || k.StartsWith("nv.") || k.StartsWith("ddu.") || k.StartsWith("net.") || k.StartsWith("clean.") || k.StartsWith("oled.") || k.StartsWith("comp.");

    // ------------------------------------------------------------------ registro

    private static Tweak RegTweak(string id, string title, string desc, string effect, RegistryKey hive, string path, string name,
                                  int on, int off, bool absentMeansOn, bool needsRestart = false, int weight = 3) => new()
    {
        Id = id, Title = title, Description = desc, Effect = effect, NeedsRestart = needsRestart, Weight = weight,
        Read = () => (ReadDword(hive, path, name) is int v) ? v == on : absentMeansOn,
        Write = (enable, s) => SetDword(s, id, hive, path, name, enable ? on : off),
        Restore = s => RestoreDword(s, id, hive, path, name),
    };

    /// <summary>Um valor do registro: o que o ajuste grava (On) e o padrão do Windows (Default; null = o valor não existe).</summary>
    /// <summary>Optional: valor de conforto (ex.: botão da barra); se o Windows proteger a chave, o ajuste vale mesmo sem ele.</summary>
    private sealed record RegValue(RegistryKey Hive, string Path, string Name, object On, object? Default, bool Optional = false)
    {
        public string Key => $"{Name}@{Path}";
    }

    /// <summary>Ajuste com vários valores do registro (DWORD ou texto). Ligado = todos com o valor On.</summary>
    private static Tweak MultiRegTweak(string id, string title, string desc, string effect, RegValue[] values, int weight = 3,
                                       bool needsRestart = false, bool laptopCaution = false, Func<bool>? available = null) => new()
    {
        Id = id, Title = title, Description = desc, Effect = effect, NeedsRestart = needsRestart, Weight = weight,
        LaptopCaution = laptopCaution, Available = available ?? (() => true),
        Read = () => values.Where(v => !v.Optional).All(v => Equals(ReadAny(v.Hive, v.Path, v.Name), v.On)),
        Write = (on, s) =>
        {
            foreach (var v in values)
            {
                string bk = $"{id}.{v.Key}";
                try
                {
                    if (!s.TweakBackup.ContainsKey(bk)) s.TweakBackup[bk] = EncodeAny(ReadAny(v.Hive, v.Path, v.Name));
                    // Desligar: volta ao valor de antes do app; sem cópia, ao padrão do Windows.
                    object? target = on ? v.On : (DecodeAny(s.TweakBackup[bk]) is { } prev && !Equals(prev, v.On) ? prev : v.Default);
                    WriteAny(v.Hive, v.Path, v.Name, target);
                }
                catch (Exception ex) when (v.Optional && ex is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    s.TweakBackup.Remove(bk); // chave protegida pelo Windows: segue sem ela
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    throw new UnauthorizedAccessException($"o Windows protege a chave {v.Path}\\{v.Name} contra alterações de programas.");
                }
            }
        },
        Restore = s =>
        {
            foreach (var v in values)
                if (s.TweakBackup.TryGetValue($"{id}.{v.Key}", out var prev))
                    try { WriteAny(v.Hive, v.Path, v.Name, DecodeAny(prev)); } catch when (v.Optional) { }
        },
    };

    private static object? ReadAny(RegistryKey hive, string path, string name)
    {
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(name) switch { int i => i, string str => str, null => null, var o => o.ToString() };
    }

    private static void WriteAny(RegistryKey hive, string path, string name, object? value)
    {
        using var k = hive.CreateSubKey(path);
        switch (value)
        {
            case null: k.DeleteValue(name, false); break;
            case int i: k.SetValue(name, i, RegistryValueKind.DWord); break;
            default: k.SetValue(name, value.ToString()!, RegistryValueKind.String); break;
        }
    }

    private static string EncodeAny(object? v) => v switch { null => "<ausente>", int i => $"d:{i}", _ => $"s:{v}" };

    private static object? DecodeAny(string? s) => s switch
    {
        null or "<ausente>" => null,
        _ when s.StartsWith("d:") => int.Parse(s[2..]),
        _ when s.StartsWith("s:") => s[2..],
        _ => s,
    };

    // ------------------------------------------------------------------ opções do plano de energia

    /// <summary>
    /// Ajuste de uma opção do plano de energia ativo (valor "na tomada"). A cópia guarda o GUID do plano junto, para
    /// restaurar no plano certo mesmo que outro esteja ativo depois.
    /// </summary>
    private static Tweak PowerSettingTweak(string id, string title, string desc, string effect, string sub, string setting, int on,
                                           int weight = 3, bool laptopCaution = false, Func<string, string?>? avoidFor = null) => new()
    {
        Id = id, Title = title, Description = desc, Effect = effect, Weight = weight, LaptopCaution = laptopCaution,
        AvoidFor = avoidFor ?? (_ => null),
        Read = () => AcIndex("scheme_current", sub, setting) is int v ? v == on : null,
        Write = (enable, s) =>
        {
            string scheme = ActiveScheme() ?? "scheme_current";
            string bk = $"{id}.{scheme}";
            if (!s.TweakBackup.ContainsKey(bk) && AcIndex(scheme, sub, setting) is int cur) s.TweakBackup[bk] = cur.ToString();
            int value = enable ? on : (s.TweakBackup.GetValueOrDefault(bk) is string prev && int.Parse(prev) != on ? int.Parse(prev) : DefaultOff(on));
            PowerCfg($"/setacvalueindex {scheme} {sub} {setting} {value}");
            PowerCfg("/setactive scheme_current"); // aplica na hora
        },
        Restore = s =>
        {
            foreach (var (key, prev) in s.TweakBackup.Where(kv => kv.Key.StartsWith(id + ".")).ToList())
            {
                if (prev is null) continue;
                if (SafeGuid(key[(id.Length + 1)..]) is not string scheme || !int.TryParse(prev, out int v)) continue; // valor alterado fora do app
                PowerCfg($"/setacvalueindex {scheme} {sub} {setting} {v}");
            }
            PowerCfg("/setactive scheme_current");
        },
    };

    /// <summary>Sem cópia guardada, "desligar" volta ao padrão do Windows: suspensão USB e ASPM ligados, core parking livre.</summary>
    private static int DefaultOff(int on) => on == 100 ? 0 : 1;

    /// <summary>
    /// Índice atual "na tomada" de uma opção. A saída do powercfg é traduzida, mas termina sempre com duas linhas
    /// 0x........: a penúltima é o valor na tomada (AC) e a última na bateria (DC).
    /// </summary>
    private static int? AcIndex(string scheme, string sub, string setting)
    {
        var hex = System.Text.RegularExpressions.Regex.Matches(PowerCfg($"/qh {scheme} {sub} {setting}"), @"0x([0-9a-fA-F]{8})");
        if (hex.Count < 2) return null;
        return Convert.ToInt32(hex[^2].Groups[1].Value, 16);
    }

    private static int? ReadDword(RegistryKey hive, string path, string name)
    {
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(name) is int v ? v : null;
    }

    private static string? ReadString(RegistryKey hive, string path, string name)
    {
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(name)?.ToString();
    }

    private static void SetDword(AppSettings s, string backupKey, RegistryKey hive, string path, string name, int value)
    {
        if (!s.TweakBackup.ContainsKey(backupKey))
            s.TweakBackup[backupKey] = ReadDword(hive, path, name)?.ToString() ?? "<ausente>";
        using var k = hive.CreateSubKey(path);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void RestoreDword(AppSettings s, string backupKey, RegistryKey hive, string path, string name)
    {
        if (s.TweakBackup.GetValueOrDefault(backupKey) is not string prev) return;
        using var k = hive.CreateSubKey(path);
        if (prev == "<ausente>") k.DeleteValue(name, false);
        else k.SetValue(name, int.Parse(prev), RegistryValueKind.DWord);
    }

    private const string DxPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>"Chave=Valor;Chave=Valor;" → dicionário (o valor pode ter outras opções do usuário, que preservamos).</summary>
    private static Dictionary<string, string> DxSettings()
    {
        var d = new Dictionary<string, string>();
        string raw = Registry.CurrentUser.OpenSubKey(DxPath)?.GetValue("DirectXUserGlobalSettings") as string ?? "";
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2) d[kv[0].Trim()] = kv[1].Trim();
        }
        return d;
    }

    private static void SetDxFlag(AppSettings s, string id, string flag, bool on)
    {
        if (!s.TweakBackup.ContainsKey(id))
            s.TweakBackup[id] = Registry.CurrentUser.OpenSubKey(DxPath)?.GetValue("DirectXUserGlobalSettings") as string ?? "<ausente>";
        var d = DxSettings();
        d[flag] = on ? "1" : "0";
        using var k = Registry.CurrentUser.CreateSubKey(DxPath);
        k.SetValue("DirectXUserGlobalSettings", string.Concat(d.Select(kv => $"{kv.Key}={kv.Value};")));
    }

    private static void RestoreDx(AppSettings s, string id)
    {
        if (s.TweakBackup.GetValueOrDefault(id) is not string prev) return;
        using var k = Registry.CurrentUser.CreateSubKey(DxPath);
        if (prev == "<ausente>") k.DeleteValue("DirectXUserGlobalSettings", false);
        else k.SetValue("DirectXUserGlobalSettings", prev);
    }

    // ------------------------------------------------------------------ interrupções da placa de vídeo (MSI)

    private static string MsiKey(string pnpId) =>
        $@"SYSTEM\CurrentControlSet\Enum\{pnpId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";

    /// <summary>Dispositivos PCI de uma classe do WMI (só PCI: USB e outros barramentos não têm modo MSI).</summary>
    private static List<string> PciKeys(string wql, string? vendorRegex = null)
    {
        var list = new List<string>();
        try
        {
            using var q = new System.Management.ManagementObjectSearcher(wql);
            foreach (System.Management.ManagementObject o in q.Get())
            {
                string id = o["PNPDeviceID"]?.ToString() ?? "";
                if (!id.StartsWith(@"PCI\VEN_", StringComparison.OrdinalIgnoreCase)) continue;
                if (vendorRegex != null && !System.Text.RegularExpressions.Regex.IsMatch(id, vendorRegex, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                string k = MsiKey(id);
                if (!list.Contains(k)) list.Add(k);
            }
        }
        catch { }
        return list;
    }

    /// <summary>Placas de vídeo PCI (NVIDIA, AMD, Intel) e o caminho da chave de interrupções de cada uma.</summary>
    private static List<string> GpuMsiKeys() =>
        PciKeys("SELECT PNPDeviceID FROM Win32_VideoController", @"^PCI\\VEN_(10DE|1002|8086)&");

    /// <summary>Placas de rede físicas (com cabo e Wi-Fi) e controladoras USB, os outros dois caminhos do input lag.</summary>
    private static List<string> IoMsiKeys() =>
        PciKeys("SELECT PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE")
            .Concat(PciKeys("SELECT PNPDeviceID FROM Win32_USBController")).Distinct().ToList();

    /// <summary>
    /// Modo MSI (o que o "MSI Utility" faz): o dispositivo avisa a CPU por mensagem em vez de por linha de interrupção
    /// compartilhada. Menos latência de DPC e menos engasgos. Os drivers recentes costumam já vir assim; quando não vêm,
    /// isso liga. O valor de antes de cada dispositivo fica guardado.
    /// </summary>
    private static Tweak MsiTweak(string id, string title, string desc, string effect, Func<List<string>> keys, int weight) => new()
    {
        Id = id, Weight = weight, NeedsRestart = true, Available = () => keys().Count > 0,
        Title = title, Description = desc, Effect = effect,
        Read = () => keys() is { Count: > 0 } ks && ks.All(k => ReadDword(Registry.LocalMachine, k, "MSISupported") == 1),
        Write = (on, s) =>
        {
            foreach (var k in keys())
            {
                string bk = $"{id}.{k}";
                if (!s.TweakBackup.ContainsKey(bk)) s.TweakBackup[bk] = ReadDword(Registry.LocalMachine, k, "MSISupported")?.ToString() ?? "<ausente>";
                using var key = Registry.LocalMachine.CreateSubKey(k);
                if (on) key.SetValue("MSISupported", 1, RegistryValueKind.DWord);
                else if (s.TweakBackup[bk] is string prev && int.TryParse(prev, out int pv)) key.SetValue("MSISupported", pv, RegistryValueKind.DWord);
                else key.DeleteValue("MSISupported", false);
            }
        },
        Restore = s =>
        {
            foreach (var (bk, prev) in s.TweakBackup.Where(kv => kv.Key.StartsWith(id + ".")).ToList())
            {
                string path = bk[(id.Length + 1)..];
                // O caminho vem da cópia: só chaves de interrupção de dispositivos PCI.
                if (!path.StartsWith(@"SYSTEM\CurrentControlSet\Enum\PCI\", StringComparison.OrdinalIgnoreCase)
                    || !path.EndsWith("MessageSignaledInterruptProperties", StringComparison.OrdinalIgnoreCase)) continue;
                using var key = Registry.LocalMachine.CreateSubKey(path);
                if (int.TryParse(prev, out int pv)) key.SetValue("MSISupported", pv, RegistryValueKind.DWord);
                else key.DeleteValue("MSISupported", false);
            }
        },
    };

    private static Tweak GpuMsiTweak() => MsiTweak("gpumsi", "Placa de vídeo em modo MSI (interrupções por mensagem)",
        "Faz a placa de vídeo usar interrupções por mensagem (MSI) em vez de linha compartilhada, como o MSI Utility.",
        "Menos latência do sistema e menos microengasgos. Muitos drivers já vêm assim: nesse caso aparece como aplicado.",
        GpuMsiKeys, weight: 3);

    private static Tweak IoMsiTweak() => MsiTweak("iomsi", "Rede e USB em modo MSI",
        "Liga o modo MSI nas placas de rede (cabo e Wi-Fi) e nas controladoras USB, onde ficam mouse, teclado e headset.",
        "Menos latência de interrupção no caminho do mouse e da rede. Confira com o Teste de engasgos antes e depois. " +
        "Se algum dispositivo parar depois de reiniciar (raro, em placas antigas), desligue aqui e reinicie.",
        IoMsiKeys, weight: 0);

    private static void ApplyMouse(string[] v)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse"))
        {
            k.SetValue("MouseSpeed", v[0]);
            k.SetValue("MouseThreshold1", v[1]);
            k.SetValue("MouseThreshold2", v[2]);
        }
        // Aplica na hora, sem precisar sair da conta.
        var p = v.Select(int.Parse).ToArray();
        SystemParametersInfo(0x0004 /* SPI_SETMOUSE */, 0, p, 0x01 | 0x02);
    }

    // ------------------------------------------------------------------ plano de energia

    /// <summary>Só GUIDs (ou o apelido scheme_current) vão para o powercfg: os valores salvos vêm de um arquivo.</summary>
    private static string? SafeGuid(string? s) =>
        s == "scheme_current" ? s : Guid.TryParse(s, out var g) ? g.ToString() : null;

    private static string? ActiveScheme()
    {
        var m = System.Text.RegularExpressions.Regex.Match(PowerCfg("/getactivescheme"), @"[0-9a-fA-F\-]{36}");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    private static string PowerCfg(string args)
    {
        var psi = new ProcessStartInfo("powercfg.exe", args)
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return output;
    }

    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, int[] data, uint winIni);
}
