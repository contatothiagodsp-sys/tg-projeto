# Auditoria da 1.7.6 (T1 a T14)

Correções de segurança, estabilidade e desempenho sobre a 1.7.6, previstas para a 1.7.7. **Ainda não publicadas:** o `<Version>` continua em 1.7.6.

## Resultado

| Verificação | Resultado |
|---|---|
| `dotnet build tHgOptimization.csproj -c Release` | 0 erros, 0 avisos |
| Suíte completa, antes das mudanças | 1068 de 1068 aprovados |
| Suíte completa, depois das mudanças | 1124 de 1124 aprovados |
| `OcGate.RealHardwareEnabled` | continua `false` |
| Pacotes NuGet novos | nenhum |
| Teste desabilitado ou apagado | nenhum |

## O que não foi verificado

Tudo abaixo depende de abrir o app, e ele não foi aberto com estas mudanças. O tHg instalado estava rodando e o app aceita uma instância só. Os testes cobrem a lógica, não o uso real.

- **Painel (T11):** o gráfico e os anéis foram testados fora da tela. Falta olhar na janela, inclusive trocando o tema.
- **Prévia do LCD (T9):** o teste confirma o aviso de mudança e os pixels novos no bitmap reaproveitado. Falta ver um GIF animando na prévia e o envio para uma tela real.
- **Bandeja (T7):** o ícone de reserva não tem teste, porque criar um `TrayIcon` adicionaria um ícone de verdade à bandeja.
- **Overlay (T7):** o estilo novo foi testado numa janela comum. Falta confirmar que o overlay continua deixando o clique passar.
- **Reset de OC (T12):** só o texto e a decisão de desarmar têm teste. O reset em uma placa NVIDIA real não foi executado.
- **DDU (T2):** o caminho de falha ao iniciar a limpeza não foi exercitado.

## Correções

### T1: atalho de pasta na limpeza do Windows
- **Arquivos:** `WindowsCleanup.cs`, `DduDeleteTreeTests.cs`.
- **Problema confirmado:** quatro testes novos falharam antes da correção, com a exclusão atravessando a junction e apagando o arquivo do outro lado.
- **Correção:** `Clean` e `Measure` conferem a pasta e o caminho até ela com `DduCleaner.PassesThroughReparse`. Pasta com atalho é ignorada, com registro e aviso na tela. Se a conferência falhar, a pasta também fica de fora.
- **Conferido nesta máquina:** nenhuma pasta real de limpeza foi acusada como atalho.
- **Limite:** ainda existe um intervalo entre conferir e apagar. Um programa rodando ao mesmo tempo poderia trocar a pasta nesse intervalo.

### T2: `async void` e estado dos botões
- **Arquivos:** `Infrastructure/UiGuard.cs` e as páginas Clean, Comp, Oc, Lcd, Ddu, Latency, GameMode, BenchLab, Capture, Settings, Integ e Rt.
- **Correção:** novo `UiGuard.RunButtonAsync`. Falha agora vira registro e aviso, e o botão sempre volta. Isso importa porque `DispatcherUnhandledException` só registra a exceção e não a marca como tratada, então ela derrubava o app.
- **Sem alteração:** Disks e Net já tinham `catch` e `finally`.
- **Fora do escopo pedido:** Apps, Diagnostics, Driver, Fans, Launcher, Maker, NetExtra, Nav, Shield, Turbo, Update e `MainWindow.xaml.cs` ainda têm handlers `async void` que não foram revisados nesta tarefa.

### T3: coleções de `AppSettings`
- **Arquivos:** `AppSettings.cs`, `Infrastructure/AppLog.cs`, `GameMode.cs`, `CompetitiveProfiles.cs` e leitores da janela.
- **Correção:** `Read` e `CopyList` leem sob `SaveLock`. Os leitores pedidos passaram a usá-los, e também `Rt`, `Oc`, `Disks` e `CustomGames`.
- **`null` no arquivo:** `SettingsMigration.EnsureCollections` recria as coleções ausentes. `OverlayItems` e `ComDbBackup` continuam podendo ser `null`, porque aí `null` tem significado. `OcProfiles` volta com as 5 vagas.
- **Além do pedido:** `LegacySettingsMigration.Sanitize` também chama essa rotina. Antes, um arquivo antigo com `null` fazia a migração inteira falhar sem aviso. Itens `null` em dicionários são removidos.

### T4: `_game` no Modo Jogo
- **Arquivo:** `GameMode.cs`.
- **Correção:** `_game` agora é `volatile`, e o acesso de `_dirty` ficou documentado.

### T5: vigia de OC assíncrono
- **Arquivo:** `Core/Oc/OcGpuProviders.cs`.
- **Correção:** novo `TickAsync`, usado pelo `MonitorLoop`. `Tick` continua como invólucro síncrono. Um teste novo comprova que a espera entre tentativas não prende a thread.

### T6: `CancellationTokenSource` e timers
- **Arquivos:** `MainWindow.xaml.cs`, Disks, Net, Oc, Driver, Launcher.
- **Correção:** `DisposeCts` descarta e deixa o campo em `null`, então um clique tardio não chama `Cancel` em objeto descartado.
- **Ao fechar:** o `Closed` para todos os timers antes de desligar os subsistemas. Um teste novo lê os campos `DispatcherTimer` e falha se algum novo não parar ali.
- **Mantido de propósito:** `_drvCts` e `_drvInstallAllCts` não são cancelados ao fechar, para não interromper um instalador de driver no meio.

### T7: Win32
- **Arquivos:** `TrayIcon.cs`, `OverlayWindow.xaml.cs`, `ToastWindow.xaml.cs`, `Infrastructure/WindowExStyle.cs` (novo), `Infrastructure/ProcessUtil.cs`.
- **Ícone:** `_iconOwned` impede destruir o ícone compartilhado do Windows.
- **Estilo da janela:** `WindowExStyle` usa as versões Ptr em 64 bits. A especificação citava `TrayIcon`, que não usa essas funções. Quem usava era `OverlayWindow` e também `ToastWindow`, então a correção foi nesses dois.
- **Handles:** `CanOpen` e `WithHandle` usam `SafeProcessHandle`.
- **Sem alteração em `TuringSerial.cs`:** a queixa já não se aplica. `LcdPortOpener.Using` descarta a porta em qualquer falha, e isso já tinha teste. `Init` não reabre a porta.

### T8: prazo em `SecurityAudit`
- **Arquivos:** `SecurityAudit.cs`, `ProcessRunnerTests.cs`.
- **Correção:** `RunSplitAsync` limita o programa e as saídas dele. Um filho que herda o stdout não prende mais a chamada para sempre.
- **Além do pedido:** o processo só é descartado depois das leituras terminarem, porque fechar um pipe com leitura pendente pode travar. Se o prazo vencer com o programa já encerrado, volta o código real em vez de -1.

### T9: prévia do LCD
- **Arquivos:** `LcdStudio.cs`, `MainWindow.Lcd.cs`.
- **Correção:** a prévia usa `RenderLive`, que reaproveita o bitmap. `Render` fica para salvar o quadro. `Typeface` e pincel de sombra agora são estáticos.
- **Diferença da especificação:** `Typeface` não tem `Freeze`. Ele é imutável, então só o pincel é congelado.

### T10: caminhos quentes
- **Arquivos:** `Core/Benchmark/FrameTimeStatistics.cs`, `Infrastructure/CpuInfo.cs`, `BottleneckAnalyzer.cs`, `SensorMonitor.cs`.
- **`Median`:** ordena num buffer do `ArrayPool`. Um teste limita a alocação a menos de 1 KB com 50 mil valores.
- **`Live`, `Cores` e `Sample`:** uma passada só pela lista, escolhendo as mesmas linhas de antes, inclusive em empates. Os testes travam essa ordem. As expressões regulares de `Cores` continuam, agora só nas linhas da CPU.
- **`RefreshAsync`:** novo `SensorRefreshGate`. Uma leitura por vez, e cada uma termina depois de aplicada, então duas leituras não aplicam fora de ordem.
- **Removido:** `Refresh()` e `Refresh(bool)` síncronos. Uma busca confirmou que ninguém os chama.

### T11: gráficos do painel
- **Arquivos:** `Views/DashCharts.cs` (novo), `MainWindow.Dash.cs`, `MainWindow.xaml.cs`.
- **Correção:** `DashChartView` e `DashRingsView` criam os elementos uma vez e só trocam propriedades. As cores são relidas a cada atualização, para acompanhar o tema. O arco só é refeito quando o valor muda de forma visível, e a legenda só é trocada quando muda.
- **Sem alteração em `FrametimeCompareChart`:** já usava pincéis e tracejados congelados, e só redesenha ao receber dados ou mudar de tamanho.
- **Ajuste menor:** o `DrawChart` da página de stress só passou a usar um tracejado congelado. Ele continua recriando os elementos, porque só roda durante o teste.

### T12: reset e verificador de OC
- **Arquivo:** `MainWindow.Oc.cs`.
- **Correção:** `DescribeReset` informa energia e clock separadamente. `Disarm` só roda se os dois voltaram, porque em reset parcial parte do ajuste continua valendo.
- **Verificador:** as duas leituras de `OcCheck.ErrorsSince` em `RunOcCheckAsync` saíram da thread da janela.
- **Pendente:** `UpdateDashHealth`, no painel, ainda lê `ErrorsSince` na janela, no máximo a cada 10 minutos.

### T13: diálogo e aviso
- **Arquivos:** `ThgDialog.cs`, `ToastWindow.xaml.cs`.
- **Correção:** `CanOwn` recusa janela invisível, o próprio diálogo, overlay e aviso. `Notify` sempre chama `Place()`.
- **Além do pedido:** o timer do aviso para no `Closed`, porque `Hide` numa janela fechada lançaria exceção.

## Diferenças em relação à especificação

**Comando de teste.** Não há `.sln`, então `dotnet test -c Release` na raiz não roda a suíte. O comando certo, igual ao do CI, é:

```
dotnet test tests\ThgOptimization.Tests -c Release
```

**Filtros.** Alguns filtros usam nome de arquivo, mas o xunit filtra por nome de classe. Eles não rodam nenhum teste e o comando termina sem erro, o que pareceria aprovação. Usei as classes reais:

| Tarefa | Filtro da especificação | Classes usadas |
|---|---|---|
| T4 | `GameExitedAndClientsTests`, `GameProcessTuningTests` | `ProcessHasExitedTests`, `ClientDemoterTests`, `GameHandleTests`, `GameIoPriorityTests`, `GameModeDefaultsTests`, `GamePriorityRestoreTests`, `ForegroundLookupTests` |
| T5 | `OcLifecycleTests` | `OcGateClosedTests`, `ProviderRestoreTests`, `WatchdogLifecycleTests` |
| T7 | `ProcessSnapshotTests`, `GameProcessTuningTests`, `FrameMetricsTests` | classes dos três arquivos, mais `WindowExStyleTests` e `ProcessHandleTests` |
| T10 | `DiagnosticsTests` | classes do arquivo, mais `FrameTimeMedianTests`, `SensorHotPathTests`, `SensorRefreshGateTests` |
| T13 | `Release170UxTests` | classes do arquivo, mais `DialogAndToastTests` |

## Para publicar como 1.7.7

Subir o `<Version>` e dar push gera a release e o auto-update para todos os usuários. Por isso, ficou de fora até o app ser aberto. Passos:

1. Subir o `<Version>` para 1.7.7 no `tHgOptimization.csproj`.
2. Trocar `## [Não publicado]` por `## [1.7.7] - <data>` no `CHANGELOG.md`.
3. Se quiser a janela de novidades, adicionar o texto em `WhatsNew.Notes` e em `NOVIDADES.md`, e ajustar o `InlineData` de `Whats_new_shows_once_per_version` em `Release160Tests.cs`.
