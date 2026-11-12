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

@@TESTES@@

**Testes instáveis da 1.9.** `FanLifecycleTests.Sensor_exception_does_not_stop_the_loop` e `OcProviderTests.Hardware_error_rolls_back_at_once…` falharam uma vez cada. Nos dois, o código sob teste precisa de uma thread do pool dentro de 5 s, enquanto outras classes seguram threads em paralelo. O mínimo do pool nos testes passou a 64. Os testes e os prazos não mudaram. A falha não foi reproduzida sob demanda, então a causa é a explicação compatível, não uma prova; @@FLAKY@@

**Não executado.**

- Jogo real, em qualquer cenário (GPU-bound, CPU-bound, competitivo, pesado, com stutter).
- Hardware real: sensores, NVML/ADLX, Intel Arc, notebook, CPU híbrida.
- Anti-cheat real.
- Uso manual da interface nova por uma pessoa, leitor de tela, DPI alto, múltiplos monitores.
- Atualização automática de ponta a ponta pelo app (o instalador por cima foi testado; o download pelo atualizador, não).
- Windows 10.

## K. Performance do aplicativo

@@MEDICAO@@

**Orçamentos** (de `docs/auditoria-consumo.md`, mais os da 2.0): memória 80 MB em repouso, 150 MB com jogo, 400 MB de pico; CPU em repouso abaixo de 2%; CPU do tHg em jogo até 1,5% antes de o governador agir; primeira pintura abaixo de 1,5 s. A meta de memória em repouso já não era cumprida na 1.9 e continua não sendo.

**Overhead durante o jogo: não medido.**

## L. Release

@@RELEASE@@

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

@@VEREDITO@@
