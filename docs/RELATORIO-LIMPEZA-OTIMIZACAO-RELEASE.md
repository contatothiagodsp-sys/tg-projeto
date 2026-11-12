# Relatório: limpeza, otimização, segurança e preparação para release

Base: commit `5c98d2e` (versão 1.8.1). Data: 2026-11-11. Branch de trabalho: `claude/lucid-archimedes-sx5om9`.

## A. Resumo executivo

**Veredito: APROVADO COM RESSALVAS** (seção K). O código compila limpo, nenhum teste regrediu e o CI do Windows passou inteiro (build, testes, publish e instalador). Falta o que nenhum CI cobre: teste manual num PC real da abertura de jogos e links, da atualização de ponta a ponta e das funções de hardware.

O que mudou, em ordem de importância:

1. **Abrir programas sem administrador não tem mais saída elevada.** O `catch` de `Unelevated.Start` abria o programa com `ShellExecute` a partir do tHg elevado, o que dava administrador ao jogo ou ao programa auxiliar. Agora a falha é devolvida a quem chamou e aparece na tela.
2. **Jogos de launcher, links e relatórios também saem sem administrador.** `steam://`, Epic, Ubisoft, páginas da internet e relatórios HTML eram abertos direto pelo processo elevado: com o launcher ou o navegador fechado, ele nascia como administrador. Agora passam pelo Explorer do usuário.
3. **Assinatura digital fail-closed.** Falha ao conferir a assinatura do próprio tHg era engolida e o app passava a aceitar atualização como se fosse um build sem assinatura. Agora a atualização é recusada.
4. **"Desfazer" não perde mais o valor original.** Em cinco páginas, uma restauração que falhava era engolida, a cópia do valor de antes era apagada e a tela dizia "Restaurado". Agora a cópia só sai quando a restauração deu certo, e a tela diz o que não voltou.
5. **Executor de processos único**, com o prazo valendo também para a leitura da saída e teto de memória.
6. **Limpeza comprovada:** 13 itens sem nenhuma referência removidos, 19 `using` sem uso retirados.
7. **Executável 15,4 MB menor** (226,6 → 211,1 MB, medido).

Situação inicial: o projeto já tinha passado por auditorias (timers centralizados e conferidos por teste, descarte de `Process`, pasta protegida por ACL, clientes HTTP reutilizados). Por isso as mudanças se concentram nos defeitos que ainda existiam, e não numa reorganização geral.

Números: 48 arquivos alterados; no app, +634 e −291 linhas; 110 testes novos.

`OcGate.RealHardwareEnabled` continua `false` (`Core/Oc/OcCore.cs:34`), e o teste que confere isso continua passando. Nenhum código de overclock foi tocado.

## B. Arquivos removidos

Nenhum arquivo foi removido. Não encontrei arquivo inteiro sem uso que pudesse comprovar. As remoções foram de membros dentro de arquivos (seção H).

## C. Arquivos modificados

### Segurança

| Arquivo | Problema | Alteração | Resultado esperado |
|---|---|---|---|
| `Unelevated.cs` | `catch` abria o programa elevado com `ShellExecute`. | `TryStart` devolve `UnelevatedResult` (aberto, ou o motivo). Sem fallback. Novos `OpenInShell`, `OpenUrl` e `OpenLauncherUri`, que abrem pelo Explorer com o token do usuário e validam o alvo. O registro guarda só o nome do arquivo, sem caminho nem argumentos. | Nenhum programa herda administrador por engano. A falha aparece na tela. |
| `GameLauncher.cs` | Programas auxiliares e jogos usavam o fallback; `steam://` e `LaunchUri` iam direto por `ShellExecute` elevado. | Usa `TryStart` e `OpenLauncherUri`. Falha vira mensagem de status. | Launcher e jogo nascem sem administrador. |
| `Infrastructure/BinaryTrust.cs` | `try { mine = Signer(self); } catch { }` transformava qualquer falha em "sem assinatura". | `SignatureState` com quatro estados (`Unsigned`, `Valid`, `Invalid`, `Unverifiable`), `Inspect` que não lança, e `CheckSameSigner` com a decisão isolada. SHA-256 malformado é recusado. | Falha de verificação nunca relaxa a conferência. Build local sem assinatura continua aceito, identificado como tal. |
| `AppUpdater.cs` | SHA-256 ausente só era conferido depois do download; sem limite de tamanho; destino do redirecionamento não conferido; download cancelado ficava no disco; intervalo entre conferir e executar. | `EnsureDownloadable` antes de baixar; `CopyLimitedAsync` com teto igual ao tamanho publicado (1 GB se não vier); conferência do endereço final; limpeza em qualquer falha; o arquivo fica aberto sem permitir escrita nem exclusão da conferência até o processo nascer. | Nada é baixado sem poder ser conferido, e o arquivo executado é o arquivo conferido. |
| `AmdSnapshot.cs` | Confiava no tamanho declarado no cabeçalho do .zip; sem limite de entradas nem de total descompactado; entradas de mesmo nome se sobrescreviam; `.tmp` ficava após falha. | `ReadBounded`, limites de 2000 entradas, 20 MB por JSON e 512 MB no total; reescrita indexada pela entrada; limpeza do temporário. | Um .zip trocado ou corrompido é recusado sem encher memória ou disco. |
| `MainWindow.Settings.cs`, `.Driver.cs`, `.Ddu.cs`, `.Maker.cs`, `.Disks.cs`, `.Turbo.cs`, `.BenchLab.cs` | Links e relatórios abertos por `ShellExecute` a partir do processo elevado; em `Driver` e `Ddu` a URL podia vir de dados remotos sem validação. | Passam por `Unelevated.OpenUrl` (só http/https) ou `OpenInShell`. Falha vira aviso. | Navegador sem administrador; texto remoto não vira comando. |

### Confiabilidade

| Arquivo | Problema | Alteração | Resultado esperado |
|---|---|---|---|
| `WindowsTweaks.cs`, `WindowsTweaks.Competitive.cs`, `SecurityAudit.cs` | `try { t.Restore(s); } catch { }` seguido de apagar todas as cópias. No SHIELD, o código de saída de `netsh`/`net` e o erro do PowerShell eram ignorados. | `RestoreEach` e `TryRestoreStep` registram e devolvem o que falhou. As cópias só são apagadas com tudo restaurado (no SHIELD, item a item). As quatro funções devolvem a lista de falhas. | O valor original nunca se perde por uma falha silenciosa. |
| `MainWindow.xaml.cs`, `.Turbo.cs`, `.Shield.cs`, `.Comp.cs`, `.Clean.cs` | Diziam "Restaurado" sempre; três handlers `async void` sem proteção. | Mostram o que não voltou; handlers passam pelo `UiGuard` existente. | Mensagem fiel; o app não cai. |
| `Infrastructure/ProcessRunner.cs`, `SecurityAudit.cs` | Dois executores duplicados. O do `ProcessRunner` não aplicava o prazo à leitura da saída (um filho que herdava o stdout travava a chamada) e lia sem limite. | Núcleo único `RunSplitAsync`; `SecurityAudit.RunSplitAsync` delega. Saída limitada a 4 milhões de caracteres por fluxo, lida até o fim. | Um ponto só para manter; sem travamento nem crescimento de memória. |
| `UI/ViewModels/CpuViewModel.cs` | `async void Refresh()`. | `Task RefreshAsync()`; a parte de energia registra a falha em vez de engolir. | Sem `async void` no ViewModel. |
| `MainWindow.Update.cs` | Laço `async void` de vida longa sem proteção: falha ao gravar configurações derrubava o app. | `try/catch` com registro em cada volta. | O laço sobrevive e tenta de novo. |
| `MainWindow.xaml` | Botão só com ícone ("Abrir detalhes") sem nome acessível. | `AutomationProperties.Name`. | Leitor de tela anuncia o botão. |

### Limpeza e build

| Arquivo | Alteração |
|---|---|
| `tHgOptimization.csproj` | `SatelliteResourceLanguages=pt-BR`. |
| `CHANGELOG.md` | Seção "Não lançado". |
| 19 arquivos `.cs` | Um `using` sem uso removido em cada (dois em `MainWindow.Disks.cs`). |
| 11 arquivos `.cs` | Membros sem referência removidos (seção H). |

## D. Segurança

### Corrigido

1. Fallback elevado em `Unelevated`.
2. Launchers, links e relatórios abertos pelo processo elevado.
3. Falha de verificação da própria assinatura tratada como build sem assinatura.
4. Atualizador: validação tardia do SHA-256, download sem limite, redirecionamento não conferido, intervalo entre conferir e executar.
5. URL de dados remotos aberta sem validar o esquema.
6. Restaurações que apagavam o backup depois de falhar.
7. Instantâneo AMD sem limites.

### Revisado e mantido como está

- `SecureFolder.cs`: ACL, dono confiável e bloqueio de reparse point conferidos nível a nível. Sem alteração.
- `DriverInstallers.cs` e `DriverManager.cs`: SHA-256 e assinatura do fabricante são conferidos de novo logo antes de executar, e os arquivos ficam na pasta protegida.
- Clientes HTTP: todos passam por `ResilientHttpHandler`.
- `Process.Start` restantes: instaladores oficiais (precisam de administrador), `ms-settings:`, `windowsdefender:`, a Store, `explorer /select` e o OpenRGB, que é uma exceção documentada no código porque precisa do privilégio para o SMBus.

### Ainda existente

- **Página de relatório de falha (`MainWindow.Nav.cs:79`)** continua abrindo por `ShellExecute`. A URL pode passar de 18 mil caracteres, acima do que o Explorer aceita. Risco: navegador elevado se nenhum estiver aberto. Sugestão: encurtar o corpo ou copiar para a área de transferência.
- **Comparação do emissor por `Subject`.** Um certificado válido de outra autoridade com o mesmo `Subject` passaria. Comparar por impressão digital quebraria a atualização a cada renovação do certificado; a defesa principal continua sendo o SHA-256 publicado pelo GitHub.
- **Entre conferir e executar um instalador de driver** não há trava de arquivo; a proteção é a ACL da pasta.
- **Tique principal de 1 s e 27 handlers `async void`** continuam sem `try/catch` próprio. Uma exceção ali ainda fecha o app (com registro em `falhas.txt`). Protegi os que mexem em estado do sistema; os demais estão listados na seção J.
- **`requireAdministrator` global** (seção F).

### Depende de validação adicional

- A abertura pelo Explorer (`explorer.exe "steam://…"`, links, relatórios) é uma técnica conhecida, mas **não foi executada em Windows nesta sessão**. É a mudança de maior risco funcional: se falhar num PC, o jogo de launcher não abre pelo tHg e a tela mostra o motivo.
- A trava do instalador em `RunInstaller` tem um teste que só roda de verdade no Windows (`Arquivo_travado_para_leitura_ainda_executa_e_nao_pode_ser_trocado`).
- A classificação dos códigos do `WinVerifyTrust` foi testada com valores simulados, não com arquivos assinados reais.

## E. Performance

Só o que foi medido:

| Medida | Antes | Depois | Como |
|---|---|---|---|
| `.exe` de arquivo único (win-x64, self-contained) | 226.565.541 bytes | 211.139.859 bytes | `dotnet publish` com os parâmetros do `publicar.bat`, antes e depois do `SatelliteResourceLanguages`. |
| Mesmo publish com `EnableCompressionInSingleFile` | — | 89.180.331 bytes | Medido só para referência. **Não adotado**: descompacta na memória a cada abertura, e não pude medir o tempo de abertura. |

**Não medido:** working set, heap, CPU em repouso e em jogo, tempo de abertura, primeira pintura, handles. Exigem o app rodando em Windows. Não afirmo ganho nenhum nesses pontos. As mudanças não acrescentam timers, tarefas periódicas nem leituras de sensor; a única alocação nova em caminho repetido é a leitura limitada da saída de processos, que substitui um `ReadToEndAsync`.

Os timers, a coleta de sensores e o descarte de recursos foram revisados e não alterados: já são centralizados no `TimerRegistry`, parados em `finally` e cobertos por testes.

## F. Arquitetura

**Feito:**
- Execução de processos com captura de saída num lugar só (`ProcessRunner`).
- Abertura de programas, links e launchers com uma política por tipo, toda em `Unelevated`.
- Restauração de ajustes com núcleo testável, separado da gravação do `settings.json`.

**Não feito, de propósito:**
- **`MainWindow` (38 arquivos parciais, cerca de 10.800 linhas).** Extrair responsabilidades sem poder abrir a janela em Windows seria trocar código que funciona por código não validado às vésperas do release. Ordem sugerida, uma página por vez e com teste de janela real: Atualização (`MainWindow.Update.cs`, já quase sem dependência de controles), Restauração de ajustes, Launcher, Drivers.
- **`AppSettings.cs`.** Agrupar por domínio muda o formato do JSON e exige migração; o risco de perder backups de ajustes não se justifica agora. Caminho seguro: classes de domínio como vistas sobre as mesmas propriedades, sem mudar o arquivo.
- **Injeção de dependência.** Hoje só `AppSettings`, `SensorMonitor`, navegação e `CpuViewModel` estão registrados. Ampliar acompanha a extração das páginas.

**Proposta para o privilégio (não implementada):** interface como usuário comum; serviço auxiliar elevado para registro HKLM, drivers, sensores de baixo nível, fans, DDU e `powercfg`; comunicação por named pipe com ACL restrita ao usuário da sessão e uma lista fechada de comandos, sem caminhos nem linhas de comando livres. Isso elimina a necessidade de `Unelevated`. É trabalho para uma versão própria.

## G. Testes

Ambiente: Ubuntu 24.04, SDK .NET 8.0.428. O projeto é Windows/WPF; para rodar aqui usei o targeting do Windows e os assemblies gerenciados do WindowsDesktop. Tudo que chama Win32, COM, WMI ou ACL falha neste ambiente.

| Comando | Resultado |
|---|---|
| `dotnet build tHgOptimization.csproj -c Release` (inicial) | 0 avisos, 0 erros |
| `dotnet test tests/ThgOptimization.Tests -c Release` (inicial) | 1340 testes: 1134 aprovados, 203 reprovados, 3 ignorados |
| `dotnet build … --no-incremental` (final) | 0 avisos, 0 erros |
| `dotnet test …` (final) | 1450 testes: 1244 aprovados, 203 reprovados, 3 ignorados |
| `dotnet publish … -r win-x64 --self-contained -p:PublishSingleFile=true` | Concluído |
| `dotnet list package --vulnerable --include-transitive` | Nenhum pacote vulnerável (app e testes) |

- **Os 203 reprovados no Linux são os mesmos antes e depois**, comparados nome a nome. Causas: COM (41), `ping.exe` (41), `LibreHardwareMonitorLib` (37), `cmd.exe` (16), ACL (13), `kernel32`/`advapi32`/`wintrust` e semelhantes.
- **No Windows eles passam.** O workflow `Build` rodou no commit `aa053d2` em `windows-latest` ([run 43103077391](https://github.com/contatothiagodsp-sys/tg-projeto/actions/runs/43103077391)) e todos os passos terminaram com sucesso: Compilar (Release), Testar, Publicar (arquivo único) e Instalador (Inno Setup). O passo de testes falha o job com qualquer teste reprovado, então a suíte inteira passou lá; não li a contagem exata no log. O `.exe` e o instalador de teste ficaram como artefato do run por 7 dias.
- **Os 3 ignorados** são manuais, marcados no próprio projeto ("exige hardware real e administrador").
- **110 testes novos, todos aprovados:** `ReleaseSecurityTests.cs` (Unelevated, BinaryTrust, AppUpdater), `RestoreSafetyTests.cs`, e acréscimos em `ProcessRunnerTests.cs` e `AmdSnapshotTests.cs`. Dois testes de processo rodam de verdade também no Linux (com `/bin/sh`). Um teste (`Arquivo_travado_…`) retorna sem verificar nada fora do Windows.
- Nenhum teste existente foi alterado, desligado ou ignorado.

Com o SDK 8.0.1xx o build falha com dois `CS8602` em `GpuRtBench.cs` (análise de nulidade do compilador antigo). Com o 8.0.4xx, que é o que o CI instala, não acontece.

## H. Remoções e simplificações

**Removido** (cada um com uma única ocorrência em todo o repositório, a própria declaração; busca por palavra em `.cs`, `.xaml`, `.iss`, `.bat`, `.yml` e `.md`):

| Item | Arquivo |
|---|---|
| `SessionRecorder.RecentSessions` | `Core/Benchmark/SessionTimeline.cs` |
| `DownloadAndLaunchAsync` | `DriverManager.cs` |
| `FirstTimestamp`, `CaptureDropped` | `FrameMonitor.cs` |
| `AmdChipsetUrl` | `GamerAudit.cs` |
| `MarketingName` | `GpuSpecDb.cs` |
| `SetTimer`, `GameTimer` | `Infrastructure/NativePerfApi.cs` |
| `HasMedia` | `LcdStudio.cs` |
| `ProtocolVersion` | `LightBackends.cs` |
| `MemoryResult` | `MemoryBench.cs` |
| `HighPerformance` (constante duplicada, duas vezes) | `WindowsTweaks.cs`, `GameLauncher.cs` |
| 19 diretivas `using` | apontadas pelo compilador (IDE0005) |

**Consolidado:** os dois executores de processo; a abertura de links (dois métodos privados iguais em páginas diferentes).

**Preservado de propósito:**
- `App._single` e `App._showWait`: o analisador marca como "nunca lidos", mas existem para o mutex e a espera não serem coletados.
- `TuringSerial.CmdReset`: constante do protocolo do aparelho, documenta o comando.
- Campos de structs de interop: fazem parte do layout nativo.
- `ScrollChain.Get/SetForwardAtEdges`: propriedade anexada usada pelo XAML.
- Membros usados só pelos testes (`AllowInTests`, `UseFile` e outros).
- 198 `catch { }` vazios: a maioria é limpeza de melhor esforço (apagar temporário, ler um processo que fechou). Não converti em massa; tratei os que escondiam perda de dados ou risco de segurança.

**Não foi possível comprovar:** uso por reflexão fora do repositório. Não há sinal disso, mas a busca cobre só o que está versionado.

## I. Dependências

Todas mantidas, sem atualização. Nenhuma tem vulnerabilidade conhecida.

| Pacote | Versão | Onde é usado | Mais recente |
|---|---|---|---|
| CommunityToolkit.Mvvm | 8.4.0 | `CpuViewModel`, `SensorPageViewModel` | 8.4.2 |
| LibreHardwareMonitorLib | 0.9.6 | sensores, fans (16 arquivos) | — |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.2.8 | `FrameMonitor`, `PresentationMode`, `LatencyTest` | 4.0.0 |
| Microsoft.Extensions.DependencyInjection | 8.0.1 | `App.xaml.cs` | 11.0.0 |
| Polly.Core | 8.4.2 | `ResilientHttp` | 8.9.1 |
| System.Management | 10.0.2 | WMI (24 arquivos) | 11.0.0 |
| System.Security.Cryptography.ProtectedData | 10.0.2 | `AppSettings` (DPAPI) | 11.0.0 |
| Vortice.D3DCompiler / Direct3D11 | 3.8.3 | `GpuBurn`, `GpuGameBench` | — |
| Vortice.Direct3D12 / Dxc | 3.8.3 | `GpuRtBench` | — |

Não atualizei nada: não há correção de segurança pendente, e as versões novas de maior salto (TraceEvent 4, pacotes 11.0) pedem teste de frametime e de WMI em Windows.

**`packages.lock.json`:** avaliado e não adicionado. Gerado no Linux, o arquivo pode divergir do que o Windows resolve, e o `publish -r win-x64` em modo travado quebraria o CI. Recomendo gerar no Windows (`RestorePackagesWithLockFile`) e ligar `--locked-mode` no workflow.

**Tamanho:** os maiores itens do pacote são `Microsoft.Windows.SDK.NET.dll` (24,9 MB, exigido pela Iluminação Dinâmica), `dxcompiler.dll` (17,1 MB, benchmark de ray tracing), WPF e Windows Forms. Nenhum pode sair sem remover função.

## J. Pendências

Precisam de Windows, hardware real ou credenciais:

1. ~~Rodar o CI do Windows~~ **Feito:** `build.yml` verde no commit `aa053d2` (seção G).
2. **Teste manual da abertura sem administrador:** jogo Steam com a Steam fechada, jogo Epic, jogo por `.exe`, programa auxiliar, link de driver, relatório HTML do Lab e de Discos. No Gerenciador de Tarefas, a coluna "Elevado" deve mostrar "Não".
3. **Teste manual da atualização** de ponta a ponta com um instalador real, de preferência assinado.
4. **Teste manual de "Desfazer"** nas cinco páginas, incluindo um caso forçado de falha.
5. **Medições de consumo** com as ferramentas do app (MEDIR AGORA) contra as metas de `docs/auditoria-consumo.md`.
6. **Assinatura:** não feita (exige os segredos `SIGN_PFX_*`, usados só no `publicar.yml`). O instalador de teste sem assinatura foi compilado pelo CI; instalar e desinstalar esse instalador num PC ainda não foi conferido. O `.exe` produzido no Linux não leva ícone nem o manifesto de administrador e serviu só para medir tamanho.
7. **DPI, leitor de tela e teclado:** não testados. Dos seis botões só com ícone, cinco já tinham nome acessível e o sexto ganhou.
8. **Overclock, fans, RGB, LCD, DDU, drivers:** nada foi executado contra hardware.

Deixadas para outra rodada:

- 27 handlers `async void` sem `try/catch` próprio, entre eles `BtnUsbRemove_Click`, `LGameList_Changed`, `OnVolumes`, `InitOptimize`, `BtnMakerScan_Click`, `BtnDduCleanInstallers_Click` e `BtnPurgeStandby_Click`.
- Tique principal sem proteção.
- URL do relatório de falha (seção D).
- Versão: **não alterei o número** (continua 1.8.1). No `publicar.yml`, mudar a versão e enviar para a `main` publica a release sozinho. As mudanças estão em "Não lançado" no `CHANGELOG.md`; ao decidir publicar, troque o título pela versão nova, atualize `<Version>` e as novidades (`NOVIDADES.md` e `WhatsNew`).

## K. Veredito de release

**APROVADO COM RESSALVAS.**

A favor: build Release sem avisos; suíte completa verde no CI do Windows, com publish e instalador compilados; nenhuma regressão no Linux; 110 testes novos cobrindo cada correção; nenhuma dependência vulnerável; nenhuma função removida; overclock real continua desligado.

Ressalvas que impedem "aprovado" pleno:

1. A abertura de jogos e links pelo Explorer mudou o caminho principal do Launcher. Os testes cobrem a decisão e a validação do alvo com o Windows simulado; ninguém abriu um jogo de verdade com esta versão.
2. Atualização de ponta a ponta e assinatura não foram validadas.
3. Nenhuma medição de consumo em execução.
4. Funções de hardware sem teste manual.

Não é "bloqueado" porque nenhuma dessas ressalvas é um defeito conhecido: são validações que exigem um PC real. Com os itens 2 a 4 da seção J conferidos, a versão pode sair.

### Checklist

| Item | Estado |
|---|---|
| Build Release | Aprovado (Linux e CI do Windows) |
| Testes automatizados | Aprovados no CI do Windows; sem regressão no Linux |
| Regressão conhecida | Nenhuma |
| Dependências | Verificadas, sem vulnerabilidade |
| Segurança | Revisada; pendências na seção D |
| Atualizador | Corrigido e testado por unidade; **pendente ponta a ponta** |
| Instalador | Compilado pelo CI (sem assinatura); **instalação não conferida** |
| Executável portátil | Compilado pelo CI (artefato de teste do run) |
| Configurações preservadas | Formato do `settings.json` inalterado |
| Rollback | Corrigido e testado por unidade; **pendente teste manual** |
| Logs | Novo registro sem caminho nem argumentos |
| Funções de hardware | **Teste manual pendente** |
| Versão e changelog | Changelog atualizado em "Não lançado"; versão não alterada |
| `OcGate.RealHardwareEnabled` | `false` |
