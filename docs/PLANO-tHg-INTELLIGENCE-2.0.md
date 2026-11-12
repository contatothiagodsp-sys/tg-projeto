# Plano: tHg Intelligence 2.0

Plano de implementação da versão 2.0.0. Escrito depois da inspeção do repositório na `main` em `6d4009a` (1.9.0 publicada).

## 1. Situação de partida (verificada no Git e no GitHub)

- A 1.9.0 **está publicada**: release `latest` com título "tHg Optimization 1.9.0", publicada em 2026-11-12, com `tHgOptimization-Setup.exe`, `tHgOptimization-portatil.zip` e `tHgOptimization.exe`. O relatório `docs/RELATORIO-RELEASE-1.9.md` na `main` não tem campos provisórios.
- As correções da 1.9 estão na `main` (merge `34d1b1c`, PR #2). O branch `claude/lucid-archimedes-sx5om9` existe e tem um commit a mais (só documentação), que segue junto com a 2.0.
- O projeto **não usa tags de versão**: a release oficial é a `latest`, recriada pelo `publicar.yml` quando o `<Version>` do `.csproj` muda. Uma tag `v*` dispararia uma segunda publicação; por isso nenhuma tag é criada.
- Não há certificado de assinatura. As releases são não assinadas por política (`docs/POLITICA-ASSINATURA.md`); a integridade é por SHA-256.

## 2. Arquitetura atual, e o que é reaproveitado

| Já existe | Onde | Papel na 2.0 |
|---|---|---|
| Detecção de jogo em primeiro plano, sem abrir a memória do processo | `GameModeEngine` (`GameMode.cs`) | Continua sendo a detecção. O controlador de sessão observa o resultado |
| Gravação de sessão: 1 amostra/s + todos os frames | `SessionRecorder`, `SessionStore` (`Core/Benchmark/SessionTimeline.cs`) | Fonte única da telemetria de sessão. Ganha colunas opcionais |
| Frames por ETW (DXGI/D3D9 Present) | `FrameMonitor` | Continua. Sem serviço externo |
| Estatística de frametime, critério de engasgo | `FrameStatistics`, `StutterClassifier` | Reaproveitados pelo diagnóstico |
| A/B com Mann-Whitney / t de Student | `FrameTimeStatistics`, `BenchLabFlow` | Continua no Performance Lab. A medição de ações usa bootstrap, entre sessões |
| Sensores (LHM, NVML, ADLX), portão de leitura | `SensorMonitor`, `SensorRefreshGate` | Um único leitor. Nada novo abre sensores |
| Topologia e planos de CPU Sets | `CpuTopologySnapshot`, `CpuSetPlanner` | Compatibilidade das ações de CPU |
| Ajustes com backup e restauração | `Tweak`, `AppSettings.TweakBackup`, opções do Modo Jogo | Alvos das ações. Nenhum segundo sistema de backup |
| Timers com pausa dos não essenciais | `TimerRegistry` | Redução do próprio tHg em jogo |
| Pasta protegida | `SecureFolder`, `AppSettings.Folder` | Onde os dados do Intelligence ficam |
| DI | `App.xaml.cs` (`Microsoft.Extensions.DependencyInjection`) | Registro dos serviços novos |

O que **não** existia: diagnóstico com confiança e evidências, baseline por jogo, aprendizado, catálogo de ações com transação e conflito, máquina de estados de sessão, medição automática de ações, inventário com estados de suporte, diagnóstico de rede durante a partida.

## 3. Módulos novos

`Core/Intelligence` (lógica pura, sem WPF e sem API do Windows; testável em qualquer lugar):

| Arquivo | Responsabilidade |
|---|---|
| `Telemetry.cs` | Modelo de telemetria, estados de leitura (`Supported`/`Unsupported`/`Unavailable`/`Stale`), contexto e assinatura da sessão, qualidade dos dados |
| `RobustStats.cs` | Mediana, MAD, Theil–Sen, Spearman, bootstrap determinístico |
| `Diagnosis.cs`, `DiagnosisEngine.cs` | 17 classes de diagnóstico, confiança, evidências, limitações, "verificado e sem problema" |
| `Learning.cs` | Resumo de sessão, baseline por condição, outliers, mudança de regime |
| `Experiment.cs` | Avaliação antes/depois com intervalo de confiança e piso de ruído |
| `Actions.cs` | Contrato de ação, classes de risco A–D, política de automação, executor transacional, conflito |
| `Recommendations.cs` | Perfis de uso, catálogo de ids, destinos fechados, recomendações explicáveis |
| `GameSessionController.cs` | Estados Idle/Detecting/Preparing/Gaming/Capturing/Diagnosing/Restoring/Error |
| `MachineProfile.cs` | Inventário com estado por item e "pontos cegos" |
| `IntelligenceStore.cs` | Persistência local, limites, retenção, exportar/importar/apagar |
| `IntelligenceOrchestrator.cs` | O fluxo inteiro; relatório de sessão; governador do consumo do próprio tHg |

`Infrastructure/Intelligence` (ligação com o app): adaptador `SessionTimeline → SessionTelemetry`, construtor do inventário, catálogo de ações sobre as opções existentes, sonda de rede da sessão, serviço que junta tudo.

Interface: área **Intelligence** (Diagnóstico, Perfis de jogos, Meu PC), cartão no painel inicial, linha no overlay, primeiro uso.

## 4. Decisões técnicas

- **IA local, sem modelo treinado.** Não existe dataset rotulado de sessões deste app. Um modelo sem dados seria teatro. O motor é de regras com evidência, estatística robusta e baseline individual. ONNX Runtime/Windows ML foram avaliados e ficam de fora: acrescentariam dezenas de MB a um executável de 211 MB sem um modelo que os justifique.
- **PresentMon.** O serviço do PresentMon daria GPU Busy e tempo de CPU por frame, mas precisa ser instalado como serviço do Windows. O tHg não instala serviços em silêncio e a 2.0 não o redistribui. A captura de frames segue pelo ETW próprio. GPU Busy fica declarado como indisponível, e a confiança dos diagnósticos de gargalo tem teto por isso.
- **Ações do motor = opções que o app já tem e já restaura.** O catálogo inicial usa as opções do Modo Jogo (vigentes só durante o jogo). Ajustes de registro, drivers e hardware continuam nas páginas próprias, por decisão do usuário. Sem validação em hardware real, nada sensível é aplicado sozinho.
- **Preferência de GPU por aplicativo**: o motor orienta e abre a tela do Windows; não grava a preferência.
- **`AppSettings`**: só campos novos com valor padrão (compatível com 1.8.1 e 1.9.0, sem migração destrutiva). Os dados do Intelligence ficam em arquivo próprio, versionado.
- **`SessionSample`**: colunas novas opcionais. Sessões antigas continuam abrindo.

## 5. Ordem de implementação

1. Núcleo puro e testes (telemetria, estatística, diagnóstico, aprendizado, ações, experimento, controlador, persistência).
2. Ligação com o app: adaptador de sessão, inventário, catálogo de ações, sonda de rede, serviço, DI, campos de configuração.
3. Modo Jogo: amostras novas (núcleo mais ocupado, commit), programas que seguraram a CPU, controlador de sessão no tique.
4. Interface: área Intelligence, perfis de jogos, cartão do painel, overlay, relatório de sessão, primeiro uso.
5. Testes instáveis da 1.9 (causa e correção).
6. CI: SHA-256 publicado, SBOM, atestação de proveniência.
7. Versão 2.0.0, changelog, novidades, relatório.
8. Validação no Windows (CI), merge na `main`, release, conferência.

## 6. Testes

- Lógica do motor com sessões sintéticas: dados completos, incompletos, sensores ausentes, amostras pequenas, baseline inexistente e contaminado, mudança de driver/hardware, reaprendizado.
- Cada diagnóstico, com o caso positivo e o caso em que a aparência engana (GPU a 99% por pouco tempo, temperatura alta sem queda de clock, RAM cheia sem pressão, ping sem culpa no FPS).
- Controlador de sessão: início, fim, troca de jogo, falso positivo, sumiço do processo, falha em cada etapa, app fechando, concorrência.
- Executor: pré-condições, falha parcial, restauração, conflito externo, tempo esgotado, cancelamento, idempotência.
- Avaliação A/B: iguais, melhora, regressão, ruído, outlier, poucas amostras, condições incompatíveis, dados ausentes.
- Compatibilidade de arquivos antigos de sessão e de configurações.
- Nenhum teste novo depende de GPU, sensor ou jogo real. Os de hardware continuam separados e opt-in.

## 7. Critérios de aceite

Build Windows Release sem erros e sem avisos novos; suíte completa aprovada no Windows; nenhum teste desativado; `OcGate.RealHardwareEnabled = false`; nenhuma ação fora do catálogo fechado; nenhuma alteração aplicada sem registro e sem caminho de volta; todo controle da interface ligado a função real; release 2.0.0 com hashes conferidos.

## 8. Impacto estimado no consumo

- Em repouso: nenhum trabalho novo periódico. O inventário é montado sob demanda.
- Em jogo: duas leituras a mais por segundo, já dentro do laço que percorre os sensores (núcleo mais ocupado) e uma chamada `GlobalMemoryStatusEx`; uma sonda ICMP a cada 5 s (opcional); a leitura do próprio consumo a cada 10 s.
- Fim da sessão: o diagnóstico roda uma vez, fora da thread da janela.
- A medição real fica com o workflow `medir.yml`, comparada com a 1.9.

## 9. O que depende de hardware real

Não há PC com GPU e jogos neste ambiente. Ficam sem validação em jogo real, e por isso em modo de diagnóstico e recomendação: os limiares das regras, a eficácia de cada ação, o overhead durante uma partida de verdade, a leitura de sensores em Intel Arc e em notebooks, e o comportamento com anti-cheats. O relatório final lista isso como limitação, sem números inventados.

## 10. Riscos

| Risco | Mitigação |
|---|---|
| Limiar errado gera diagnóstico errado | Confiança explícita, limiares num só lugar, texto de associação e não de causa, "inconclusivo" como resposta |
| Ação piora o jogo | Só opções reversíveis; medição automática; desfaz em regressão ou sem benefício |
| Outro programa muda o valor depois | Detecção de conflito: não sobrescreve |
| Arquivo de dados corrompido | Guardado ao lado; o app segue com dados vazios |
| Erro de XAML não detectável fora do Windows | Testes de fumaça de XAML e de janela no CI do Windows antes do merge |
