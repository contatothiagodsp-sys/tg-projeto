# Relatório de release: tHg Optimization 1.9.0

Data: 2026-11-12. Branch de trabalho: `claude/lucid-archimedes-sx5om9`. Rodada anterior: [RELATORIO-LIMPEZA-OTIMIZACAO-RELEASE.md](RELATORIO-LIMPEZA-OTIMIZACAO-RELEASE.md).

Este relatório separa quatro tipos de evidência: **teste automatizado** (suíte xUnit), **teste real em Windows no CI** (o app e o instalador de verdade, numa máquina limpa do GitHub), **teste manual em PC real** e **teste de hardware**. Os dois últimos não foram feitos por mim: não tenho acesso a um PC com Windows, launchers, placa de vídeo e periféricos. Onde isso vale, está escrito.

## A. Resumo executivo

**Veredito: {{VEREDITO}}**

{{SITUACAO}}

O que mudou desde a 1.8.1:

- Jogos, launchers, links e relatórios não herdam mais o administrador do tHg, e não existe mais caminho de reserva que abra elevado.
- Desfazer não perde mais o valor original quando a restauração falha, nas cinco áreas (OTIMIZA, TURBO, SHIELD, Competitivo, Limpeza).
- A atualização e os instaladores de driver são conferidos e executados com o arquivo travado. Num tHg assinado, o assinante é reconhecido pela chave pública.
- 30 ações da janela que podiam fechar o app com um erro inesperado agora avisam e seguem. O tique de 1 s isola cada etapa.
- O executável ficou 15,4 MB menor (medido).

O que **não** mudou: nenhuma função saiu, o formato do `settings.json` é o mesmo, `OcGate.RealHardwareEnabled` continua `false`, nenhuma dependência foi atualizada.

## B. Correções desta rodada

As correções da rodada anterior estão no relatório anterior, seção por seção. Conferi que continuam no código e os testes delas continuam passando. Abaixo, só o que é novo.

### B.1 Relatório de falha sem abertura elevada

- **Arquivos:** `Infrastructure/CrashReporter.cs`, `MainWindow.Nav.cs`.
- **Problema:** o texto da falha ia dentro do endereço da página "nova issue". O endereço passava do limite do Explorer e por isso era aberto por `ShellExecute` direto do tHg, ou seja, o navegador nascia como administrador.
- **Implementação:** o endereço agora leva só o título e um modelo curto (menos de 600 caracteres) e abre por `Unelevated.OpenUrl`, como todos os outros links. O texto da falha vai para a área de transferência, e a página pede para colar. Antes de copiar, saem do texto o nome de usuário, a pasta do perfil e o nome do PC. Se a página não abrir ou a cópia falhar, a tela diz e aponta o arquivo `falhas.txt`.
- **Por que assim:** não cria arquivo temporário (nada para limpar, nada previsível em disco), o relatório completo continua salvo em `falhas.txt`, e o usuário vê o texto antes de enviar. O teto do texto subiu de 6.000 para 30.000 caracteres, porque não depende mais do tamanho do endereço.
- **Testes:** `CrashReportTests` (4).

### B.2 Assinante identificado pela chave pública

- **Arquivo:** `Infrastructure/BinaryTrust.cs`. Política completa em [POLITICA-ASSINATURA.md](POLITICA-ASSINATURA.md).
- **Problema:** num tHg assinado, a atualização era aceita se o texto `Subject` do certificado do instalador fosse igual ao do tHg. Dois certificados diferentes podem ter o mesmo texto.
- **Implementação:** a identidade passou a ser o SHA-256 do `SubjectPublicKeyInfo` do certificado que assinou. Mesma chave: aceito (cobre renovação do certificado). Chave diferente: só se estiver em `BinaryTrust.SuccessorKeys`, lista que uma versão assinada com a chave atual publica para anunciar a próxima. Mesmo nome com outra chave: recusado, com mensagem própria. Assinatura válida sem chave legível: tratada como não verificável.
- **Por que assim:** fixar a impressão digital do certificado quebraria toda renovação. Comparar a chave aceita a renovação comum e exige um passo explícito na troca de chave. O SHA-256 publicado continua obrigatório em todos os casos.
- **Testes:** `BinaryTrustTests`, 12 casos novos (22 no total da classe, contando os casos de teoria): sem assinatura, válida do mesmo assinante, mesmo nome com outra chave, assinatura inválida, certificado expirado, cadeia incompleta, raiz não confiável, revogado, erro de verificação, renovação com a mesma chave, chave sucessora anunciada, hash malformado e hash divergente. Os certificados do teste `KeyIdOf_identifica_a_chave_e_nao_o_nome` são gerados de verdade (RSA 2048).
- **Limite:** a parte que lê o certificado de um arquivo assinado (`ReadSigner`) só roda com um arquivo assinado de verdade, e as releases do projeto não são assinadas. Ela não foi exercitada por teste.

### B.3 Instaladores de driver: conferir e executar sem intervalo

- **Arquivos:** `Infrastructure/VerifiedLaunch.cs` (novo), `DriverInstallers.cs`, `DduCleaner.cs`, `AmdChipset.cs`.
- **Problema:** o instalador guardado (página Driver e fim da DDU) e o do chipset AMD eram conferidos e depois abertos, em dois passos. A instalação silenciosa e o atualizador já seguravam o arquivo; esses três caminhos não.
- **Implementação:** `VerifiedLaunch.Run` abre o arquivo compartilhando só leitura, confere e inicia com a trava aberta. O SHA-256 é lido pela própria trava. Conferência recusada: o arquivo é apagado depois de solto e a tela diz o motivo. Trava não obtida: nada é conferido, executado nem apagado, e dá para tentar de novo. Falha do Windows ao abrir: informada, e o instalador fica.
- **Testes:** `VerifiedLaunchTests` (9) e `PendingDriverLaunchTests` (9): arquivo alterado depois do download, arquivo substituído, tentativa de alterar, apagar, renomear e trocar durante a conferência e durante o start, assinatura inválida, hash divergente, falha ao obter a trava, limpeza do arquivo recusado, e a trava permitindo que o próprio instalador leia o executável. **No Windows do CI os quatro bloqueios (escrever, apagar, trocar por cima, renomear) foram confirmados.**
- **Limite que ficou:** o instalador agendado para depois de reiniciar (fim da DDU com reinício) é conferido ao agendar, mas não há como travar um arquivo durante a reinicialização. Ele fica na pasta protegida do tHg, onde só administradores escrevem.

### B.4 Handlers `async void`

- **Arquivos:** 12 arquivos `MainWindow.*.cs`.
- **Problema:** uma exceção num `async void` sobe ao Dispatcher e fecha o app. O relatório anterior contou 27 sem proteção; a contagem atual, feita por script que confere se todo `await` está dentro de um `try`, deu 30.
- **Implementação:** os 30 passaram a rodar dentro de `UiGuard.RunAsync`: a falha vai para o registro com a área, vira um aviso na tela e o handler termina. Onde o handler desligava um botão ou marcava "ocupado" (MAKER, TURBO, memória em espera, registros USB), a falha agora religa o botão. O fluxo assíncrono, o cancelamento e as atualizações da tela são os mesmos: o corpo de cada handler não foi reescrito, só envolvido.
- **Lista:** `BtnCapture_Click`, `DduVendor_Checked`, `BtnDduScan_Click`, `BtnDduCleanInstallers_Click`, `BtnScanGames_Click`, `BtnAddExe_Click`, `LGameList_Changed`, `BtnPickExe_Click`, `BtnLcdSend_Click`, `BtnLcdSendAll_Click`, `BtnMakerScan_Click`, `MakerReinstall_Click`, `MakerInstallBundled_Click`, `BtnMakerBundleAll_Click`, `MakerRemovePackage_Click`, `BtnMakerRestore_Click`, `UpdateQosStatus`, `SetRescanHw_Click`, `BtnShieldScan_Click`, `BtnTurboScan_Click`, `CheckFix_Click`, `BtnTurbo_Click`, `BtnPurgeStandby_Click`, `StartUpdateCheck`, `RefreshDrives`, `OnVolumes`, `InitOptimize`, `BtnUsbRemove_Click`, `BtnGpuBench_Click`, `BtnSimBench_Click`.
- **Os outros 90 `async void`:** já tinham `UiGuard` ou `try/catch` cobrindo todos os `await`. Não mexi.
- **Testes:** o `UiGuard` já tinha testes. Os handlers em si precisam da janela e não têm teste unitário; a evidência é a compilação, a revisão do diff e o app abrindo no Windows do CI.

### B.5 Tique principal

- **Arquivos:** `Infrastructure/TickGuard.cs` (novo), `MainWindow.xaml.cs`, `MainWindow.DriverCleanup.cs`.
- **Problema:** o tique de 1 s era um lambda `async` sem proteção. Uma exceção em qualquer etapa fechava o app, e uma falha no começo impedia todas as etapas seguintes.
- **Implementação:** o corpo virou `MainTickAsync`, com cada etapa (fans, sessão de jogo, sensores, painel, gargalo e as outras) dentro de `TickGuard.Step`. Uma etapa que falha não impede as seguintes. A primeira falha de cada sequência vai para o registro; se a etapa falhar 30 vezes seguidas, aparece um aviso na tela, uma vez, e o registro volta a anotar a cada 30 falhas. Quando a etapa se recupera, a contagem zera. Se a leitura dos sensores falhar, as telas desenham com a última leitura boa. Um sinalizador `_closed` impede que um tique já enfileirado toque em subsistemas desligados no fechamento.
- **O que não mudou:** a ordem das etapas, os retornos antecipados do modo leve, a cadência, o `TimerRegistry` e o `SensorRefreshGate` (que já evita leituras sobrepostas). Não há timer novo. Não adicionei "pular o tique se o anterior não terminou": a parte anterior à leitura (amostra da sessão de jogo, por segundo) precisa rodar todo segundo.
- O timer de 60 s do driver pendente também ganhou `try/catch` com registro.
- **Testes:** `TickGuardTests` (6).

### B.6 Outros pontos de abertura

- `GameLibrary.ShutdownAsync`: o pedido `steam.exe -shutdown` saía com administrador. Agora sai pelo token do Explorer e só é enviado se a Steam estiver aberta.
- `BtnDduOpenLog_Click`: o registro da DDU abria o editor de texto como administrador. Agora abre pelo Explorer do usuário.

## C. Segurança

| Tema | Situação |
|---|---|
| Processos não elevados | Jogos, programas auxiliares, launchers, links, relatórios, registro da DDU, relatório de falha e o pedido de fechar a Steam saem sem administrador. Sem caminho de reserva elevado. |
| Assinaturas | Quatro estados distintos; inválida e não verificável recusam. Assinante pela chave pública. |
| Atualizador | SHA-256 obrigatório; só GitHub por HTTPS, com o destino do redirecionamento conferido; download limitado ao tamanho publicado; parcial apagado; arquivo travado da conferência à execução. |
| Instaladores de driver | Conferência e execução sem intervalo nos cinco caminhos. |
| Pastas protegidas | `SecureFolder` intacto. Os testes de ACL passaram no Windows. |
| Restauração | Cópia do valor original só sai quando a restauração deu certo; falhas discriminadas na tela. |

### Classificação das aberturas de processo que restam com `UseShellExecute = true` ou direto

| Onde | O que abre | Elevado? | Decisão |
|---|---|---|---|
| `AppUpdater.RunInstaller` | instalador do tHg | sim | precisa: instala em Arquivos de Programas |
| `DriverInstallers.Starter`, `AmdChipset` | instalador de driver conferido | sim | precisa |
| `App.xaml.cs` (`--after`) | o próprio tHg ao reiniciar | sim | precisa |
| `MainWindow.Integ.StartOrgb` | OpenRGB | sim | exceção documentada no código: precisa do privilégio para o SMBus |
| `DduCleaner.StartWorker` | o próprio tHg como worker | sim | precisa |
| `MainWindow.Measure`, `.Integ`, `.Apps`, `.Turbo` (`ms-settings:`, `windowsdefender:`, `ms-windows-store:`) | telas do Windows | não se aplica | são apps do sistema, que o Windows abre no contexto do usuário |
| `MainWindow.Lcd`, `.Capture`, `.GameMode` (`explorer.exe /select`) | pasta no Explorer | não | o Explorer entrega o pedido ao processo dele que já está aberto |
| `ProcessRunner`, `MakerDrivers`, `AppInstaller`, `DriverManager`, `GamerAudit` | ferramentas do Windows (`powercfg`, `pnputil`, `winget`, `shutdown`) | sim | precisa: são as operações administrativas do app |

### Riscos que ficaram

1. **As releases não são assinadas.** O `publicar.yml` só assina se os segredos `SIGN_PFX_BASE64` e `SIGN_PFX_PASSWORD` existirem, e o projeto não tem certificado (o `LEIA-ME.txt` já avisa do SmartScreen). Conferi que o instalador da 1.8.1 publicada não tem assinatura, e o da 1.9.0 também não. Sem assinatura, a atualização depende do SHA-256 que o GitHub publica. Não é uma regressão, e a política do projeto não exige assinatura; é a maior melhoria de segurança disponível daqui para a frente.
2. **Manifesto global de administrador:** continua. Separar a interface de um serviço privilegiado é projeto para outra versão.
3. **Instalador agendado pela DDU para depois de reiniciar:** ver B.3.
4. **Abertura sem administrador depende do Explorer:** se o Explorer não estiver em execução, o tHg avisa e não abre. É o comportamento desejado, mas não foi testado em PC real.

## D. Estabilidade

- **Handlers assíncronos:** 120 `async void` no app. 30 protegidos nesta rodada, os outros 90 já estavam.
- **Timers:** tique principal isolado por etapa; timer do driver pendente protegido; Modo Jogo e teclado AULA já tinham proteção. Nenhum timer novo.
- **Descarte:** nenhuma mudança no ciclo de vida de recursos. O sinalizador `_closed` cobre o tique enfileirado no fechamento.
- **Concorrência:** `SensorRefreshGate` e as travas de sensores intactos.
- **ProcessRunner:** revisado, sem mudança nesta rodada. Um executor só, com prazo que cobre execução e leitura da saída, encerramento da árvore de processos, teto de 4 milhões de caracteres na saída e códigos de saída preservados. Os testes dele passaram no Windows.
- **`catch { }`:** nenhum novo em todo o branch (os dois que aparecem no diff apagam um arquivo parcial e relançam a exceção). 21 foram removidos ou substituídos por tratamento com registro.

## E. Performance

### Tamanho (medido no Windows do CI)

| Arquivo | 1.8.1 | 1.9.0 | Diferença |
|---|---|---|---|
| `tHgOptimization.exe` | 226.537.339 bytes | 211.164.905 bytes | -15,4 MB (-6,8 %) |
| Instalador | não medido nesta rodada | 61.044.252 bytes | - |

A redução vem de incluir só os recursos em português do .NET/WPF (`SatelliteResourceLanguages`).

### Abertura, memória e CPU: app real, mesmas condições

Workflow [`medir.yml`](../.github/workflows/medir.yml), [execução 43126973225](https://github.com/contatothiagodsp-sys/tg-projeto/actions/runs/43126973225). Máquina do GitHub: Windows Server 2025, 4 núcleos, 16 GB, **sem placa de vídeo dedicada e sem sensores**. Cada rodada: pasta de dados apagada, app aberto, 60 s de espera, CPU média dos 30 s seguintes. As duas versões alternadas, depois de uma abertura de aquecimento de cada uma. Janela aberta e em primeiro plano, sem jogo.

| Rodada | Versão | Janela criada | Primeira pintura | Memória em uso | Memória privada | Handles | Threads | CPU |
|---|---|---|---|---|---|---|---|---|
| 1 | 1.8.1 | 712 ms | 917 ms | 210 MB | 96 MB | 981 | 31 | 0,49 % |
| 1 | 1.9.0 | 699 ms | 950 ms | 208 MB | 92 MB | 986 | 31 | 0,66 % |
| 2 | 1.8.1 | 691 ms | 904 ms | 206 MB | 90 MB | 972 | 31 | 0,72 % |
| 2 | 1.9.0 | 773 ms | 1.031 ms | 207 MB | 91 MB | 990 | 31 | 0,57 % |
| 3 | 1.8.1 | 719 ms | 1.011 ms | 210 MB | 94 MB | 981 | 31 | 0,68 % |
| 3 | 1.9.0 | 773 ms | 1.034 ms | 209 MB | 93 MB | 978 | 30 | 0,51 % |

**Leitura honesta:** não há diferença mensurável entre as duas versões. As variações entre rodadas da mesma versão são do mesmo tamanho das diferenças entre versões. A 1.9.0 não ficou mais leve nem mais pesada em memória, CPU ou abertura; o único ganho medido é o tamanho do arquivo. Nenhuma regressão.

Contra as metas de `docs/auditoria-consumo.md`, nesta máquina: primeira pintura abaixo de 1,5 s (cumprida: 0,9 a 1,0 s), CPU abaixo de 2 % (cumprida: 0,5 a 0,7 %). A meta de 80 MB em repouso é para o app na bandeja, que este teste não mede (a janela estava aberta); com a janela aberta deu 206 a 210 MB em uso.

### Não medido

Heap gerenciado, consumo com overlay, durante benchmark, com jogo aberto, minimizado na bandeja e depois de horas aberto. Dependem de placa de vídeo, jogo e tempo que a máquina do CI não tem. Não fiz nenhuma otimização de consumo nesta rodada, porque não havia medição que apontasse onde.

## F. Testes

### Suíte automatizada

| Ambiente | Total | Aprovados | Reprovados | Ignorados |
|---|---|---|---|---|
| **Windows** (CI, [execução 43127900817](https://github.com/contatothiagodsp-sys/tg-projeto/actions/runs/43127900817)) | 1502 | 1499 | 0 | 3 |
| Linux (desenvolvimento) | 1491 | 1285 | 203 | 3 |

- As 203 reprovações no Linux são os testes que dependem do Windows (Win32, COM, ACL, WMI, registro). São as mesmas 203 da linha de base de antes de qualquer mudança, conferidas nome a nome. **No Windows, todas passam.** A diferença de total (1502 contra 1491) vem de testes cujos casos são gerados conforme o sistema.
- Os 3 ignorados são os mesmos de sempre e dependem de hardware real (`THG_HARDWARE_TESTS=1`): `MainWindowLiveTests`, `SensorSmokeTests` e `ResourceBudgetTests.Primeira_pintura`. Nenhum teste foi desativado, e nenhum `Skip` foi adicionado.
- Evolução: 1340 testes na 1.8.1, 1450 depois da rodada anterior (+110), 1491 agora no Linux (+41 nesta rodada).
- O build do Windows compila com 0 avisos e 0 erros (SDK 8.0.x do `setup-dotnet`; no Linux, 8.0.428).

### Falhas encontradas no caminho, e o que eram

1. `VerifiedLaunchTests.Durante_a_conferencia…` falhou no Windows nas duas primeiras execuções. A trava funcionou; o teste esperava `IOException`, e o Windows recusa a troca por cima de um arquivo travado com `UnauthorizedAccessException`. Corrigi o teste para aceitar as duas formas de recusa e acrescentei a confirmação de que a troca não aconteceu. A asserção ficou mais forte, não mais fraca.
2. `FanLifecycleTests.Sensor_exception_does_not_stop_the_loop` falhou **uma vez** ([execução 43126973255](https://github.com/contatothiagodsp-sys/tg-projeto/actions/runs/43126973255)) e passou na execução anterior e na seguinte, com o mesmo código do app. É um teste antigo, que espera três leituras em 5 s e depende da carga da máquina. Não mexi nele nem no código de fans. **Fica como pendência:** se ele falhar na publicação, a release não sai (o teste barra), e basta rodar de novo.

### Teste real do instalador em Windows (CI)

{{INSTALADOR}}

### Testes manuais em PC real: não executados

Não tenho acesso a um PC com Windows e launchers. Nenhum destes foi feito:

- Abrir jogo pela Steam (aberta e fechada), Epic, Ubisoft e executável direto, e conferir no Gerenciador de Tarefas que não está elevado.
- Abrir página HTTP/HTTPS, relatório HTML e link de driver e conferir que o navegador não está elevado.
- Falha do token do Explorer num PC real.
- Atualização automática de ponta a ponta pelo próprio app (baixar da release, conferir, instalar). O que foi testado: cada conferência, por teste automatizado, e a instalação por cima com os argumentos do atualizador, no CI.
- Desfazer nas cinco áreas num Windows real. O que foi testado: a lógica, com falhas forçadas, por teste automatizado.

### Testes de hardware: não executados

Fans, curvas, NVML, ADLX, OpenRGB, iCUE, Aura, SMBus, telas LCD, Lian Li, TURZX, DDU, instalação de drivers, benchmarks, ETW e métricas de FPS não foram testados em hardware. Revisei o diff inteiro do branch: nenhum arquivo desses subsistemas mudou de comportamento, exceto os caminhos de abertura de instalador (B.3) e a remoção de código sem referência da rodada anterior. Nenhuma escrita de overclock foi feita, e `OcGate.RealHardwareEnabled` continua `false` (`Core/Oc/OcCore.cs`).

### Dependências

`dotnet list package --vulnerable --include-transitive`: nenhum pacote com vulnerabilidade conhecida. Isso não quer dizer que estão na última versão: nenhuma dependência foi atualizada nesta rodada, de propósito.

## G. Build e artefatos

- **Ambiente:** GitHub Actions, `windows-latest` (Windows Server 2025), .NET SDK 8.0.x.
- **Target:** `win-x64`, Release, self-contained, arquivo único com as bibliotecas nativas embutidas.
- **Versão:** `<Version>1.9.0</Version>` no `.csproj`, a única fonte. O instalador lê a versão do próprio `.exe`. Conferido no `.exe` gerado: produto `1.9.0`, arquivo `1.9.0.0`.

{{ARTEFATOS}}

## H. Git e GitHub

{{GIT}}

## I. Release

{{RELEASE}}

## J. Pendências

1. **Testes manuais em PC real** (lista na seção F). É o item mais importante: a mudança de abrir jogos e links sem administrador é a de maior risco funcional e só foi validada por teste automatizado.
2. **Assinatura de código:** as releases continuam sem assinatura. Com um certificado nos segredos do repositório, o `publicar.yml` já assina o `.exe` e o instalador, e a política de `docs/POLITICA-ASSINATURA.md` passa a valer por inteiro.
3. **Testes de hardware** (lista na seção F).
4. **Teste instável:** `FanLifecycleTests.Sensor_exception_does_not_stop_the_loop` (seção F).
5. **Consumo:** medir na bandeja, com jogo, com overlay e depois de horas, num PC real.
6. **Arquitetura, para outra versão:** `MainWindow` (38 arquivos parciais), `AppSettings`, manifesto global de administrador. Não mexi, de propósito.
7. **Aviso do GitHub:** as ações `checkout@v4`, `setup-dotnet@v4` e `upload-artifact@v4` usam Node 20, que o GitHub está aposentando. Hoje é só um aviso.

## K. Veredito

**{{VEREDITO}}**

{{VEREDITO_TEXTO}}
