# Relatório da release 2.0.0 · tHg Intelligence

Data: 2026-11-12. Base: `main` em `6d4009a` (1.9.0 publicada). Plano seguido: [PLANO-tHg-INTELLIGENCE-2.0.md](PLANO-tHg-INTELLIGENCE-2.0.md).

## A. Resumo executivo

**Objetivo.** Fazer o tHg diagnosticar antes de otimizar, medir depois e aprender com o resultado, por jogo e por PC, sem nuvem.

**Entregue.**

- Motor de diagnóstico de sessão com 17 classes, evidências, limitações declaradas e confiança calculada.
- Aprendizado local por jogo: referência por condição, sessões anormais retidas, mudança de regime.
- Catálogo fechado de ações com transação, estado anterior, confirmação, restauração e detecção de conflito.
- Medição automática de cada ação aplicada: mantém com melhora comprovada, desfaz com piora ou sem ganho.
- Controlador de sessão de jogo com 8 estados e restauração garantida.
- Área **tHg Intelligence** (Diagnóstico, Perfis de jogos, Este PC), cartão no painel, primeiro uso, duas linhas novas no overlay.
- Sonda de rede da sessão e diagnóstico de rede separado do FPS.
- Governador do consumo do próprio tHg durante o jogo.
- Release com `SHA256SUMS.txt`, SBOM e pedido de atestação de proveniência.

**Diferença para a 1.9.** A 1.9 foi uma versão de segurança e confiabilidade. A 2.0 acrescenta a camada de decisão. Nenhuma função da 1.9 saiu, e as proteções dela (pasta protegida, abertura sem administrador, conferência de binários, atualizador com SHA-256, `TickGuard`, `UiGuard`, `ProcessRunner`) estão intactas: os 1.502 testes anteriores continuam passando.

**O que esta versão não é.** Não foi validada em jogo real. Não há PC com GPU e jogos no ambiente de desenvolvimento. Os limiares do diagnóstico vêm de critérios de engenharia, não de calibração em campo. Por isso o padrão é recomendar, e as ações do motor são só opções do Modo Jogo que já existiam. **Nenhum ganho de FPS é afirmado neste relatório.**

## B. Arquitetura

```
Modo Jogo (detecção, 1 amostra/s, frames por ETW)      já existia
        │  SessionTimeline + programas de fundo + sonda de rede
        ▼
SessionTelemetryAdapter → SessionTelemetry              Infrastructure/Intelligence
        ▼
DiagnosisEngine → DiagnosisReport (achados, confiança)  Core/Intelligence
        ▼
LearningEngine (referência por jogo e condição)
        ▼
ExperimentEvaluator ← ações ativas do diário → manter / desfazer (ActionExecutor)
        ▼
RecommendationEngine → recomendações → (clique do usuário) → ActionExecutor
        ▼
IntelligenceStore (JSON local)        →        MainWindow.Intelligence.cs
```

| Camada | Arquivos | Depende de |
|---|---|---|
| `Core/Intelligence` (11 arquivos) | `Telemetry`, `RobustStats`, `Diagnosis`, `DiagnosisEngine`, `Learning`, `Experiment`, `Actions`, `Recommendations`, `GameSessionController`, `MachineProfile`, `IntelligenceStore`, `IntelligenceOrchestrator` | Só a BCL e `FrameStatistics` |
| `Infrastructure/Intelligence` | `IntelligenceService` (adaptador de sessão, inventário, catálogo, serviço), `SessionNetProbe` | Configurações, Modo Jogo, sensores já lidos |
| Interface | `MainWindow.Intelligence.cs`, três páginas e um cartão em `MainWindow.xaml` | Serviço |

**Serviço e ciclo de vida.** `IntelligenceService` é criado pela janela no primeiro uso e descartado no fechamento, antes do Modo Jogo. A janela recebe configurações e monitor pelo DI existente; o serviço não entrou no contêiner para não mudar o construtor da janela, que um teste usa diretamente. Se a pasta protegida não puder ser usada, o app segue sem o Intelligence.

**Persistência.** `Intelligence\intelligence.json` na pasta protegida do usuário (a mesma das configurações). Versão de formato 1, gravação atômica, limites: 200 diagnósticos, 30 relatórios completos, 300 linhas de diário (as ativas nunca saem), 60 sessões por jogo, 20 por referência. Arquivo corrompido é guardado como `.corrompido`; arquivo de versão mais nova nunca é reescrito.

**Configurações.** Cinco campos novos com padrão em `AppSettings`. Nenhuma migração. Um `settings.json` da 1.9.0 foi atualizado de verdade no CI (seção J).

**Dependências.** Nenhum pacote novo. Ver seção F.

## C. tHg Intelligence

**Qualidade dos dados.** Antes de qualquer regra: duração, número de frames e cobertura de cada grandeza. Menos de 2 min: inconclusivo. 1% low só com 1.000 frames; 0,1% low só com 10.000. Grandeza com menos de metade dos segundos cobertos conta como não medida e vira limitação no relatório.

**Confiança.** `força da regra × fator de duração × cobertura das medidas usadas`, com teto quando a medida é indireta. Faixas: alta ≥ 0,75, média ≥ 0,5, baixa ≥ 0,3. Cada confiança guarda os motivos, e a tela mostra.

**Diagnósticos e critério de cada um.**

| Classe | Afirma quando | Não afirma quando |
|---|---|---|
| GPU é o limite | GPU ≥ 95% em 70% do tempo. Marcado como comportamento normal. Teto 0,9 | 99% por um terço do tempo |
| CPU é o limite | CPU ≥ 85% em metade do tempo, GPU com folga | |
| Thread principal | Um núcleo ≥ 90% em metade do tempo, CPU total baixa, GPU com folga | Sem leitura por núcleo |
| Trava de FPS | FPS estável num valor típico, nada no limite. Vai para "verificado" | |
| Temperatura CPU/GPU | Perto do limite **e** clock ≥ 5% menor do que nos segundos frios, com carga comparável | Quente sem queda de clock: "sem limitação comprovada" |
| Perda térmica progressiva | FPS −8%, clock −3% e temperatura +5 °C do primeiro terço para o trecho final | Sessão com menos de 15 min |
| Limite de potência | Consumo preso num teto, clock abaixo do máximo observado, sem calor. Teto 0,6, "possível" | Clock baixo sozinho |
| Pressão de RAM | RAM ≥ 92% ou commit ≥ 90%. Problema só se os engasgos coincidem | RAM em 80% sem pressão: "uso normal" |
| VRAM | ≥ 95% em 10% do tempo | |
| Frame pacing | ≥ 2 engasgos/min ou P99 ≥ 2,5× a mediana, com frames medidos | Um pico isolado |
| Programas de fundo | Processo acima de 10% da CPU em leituras seguidas (medido pelo Modo Jogo) | |
| DPC/ISR | Medição recente (≤ 30 dias) acima de 1.000 µs **e** engasgos | Medição velha: vira limitação |
| Disco | Jogo em HD mecânico **e** engasgos | |
| Rede | Perda ≥ 2%, jitter ≥ 20 ms ou picos. Texto diz que não derruba FPS | Menos de 20 sondas |
| Instabilidade | Evento WHEA/TDR registrado. Vale em sessão curta | |
| Driver | Piora na primeira sessão com driver novo, resto igual. Confiança baixa | |
| Inconclusivo | Coleta curta, ou engasgo sem nenhuma condição associada | |

**Associação, não causa.** Para ligar engasgos a uma condição, o motor compara a presença da condição nos segundos ruins (e nos 2 s anteriores) com o resto. Exige 5 segundos ruins, presença em metade deles e 25 pontos acima do resto. O texto diz "coincide" e "associação".

**Aprendizado.** Resumo de cada sessão (poucos números). Referência = mediana e MAD das sessões aceitas com a mesma assinatura (hardware, driver, resolução, tomada/bateria). Sessão com menos de 5 min não ensina. Desvio robusto ≥ 3,5 em FPS, 1% low ou P99: fica retida. Três retidas seguidas na mesma direção refazem a referência. Assinatura nova começa referência nova.

**Recomendações.** Cada uma traz problema, proposta, benefício possível, risco, compatibilidade, reinício, como desfazer e como medir. Uma ação só é oferecida com diagnóstico que a justifica, confiança mínima, compatibilidade e ausência de configuração equivalente. Destinos de "abrir tela" são uma lista fechada, conferida por teste contra as páginas reais.

**Machine learning.** Não há modelo treinado. Sem dataset rotulado, um modelo seria aparência. ONNX Runtime e Windows ML foram avaliados e descartados nesta versão.

## D. Game Session Controller

- **Detecção:** a do Modo Jogo (janela em primeiro plano, nome e caminho do executável contra a biblioteca e os perfis conhecidos). Sem ler memória, sem injeção, sem hook. Valorant continua reconhecido só pelo nome.
- **Estados:** Idle, Detecting, Preparing, Gaming, Capturing, Diagnosing, Restoring, Error, com tabela de transições testada.
- **Garantias testadas:** observação repetida não repete trabalho; troca de jogo entre dois tiques fecha uma sessão antes de abrir a outra; falha ao preparar ou diagnosticar não pula a restauração; app fechando restaura sem diagnosticar; 64 observações em paralelo abrem uma sessão só.
- **Diagnóstico:** agendado fora da thread da janela quando o jogo fecha.
- **Restauração:** o que o Modo Jogo altera durante a partida continua sendo desfeito por ele, como na 1.9.
- **O próprio tHg em jogo:** já havia modo leve e pausa de timers. A 2.0 mede a CPU do processo a cada 10 s; acima de 1,5% sustentado, overlay no perfil econômico; se persistir, pausa as tarefas não essenciais. Avisa o usuário e volta sozinho.

## E. Game Profiles

- Lista: biblioteca detectada (Steam, Epic, Riot, jogos adicionados à mão) mais os jogos com dados aprendidos.
- Por jogo: perfil de uso, referência por condição com valores habituais, últimas sessões, otimizações e resultado medido, apagar dados.
- Perfis de uso: Competitivo, Performance, Equilibrado, Silencioso, Personalizado. Orientam quais recomendações aparecem; não aplicam pacote de ajustes.
- Associação manual de executável: continua em Jogos › Biblioteca; a página aponta para lá.
- **Não entregue:** ícone do jogo na lista; página própria por jogo (o detalhe fica ao lado da lista).

## F. Performance (medição)

| Grandeza | Fonte | Situação na 2.0 |
|---|---|---|
| Frames, frametime, percentis, 1%/0,1% low | ETW próprio (`FrameMonitor`) | Como na 1.9; lows só com amostra suficiente |
| GPU Busy, tempo de CPU por frame | Serviço do PresentMon | **Não medido.** Exige instalar um serviço; o tHg não instala em silêncio nem redistribui |
| Uso, clock, temperatura, consumo de CPU e GPU | LibreHardwareMonitor, NVML, ADLX (um leitor só) | Como na 1.9 |
| Núcleo mais ocupado | Mesmo laço dos sensores | Novo na gravação de sessão |
| Memória comprometida | `GlobalMemoryStatusEx` | Novo |
| Rede | ICMP a cada 5 s | Novo |
| Paginação, page faults, disco por segundo, context switches | — | **Não medido** |
| DPC/ISR | Teste de engasgos existente, fora da sessão | Entra como evidência externa com data |

**ETW.** Nenhuma sessão nova. Captura detalhada sob demanda (context switch, disco) não foi implementada.

**Dependências revisadas.** Nenhuma atualizada: CommunityToolkit.Mvvm 8.4.0, LibreHardwareMonitorLib 0.9.6, TraceEvent 3.2.8, Polly.Core 8.4.2, Vortice 3.8.3, System.Management 10.0.2. Não houve motivo técnico para trocar, e este ambiente não permite validar sensores depois de uma troca. Versões mais novas não foram levantadas uma a uma. NVAPI não foi integrada.

## G. Otimização

**Catálogo (6 ações).** Todas ligam uma opção do Modo Jogo, que vale só com um jogo aberto.

| Ação | Classe | Justificada por |
|---|---|---|
| Ligar o Modo Jogo automático | B | Sem sessão para analisar |
| Segurar programas de fundo | B | Programas de fundo |
| EcoQoS nos programas de fundo (nunca no jogo) | B | Programas de fundo |
| Plano de alto desempenho durante o jogo | B | Possível limite de potência |
| Separar programas de fundo dos núcleos do jogo | B | CPU / thread principal, 12+ threads |
| Preferir os núcleos mais rápidos (CPU Sets) | C | CPU / thread principal, só híbrido ou dois CCDs |

**Transação.** Lê e guarda o estado; se já é o desejado, não altera; escreve com limite de tempo; lê de novo; se não confirmou, devolve o anterior e relata a falha. Desfazer compara o valor atual com o que o tHg deixou: se outro mudou, não sobrescreve e informa o valor original.

**Política.** Só diagnóstico / Recomendar (padrão) / Automático seguro (só classe B pré-aprovada). Classe C sempre pede confirmação. Classe D é recusada mesmo com aprovação.

**Resultados A/B.** O avaliador exige 3 sessões comparáveis antes e 3 depois, intervalo de confiança de 95% (bootstrap das medianas) excluindo o zero e diferença maior que o dobro do ruído, com piso de 3%. **Não há resultado de A/B em jogo real neste relatório.** O Performance Lab da 1.9 segue igual; o veredito novo não foi ligado à tela dele.

**Revisão do catálogo de ajustes do Windows (item 16 do pedido).** **Não feita.** Os ajustes de OTIMIZA, TURBO, Competitivo e Limpeza estão como na 1.9. Avaliar cada um exigiria medir em hardware real. Nenhum deles é aplicado pelo Intelligence.

## H. Experiência

- **Painel inicial:** cartão com o estado da sessão, o último diagnóstico e a recomendação principal.
- **Diagnóstico:** DIAGNOSTICAR AGORA (relê a sessão mais recente), números da sessão com origem (medido / estimado / indisponível), achados com evidências e "por que esta confiança", verificado e sem problema, limitações, recomendações com APLICAR / abrir / DISPENSAR, histórico com relatório completo e DESFAZER.
- **Primeiro uso:** três passos, puláveis.
- **Este PC:** resumo, o que dá para medir, inventário, dados e privacidade.
- **Overlay:** "limitação detectada agora" e "perfil do jogo e estado da sessão", desligadas por padrão.
- **Relatório de sessão:** texto com desempenho, diagnóstico, verificado, comparação com sessões anteriores, otimizações, recomendações e limitações. Diz que input lag não é medido.
- **Acessibilidade:** nomes de automação nos controles novos, regiões vivas nos títulos de estado, navegação por teclado dos estilos existentes. Sem teste com leitor de tela.
- **Não entregue:** gráficos novos no Performance Lab; timeline nova (a do Histórico continua a mesma); revisão visual geral do app.

## I. Segurança e privacidade

- **Sem comando livre.** O motor só conhece ids de um catálogo fechado; id desconhecido é recusado (teste).
- **Sem execução de conteúdo.** A importação lê só dados, tem limite de tamanho e não importa o diário de ações.
- **Sem elevação nova.** A tela do Windows aberta por uma recomendação usa `Unelevated.OpenInShell`.
- **Sem rede além da sonda.** ICMP para o roteador; se ele não responde a ping, para `1.1.1.1`. Desligável.
- **Privacidade.** Dados locais; o inventário omite números de série e nome do computador (teste). Exportar, importar, apagar tudo, apagar por jogo e retenção de 90/180/365 dias.
- **Anti-cheat.** Nenhuma técnica nova toca o processo do jogo.
- **Hardware.** `OcGate.RealHardwareEnabled = false`, conferido por teste. Nenhuma ação altera clock, tensão, limites de potência ou MSR.
- **Atualizador.** Sem mudança de código. Política de release sem assinatura escrita em `POLITICA-ASSINATURA.md`.
- **Não feito:** auditoria nova de `SecureFolder`, `BinaryTrust`, `AppUpdater`, `VerifiedLaunch`, `ProcessRunner` e `CrashReporter`. Não foram alterados; valem os testes da 1.9.

## J. Testes

Rodada de referência: workflow Build nº 43306931905, commit `cf52d80`, Windows Server 2025 (runner `windows-latest`, AMD EPYC, sem GPU dedicada).

| | Quantidade |
|---|---:|
| Total | 1.645 |
| Aprovados | 1.642 |
| Reprovados | 0 |
| Ignorados (dependem de hardware ou de janela real, opt-in) | 3 |
| Testes que já existiam na 1.9 | 1.502 |
| Testes novos | 143 |

Build Release: 0 erros, 0 avisos. Nenhum teste foi desativado, removido ou marcado para pular.

**Testes novos (143).**

- `IntelligenceDiagnosisTests` (42): cada diagnóstico com o caso positivo e o caso em que a aparência engana; qualidade dos dados; confiança; estatística robusta; inventário.
- `IntelligenceEngineTests` (78): executor de ações (19), controlador de sessão (12), aprendizado (10), avaliação A/B (9), recomendações (10), persistência e orquestração (15), consumo do próprio tHg (3).
- `IntelligenceIntegrationTests` (23): sessão gravada pela 1.9 continua abrindo; colunas novas gravadas e lidas; catálogo sobre as configurações reais; conflito ao desfazer; padrões seguros num `settings.json` antigo; sonda de rede com ping falso; inventário sem identificadores; navegação e manipuladores do XAML novo; overclock real desligado.

Dois testes de proteção que já existiam apontaram um erro meu durante o desenvolvimento: a lista de ações pré-aprovadas estava fora da cópia e da migração das configurações. Corrigido antes do merge.

**Janela real no CI.** O teste manual da janela (`MainWindowLiveTests`) passou a rodar no Build como passo que não bloqueia. Nesta rodada ele passou: abriu a janela de verdade, navegou por 29 páginas, e a área do Intelligence desenhou um diagnóstico sintético (6 blocos de achados, 2 recomendações, 1 linha de histórico), o perfil do jogo (12 elementos), o inventário (9 medidas, 6 grupos) e o cartão do painel. Nenhuma exceção e nenhuma linha de erro no registro. É uma janela sem GPU nem sensores de temperatura, com dados sintéticos: prova que a interface monta e responde, não que o diagnóstico acerta num jogo.

**Instalador de verdade** (workflow Testar instalador nº 43257102336, commit `77fe9a2`): instalou a 1.9.0 publicada, abriu, rodou o instalador da 2.0.0 por cima com o app aberto, e conferiu: versão instalada 2.0.0, `.exe` idêntico ao gerado (SHA-256), processo antigo fechado, app reaberto sozinho, uma instância só, respondendo, nenhum arquivo de dados do usuário perdido, `settings.json` preservado, atalhos mantidos, nenhuma falha registrada pelo app, desinstalação limpa. Também instalação limpa da 2.0.0. Tudo aprovado. O commit testado é anterior a dois ajustes pequenos de interface e de teste; o instalador não mudou depois dele.

**Testes instáveis da 1.9.** `FanLifecycleTests.Sensor_exception_does_not_stop_the_loop` e `OcProviderTests.Hardware_error_rolls_back_at_once…` falharam uma vez cada. Nos dois, o código sob teste precisa de uma thread do pool dentro de 5 s, enquanto outras classes seguram threads em paralelo. O mínimo do pool nos testes passou a 64. Os testes e os prazos não mudaram. A falha não foi reproduzida sob demanda, então a causa é a explicação compatível, não uma prova; nas rodadas da 2.0 (cinco execuções completas da suíte no CI) nenhum dos dois falhou, o que é pouco para afirmar que o problema acabou.

**Não executado.**

- Jogo real, em qualquer cenário (GPU-bound, CPU-bound, competitivo, pesado, com stutter).
- Hardware real: sensores, NVML/ADLX, Intel Arc, notebook, CPU híbrida.
- Anti-cheat real.
- Uso manual da interface nova por uma pessoa, leitor de tela, DPI alto, múltiplos monitores.
- Atualização automática de ponta a ponta pelo app (o instalador por cima foi testado; o download pelo atualizador, não).
- Windows 10.

## K. Performance do aplicativo

Medição do workflow Medir consumo nº 43257102935 (commit `77fe9a2`), que abre o app real e alterna a 1.9.0 publicada ("base") com a 2.0.0 deste branch ("novo") na mesma máquina. Ambiente: Windows Server 2025, 4 núcleos, 16 GB, **sem GPU dedicada e sem sensores**. Vale só para comparar as duas versões nesse ambiente.

| Rodada (30 s cada) | Versão | Memória privada | Working set | CPU | Handles | Threads | Primeira pintura |
|---|---|---:|---:|---:|---:|---:|---:|
| 1 | 1.9.0 | 147 MB | 261 MB | 0,66% | 1.128 | 30 | 1.577 ms |
| 1 | 2.0.0 | 149 MB | 264 MB | 0,89% | 1.157 | 30 | 1.580 ms |
| 2 | 1.9.0 | 145 MB | 261 MB | 0,61% | 1.159 | 30 | 1.584 ms |
| 2 | 2.0.0 | 147 MB | 265 MB | 0,85% | 1.161 | 33 | 1.491 ms |
| 3 | 1.9.0 | 146 MB | 262 MB | 0,68% | 1.154 | 30 | 1.616 ms |
| 3 | 2.0.0 | 145 MB | 260 MB | 0,66% | 1.133 | 31 | 1.540 ms |

Leitura:

- **Memória e primeira pintura:** sem diferença distinguível entre as versões.
- **CPU em repouso:** a 2.0.0 ficou acima em duas das três rodadas (média 0,80% contra 0,65%) e igual na terceira. Três rodadas não separam isso do ruído, mas a direção é plausível: o serviço do Intelligence é criado na abertura e observa o Modo Jogo uma vez por segundo. As duas versões ficam dentro da meta de 2%.
- Os valores absolutos de memória desta rodada são maiores que os do relatório da 1.9 (90–94 MB privada) **nas duas versões**. A base é a mesma 1.9.0, então a diferença é do ambiente (imagem do runner de 2026-11-03), não do código. Não investigado além disso.
- A primeira pintura ficou em torno de 1,5–1,6 s nas duas versões, no limite da meta de 1,5 s neste ambiente.

Tamanho: `tHgOptimization.exe` 211.508.969 bytes (1.9.0: 211.164.905; +344 KB). Instalador 61,1 MB (1.9.0: 61,0 MB).

**Orçamentos** (de `docs/auditoria-consumo.md`, mais os da 2.0): memória 80 MB em repouso, 150 MB com jogo, 400 MB de pico; CPU em repouso abaixo de 2%; CPU do tHg em jogo até 1,5% antes de o governador agir; primeira pintura abaixo de 1,5 s. A meta de memória em repouso já não era cumprida na 1.9 e continua não sendo.

**Overhead durante o jogo: não medido.**

## L. Release

Conferido depois da publicação, pela API do GitHub e por download.

| | |
|---|---|
| Branch de trabalho | `claude/lucid-archimedes-sx5om9` (11 commits sobre a 1.9.0) |
| Pull request | [#4](https://github.com/contatothiagodsp-sys/tg-projeto/pull/4), integrado por merge, sem reescrever histórico |
| Commit na `main` | `3ffdac7eeaf535c994e8d0497bf003a4ad8b18b9` |
| Workflow de publicação | Publicar nº 43311747864: todos os passos aprovados, 1.645 testes (1.642 aprovados, 0 reprovados, 3 ignorados) antes de publicar |
| Release | "tHg Optimization 2.0.0", publicada em 2026-11-12T21:39:36Z, não é rascunho nem pré-release |
| URL oficial | https://github.com/contatothiagodsp-sys/tg-projeto/releases/tag/latest |
| Tag | `latest`, apontando para `3ffdac7`. Não existe tag `v2.0.0`: o projeto publica pela `latest`, e uma tag de versão dispararia uma segunda publicação |
| Notas da release | Seção da 2.0 do `NOVIDADES.md` |
| Publicações | Uma. O push seguinte, só de documentação, roda apenas os testes |

**Artefatos.** O SHA-256 do log do build, o do `SHA256SUMS.txt` e o digest publicado pelo GitHub são iguais nos três arquivos.

| Arquivo | Bytes | SHA-256 | Authenticode |
|---|---:|---|---|
| `tHgOptimization-Setup.exe` | 61.117.510 | `106eea1c9487d651586b38afa1eb54f370582d4bd60882ec71d9b97ed2ab7135` | sem assinatura |
| `tHgOptimization-portatil.zip` | 86.413.944 | `b8b760259746b64457b986784daf8d8f66ed59b586e099552dcd9f9f5bee6fd7` | não se aplica |
| `tHgOptimization.exe` | 211.508.969 | `81197ecfde5125d3d2c4b56339fb61ac749a9fa5d3a503424ae6dda01742dc53` | sem assinatura |
| `SHA256SUMS.txt` | 276 | `f262b354429a88c30e9b9f2ad33444066bc6306511747dfa03bee97cab1b52f5` | |
| `tHgOptimization-sbom.json` | 85.137 | `fa6530b1ef61b6bc9213637247f72c1ed99a1dcf32e8b44d9eb1e3ee13419e9f` | |

Versão do executável: produto 2.0.0, arquivo 2.0.0.0.

**Conferência independente.** O instalador foi baixado da URL pública da release e o SHA-256 calculado localmente bate com o publicado. O SBOM baixado é CycloneDX 1.7, com 60 componentes.

**Proveniência.** O GitHub registrou uma atestação SLSA v1 para os três artefatos ([atestação 66905933](https://github.com/contatothiagodsp-sys/tg-projeto/attestations/66905933)), assinada pela instância pública do Sigstore e registrada no log de transparência Rekor. Lida pela API, ela declara o repositório `contatothiagodsp-sys/tg-projeto`, o workflow `.github/workflows/publicar.yml`, o ref `refs/heads/main` e o commit `3ffdac7`. **A verificação criptográfica com `gh attestation verify` não pôde ser concluída neste ambiente** (o verificador do Sigstore não inicializa atrás do proxy de saída). Num PC comum: `gh attestation verify tHgOptimization-Setup.exe -R contatothiagodsp-sys/tg-projeto`.

**Assinatura.** Nenhuma, por política. O Windows pode mostrar o aviso do SmartScreen. A atestação não muda isso.

**Não conferido depois de publicar:** instalar o instalador baixado da release (o teste real de instalação foi feito com o instalador do branch, de código idêntico mas outro build); a atualização automática 1.9.0 → 2.0.0 pelo próprio app.

## M. Limitações

1. **Nenhuma validação em jogo ou hardware real.** Limiares não calibrados; eficácia das ações não demonstrada.
2. GPU Busy e tempo de CPU por frame não são medidos (sem PresentMon).
3. Paginação, page faults, atividade de disco e context switches não são medidos durante a sessão.
4. O diagnóstico depende do Modo Jogo automático ligado. Jogo fora da biblioteca não gera sessão.
5. A sonda de rede não mede o servidor do jogo.
6. Revisão do catálogo de ajustes do Windows: não feita.
7. Revisão de dependências: nenhuma atualizada; versões novas não levantadas.
8. Performance Lab sem gráficos novos; overlay com duas linhas novas, sem redesenho.
9. Sem refatoração de `MainWindow`, `SensorMonitor`, `GameLauncher`, `AppUpdater` ou `CpuViewModel`. O código novo ficou fora deles.
10. Actions do workflow fixadas por versão maior, não por SHA de commit.
11. Releases sem assinatura Authenticode: o SmartScreen pode avisar.
12. A lista de perfis não mostra ícone do jogo.
13. O veredito estatístico novo vale para a medição de ações entre sessões; o A/B do Performance Lab usa o método da 1.9.

## N. Veredito

**APROVADO COM RESSALVAS.**

Aprovado porque os critérios verificáveis foram cumpridos: build Windows sem erros nem avisos; 1.642 testes aprovados e nenhum reprovado, incluindo os 1.502 da 1.9; nenhum teste desativado; janela real navegando todas as páginas e desenhando o Intelligence; instalação por cima da 1.9.0 com configurações preservadas; consumo equivalente ao da 1.9 no mesmo ambiente; overclock real desligado; release publicada uma vez, com hashes iguais nas três fontes e proveniência registrada.

Ressalvas, em ordem de importância:

1. **O objetivo central da versão, melhorar a experiência em jogos, não foi demonstrado.** Não houve teste em jogo nem em hardware real. O que foi entregue e testado é o mecanismo: diagnosticar, recomendar, aplicar com volta, medir e aprender. Se os limiares acertam em jogos de verdade, e se alguma ação traz ganho, só o uso real vai dizer.
2. Parte do pedido não foi feita: revisão do catálogo de ajustes do Windows, integração do PresentMon (GPU Busy), gráficos novos no Performance Lab, redesenho do overlay, revisão visual geral, refatoração das classes grandes, atualização de dependências, auditoria nova dos módulos de segurança. A seção M lista tudo.
3. O Intelligence depende do Modo Jogo automático, que vem desligado numa instalação nova. Sem ligá-lo, o usuário vê o cartão e as páginas, mas nenhum diagnóstico.
4. A CPU em repouso ficou um pouco acima da 1.9 em duas de três rodadas (0,80% contra 0,65% em média). Dentro da meta, sem certeza de que é ruído.
5. Release sem assinatura Authenticode; a verificação criptográfica da atestação não foi concluída neste ambiente.
6. A atualização automática pelo app e o uso manual da interface nova não foram testados.
