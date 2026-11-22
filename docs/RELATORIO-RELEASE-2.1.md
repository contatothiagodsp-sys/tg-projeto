# Relatório da release 2.1.0

Data: 2026-11-22. Base: `main` em `25940fe` (2.0.0 publicada).

## A. Resumo executivo

**Objetivo.** Elevar o nível técnico, visual e estatístico de todas as frentes do aplicativo (tHg Intelligence, Performance Lab, Histórico, Overlay, Ajustes, Busca Global e Aparência), atacando diretamente as limitações documentadas na release 2.0.0 sem abrir mão da arquitetura local, transparente e segura para anti-cheat.

**Entregue na 2.1.0.**

- **Telemetria de disco e paginação (`SessionIoProbe`):** coleta atividade física de disco (`MB/s`) e leituras de página em disco (`hard page faults/s`) via contadores nativos do Windows sem instalar serviços externos.
- **Diagnósticos mais refinados:** correlação de engasgos com picos reais de I/O de disco (`Storage`) e paginação (`MemoryPressure`), além da nova classe `ShaderStutter` para separar engasgos de aquecimento/compilação nos primeiros 4 minutos de problemas contínuos de *frame pacing*.
- **Índice de Saúde da Sessão (0–100) e Eficiência (`FPS/W`):** decomposição transparente em Fluidez (/40), Térmico (/25), Estabilidade (/20) e Recursos/Rede (/15), com graus A–D e exportação em relatório HTML visual offline e sanitizado (`IntelligenceHtmlReport`).
- **Aprendizado persistente e Perfis de jogos:** direção de mudança de regime (`HeldWorse`) persistida em `GameBaseline`, ícones reais dos jogos carregados fora da thread da janela (`GameIconCache`) e gráfico nativo de tendência multi-sessão (`SessionTrendChart`).
- **Catálogo de ações expandido:** adição de 3 ações reversíveis de classe B (`gamemode.timer`, `gamemode.cleanram`, `gamemode.overlay_econ`), mantendo classe D desabilitada e `OcGate.RealHardwareEnabled = false`.
- **Performance Lab e Histórico 2.1:** unificação estatística com intervalos de confiança de 95% por bootstrap e piso de ruído (MAD) em `AbComparison`, gráfico de distribuição P50/P95/P99/P99,9 (`FrametimeDistributionChart`), faixas extras no `SessionTimelineChart`, zoom direto ao clicar nos piores frames e comparador lado a lado entre sessões.
- **Overlay 2.1:** modo barra horizontal compacta, sparkline colorido por severidade (ciano/âmbar/vermelho), pill colorido de limitador ao vivo e métricas opcionais (`fps_per_watt`, `net_ping`, `disk_io`).
- **UX, Ajustes e Aparência:** tema **Obsidian (OLED)** de baixo brilho, badges de impacto/segurança nos ajustes (`WindowsTweaks`), central de comando no painel inicial (com pill de saúde e botão para habilitar Modo Jogo quando desligado), `SelfTick` reduzido para cada 30 s em repouso e busca global `Ctrl+K` com comandos rápidos e perfis de jogos.

## B. Segurança e compatibilidade

- **Compatibilidade retroativa garantida:** arquivos `settings.json`, `intelligence.json` e `.jsonl` de sessões das versões 1.9 e 2.0 abrem sem perda nem migração destrutiva; todas as novas propriedades são opcionais com padrões seguros.
- **Privacidade e XSS:** o relatório HTML (`IntelligenceHtmlReport`) aplica `WebUtility.HtmlEncode` em todos os campos dinâmicos e omite números de série e nome do computador.
- **Overclock e anti-cheat:** `OcGate.RealHardwareEnabled` permanece `false`. Nenhuma ação lê a memória de jogos ou injeta DLLs.

## C. Validação e limites do ambiente local

- **Compilação C# e XAML Pass 1:** validados via `dotnet build tHgOptimization.csproj -c Release` com **0 erros de compilação C#**, **0 avisos de nulidade (`WarningsAsErrors=nullable`)** e **0 erros de marcação XAML Pass 1**.
- **Limitação do ambiente local (Kaspersky):** nesta estação, o antivírus Kaspersky bloqueia o acesso à DLL intermediária recém-gerada na etapa `MarkupCompilePass2` (`error MC2000: Access to the path '...\tHgOptimization.dll' is denied`), impedindo a execução local da suíte `dotnet test` (inclusive na base limpa 2.0.0 antes de qualquer edição). A execução completa dos testes automatizados (incluindo os novos casos em `tests/ThgOptimization.Tests/Release210Tests.cs`) requer adicionar a pasta do repositório às exclusões do antivírus ou rodar no workflow de CI do GitHub Actions.
- **Validação em jogo real:** assim como na 2.0.0, os novos limiares (`DiskIoHighMbPerSec`, `PageFaultsHighPerSec`, `ShaderWarmupSeconds`) e o impacto real das ações continuam sujeitos à calibração em partidas reais com GPU dedicada.
