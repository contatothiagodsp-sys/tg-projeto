# Release 1.7.0: relatório

Data: 2026-11-03. Base: v1.6.6 (`7b64e52`).

## Resumo

- **Todas as 45 tarefas foram aplicadas** (T76 a T115, mais T100.1, T100.2, T110.1, T110.2 e T110.3). Nenhuma ficou BLOCKED e nenhuma foi revertida.
- **Validação:** build Release do zero com 0 avisos e 0 erros, no app e nos testes. 830 testes verdes (eram 658), em 6 execuções seguidas.
- **`OcGate.RealHardwareEnabled` continua `false`** (`Core/Oc/OcCore.cs:34`).
- **O app não foi aberto.** Com o perfil automático de OC configurado, abrir o app poderia aplicar um limite real na GPU. Tudo foi verificado por testes com simulação e por renderização do XAML fora da janela.

## O que precisa de conferência física

| Item | O que conferir |
|---|---|
| **T79 RGB** | Conector endereçável ASUS, Gigabyte e ASRock pelo OpenRGB: o botão TESTAR CORES mostra vermelho, verde e azul nesta ordem? Ver "T79" abaixo. |
| T77 | Aplicar energia e teto numa GPU NVIDIA. Sem GPU NVIDIA aqui, a NVML real não rodou. |
| T85 | Failsafe de fans com hardware de verdade. |
| T80 e T89 | Modo Jogo de ponta a ponta, com jogo real. |
| T100 e T105 | Rolagem das listas com a roda do mouse, e busca rolando até o ajuste. A renderização não testa interação. |
| T103 | Narrator lendo os botões da janela. |
| T104 | Coleta do modo de apresentação com jogo real. |

A validação física pendente da 1.6.5 e da 1.6.6 também continua em aberto.

## Decisões que divergem do pedido, ou que têm consequência

**T79: risco principal desta release.** O pedido era "dispositivo ASUS via OpenRGB retorna GRB". A 1.6.3 tinha removido exatamente isso, porque o OpenRGB já converte na ponta do driver e GRB por cima troca os canais uma segunda vez. O teste `Maker_of_the_motherboard_never_implies_grb` garantia o oposto e foi substituído. Não consegui confirmar a afirmação "documentado" da auditoria. Para reduzir o risco:
- GRB vale só em conector endereçável (placa-mãe ou fita em zona linear). Teclado, mouse e RAM da mesma marca continuam RGB.
- A confiança é média e a tela mostra a origem ("regra ASUS").
- O botão TESTAR CORES aparece também em confiança média. O pedido era só abaixo de média, o que esconderia o botão justamente nesses casos.
- `LightAutoOrder` desligado e a escolha manual continuam funcionando.

O toggle "Avançado" que já existia foi mantido no lugar de um Expander: tem o mesmo efeito.

Se as cores saírem trocadas nos conectores endereçáveis, a correção é tirar as três regras de GRB de `ColorOrderAuto.Regras`.

**T76.** Fail-closed tem um custo: em placa cujo sensor não informa VSOC, nenhum ajuste de CPU AMD é aceito. É o que foi pedido, e `docs/oc-engenharia-reversa.md` já listava essa conferência como pendente.

**T77.** A NVML não informa o teto em vigor. A leitura de volta só prova que o clock atual não passou do teto: com a GPU parada, ela passa mesmo se o teto foi ignorado. O limite de energia é conferido de verdade. Em placa sem suporte a teto, a remoção que falha não invalida o ajuste de energia, se não havia teto aplicado pelo tHg.

**T77 e T78** ficaram no mesmo commit, porque a reversão do T77 usa `OcWatchdog.Disarm`. Fechar o app normalmente apaga o aviso: sem isso, os 30 min virariam um falso "travou".

**T78.** Qualquer desligamento inesperado nas últimas 24 h, inclusive queda de energia, desliga o perfil automático.

**T85.** CPU e bomba são identificadas pelo nome da saída (CPU, Pump, Bomba). Uma fan de CPU com nome genérico recebe 80%.

**T86.** A cópia reduz a janela da corrida e tenta de novo, mas quem altera as listas continua sem trava. A garantia total exigiria travar todos os pontos de alteração. O JSON nunca sai quebrado. Em caso extremo, a gravação lança erro, como já podia acontecer.

**T88 e aceite não atingido.** `GetStats` ainda aloca **280 bytes por leitura**, e não 0: `FrameStats` e `FrameStatistics` são classes. Chegar a zero exige transformá-las em struct, e isso ficou no backlog. `dotnet-counters` não foi rodado em T88, T89 e T91: as alocações foram medidas com `GC.GetAllocatedBytesForCurrentThread`.

**T89.** Para o Valorant, a busca continua pela lista de processos, porque `GetProcessById` abriria o processo do jogo e quebraria a regra Vanguard-safe. Para os demais, nada aloca até o jogo ser identificado.

**T90.** Cobre todos os `catch { }` dos 6 arquivos. `catch { return false; }` e parecidos não entram, e os catches vazios fora do caminho quente continuam: isso ficou no backlog.

**T100 e aceite parcial.** Medição em lista sintética de 63 itens, só layout, sem o app real:

| Página | Antes | Depois |
|---|---|---|
| PageOptimize | 71 ms | **16,6 ms** (meta: 16 ms) |
| PageClean | 55 ms | 10 ms |
| PageComp | 130 ms | 21 ms |

A meta fica no limite e não foi medida no app real. As listas ganharam rolagem própria, com altura máxima de 560 px, em vez da reorganização em Grid da página inteira: essas páginas têm vários cartões e várias listas. Lista curta não muda de aparência, e no fim da lista a roda do mouse segue para a página.

**T104.** `Win32_DeviceGuard`, sugerido na auditoria, fala de VBS e HVCI e não informa compressão de memória. O certo é o método `Get` da classe WMI `PS_MMAgent`, a mesma fonte do `Get-MMAgent`, conferida nesta máquina. O PowerShell ficou de reserva. Com o item do overlay ligado (padrão desligado), a coleta abre uma sessão de ETW de 5 s por minuto durante o jogo.

**T107.** Não há comando de escrita por núcleo documentado em `docs/oc-engenharia-reversa.md`. Por isso, com núcleos divergentes o ajuste é recusado, e a restauração usa a escrita de todos os núcleos e confere cada um. A máscara do núcleo vem da topologia do Windows, que não informa o índice do SMU: com núcleos desligados fora da ordem, a leitura falha e o ajuste é recusado.

**T109.** Das opções do pedido, escolhi deixar de oferecer o ajuste, em vez de condicioná-lo à taxa do mouse. Quem já aplicou continua vendo, para poder desfazer.

**T110.** A regra do pedido (peso 0 + `LaptopCaution`) marca `comp.hpet` e `comp.ulps`, mas não `clean.bgapps`, que o pedido cita. Os três foram marcados.

**T110.1.** `_ocClockCap` nunca foi salvo entre sessões, ao contrário do que o pedido supunha. Para o aceite fazer sentido, o teto agora fica guardado com o horário e vale só no boot atual. Um reinício do driver (TDR) também solta o teto, e isso não é detectado.

**T110.3.** Sem pin rígido no código, "Prender o jogo" deixa de ter efeito em X3D de dois CCDs. O texto da tela diz isso. O teste antigo garantia o oposto e foi trocado.

**T113.** A migração roda quando a GPU é conhecida. Uma troca de placa feita antes de atualizar passa despercebida uma vez.

## Testes que afirmavam o contrário e foram alterados

- `Maker_of_the_motherboard_never_implies_grb` (T79), substituído por testes das regras.
- `Resolve_is_always_rgb_because_openrgb_already_converts` (T79): o método foi removido.
- `Dual_ccd_x3d_without_the_option_leaves_the_game_free` (T110.3).
- `Vsoc_limit_applies_to_amd_cpu`, caso `null` (T76).
- `Value_limits` e `Amd_gpu_confirmation_never_passes_the_hard_limit`, casos abaixo de +100 (T108).
- `Stats_reader_does_not_block_the_writer`: falha intermitente anterior a esta release, porque o escritor do teste gira sem pausa e pode ultrapassar o leitor.

## Métricas

| Métrica | Antes | Depois |
|---|---|---|
| Testes | 658 | 830 |
| Avisos de build | 0 | 0 |
| `GetStats` | não medido | 280 bytes por leitura |
| Pedido ao PawnIO | 1 `byte[]` por IOCTL | 0 alocações |
| RAM e CPU do app | **não medidos** | **não medidos** (o app não foi aberto) |

Nenhum ganho de FPS foi medido.

## Tarefas aplicadas

| Hash | Tarefa |
|---|---|
| `1ed6c5e` | T76: OC fail-closed |
| `e651245` | T77 e T78: OC atômico com read-back, vigia de 30 min e detecção de BSOD |
| `8cb1e23` | T79: RGB auto-detection operacional |
| `3a3a6fd` | Estabiliza teste de leitura concorrente do FrameMonitor |
| `7528fa8` | T80: prioridade do jogo restaurada |
| `9c9d907` | T81: corrida entre ReportHardwareError e Stop |
| `e9ef617` | T82: HardwareMutex revalida o Dispose |
| `c9306a3` | T83: async void sem crash |
| `58d5334` | T84: timeout real no powercfg |
| `bccc5fd` | T85: failsafe das fans |
| `a3fc90a` | T86: Save grava uma cópia |
| `487bd65` | T87: aceite de risco por placa |
| `3a44322` | T88: FrameStats sem boxing |
| `a94f003` | T89: janela da frente sem listar processos |
| `8683811` | T90: catches do caminho quente |
| `22ab3dc` | T91: pedido ao PawnIO na pilha |
| `a15c098` | T92: contagens da topologia guardadas |
| `60b941d` | T93: CPU Sets usam a topologia já lida |
| `b6202f3` | T94: ADLX não recarrega depois de descartado |
| `7cadeec` | T95: privilégio não concedido vira erro claro |
| `182ef3f` | T96: token em SafeHandle |
| `5f93226` | T97: overlay sem lista compartilhada |
| `02195b9` | T98: prévia do RGB só troca o pincel com a cor |
| `9a57986` | T99: gradiente congelado |
| `8bef631` | T100: listas longas virtualizadas |
| `613a907` | T100.1: CpuInfo.Caches com ArrayPool |
| `5b375a8` | T100.2: contagem com Interlocked |
| `1ea1f3f` | T101: hero card no hover |
| `f7b2d85` | T102: chip com OnAccent |
| `569d3d2` | T103: nome acessível |
| `68f0b39` | T104: diagnóstico em Desempenho |
| `8f88c29` | T105: busca rola até o ajuste |
| `77ffbf3` | T106: ordem da página de RGB |
| `c1c4c26` | T107: Curve Optimizer por núcleo preservado |
| `809bb76` | T108: offset de GPU com passos pequenos |
| `d3b9801` | T109: filas de mouse não oferecidas |
| `75ce597` | T110: rótulo experimental |
| `bb277f6` | T110.1: chips de clock refletem o teto |
| `5a80084` | T110.2: HasPreferredCores confere o erro |
| `e6218e0` | T110.3: X3D prefere o V-Cache |
| `4d61e9c` | Reforços da revisão em T86 e T96 |
| `b392001` | T113: migração do aceite |
| `623d536` | T111: versão 1.7.0 |
| `a3604a8` | T114: novidades da 1.7 |
| `882ea0f` | T115: backlog da 1.8.0 |
| `4661903` | T112: changelog e novidades |

**Bloqueadas:** nenhuma. **Revertidas:** nenhuma.

## Push

`git push origin main`, sem force, no mesmo envio deste relatório. Na hora de gerar este arquivo, a origem não tinha commits novos.

**Primeira tentativa de publicação falhou:** o run 35 (`acd5c7b`) parou na etapa Testar, e nenhuma release saiu. Os testes novos e os da 1.6.5 e da 1.6.6 nunca tinham rodado no servidor do GitHub. Sem acesso ao log, não deu para confirmar qual teste quebrou. Reproduzindo com menos núcleos, apareceram falhas em testes que dependiam do tempo ou da máquina, e eles foram corrigidos num commit próprio. Nenhum código do app mudou nessa correção.

**Efeito do push:** o `publicar.yml` publica uma release a cada push na main, e o app instalado se atualiza sozinho. Este push leva juntos a 1.6.5 e a 1.6.6, que estavam só locais, sem validação física.

## git log

```
4661903 docs(1.7.0): T112 - changelog e novidades da 1.7.0
882ea0f docs(1.7.0): T115 - backlog da 1.8.0
a3604a8 feat(1.7.0): T114 - novidades da 1.7
623d536 chore(1.7.0): T111 - versão 1.7.0
b392001 feat(1.7.0): T113 - migração do aceite de risco do OC
4d61e9c fix(1.7.0): T86 e T96 - reforços da revisão
8bef631 perf(1.7.0): T100 - listas longas virtualizadas
8f88c29 feat(1.7.0): T105 - busca rola até o ajuste e o destaca
68f0b39 feat(1.7.0): T104 - diagnóstico na área Desempenho e coleta pelo overlay
bb277f6 fix(1.7.0): T110.1 - chips de clock mostram o teto em uso
c1c4c26 fix(1.7.0): T107 - Curve Optimizer por núcleo da BIOS é preservado
75ce597 fix(1.7.0): T110 - ajustes sem evidência marcados como experimentais
d3b9801 fix(1.7.0): T109 - filas menores de mouse deixam de ser oferecidas
809bb76 fix(1.7.0): T108 - offset de GPU aceita passos pequenos
77ffbf3 feat(1.7.0): T106 - ordem da página de RGB segue o fluxo
569d3d2 fix(1.7.0): T103 - nome acessível nos botões da janela
f7b2d85 fix(1.7.0): T102 - chip selecionado usa OnAccent
1ea1f3f fix(1.7.0): T101 - hero card mantém o gradiente no hover
e6218e0 feat(1.7.0): T110.3 - X3D de dois CCDs sempre prefere o V-Cache
5a80084 fix(1.7.0): T110.2 - HasPreferredCores confere o erro do Windows
5b375a8 fix(1.7.0): T100.2 - contagem de eventos com Interlocked
613a907 perf(1.7.0): T100.1 - CpuInfo.Caches com ArrayPool
9a57986 perf(1.7.0): T99 - gradiente do gráfico do painel congelado
02195b9 perf(1.7.0): T98 - prévia do RGB só troca o pincel com a cor
5f93226 fix(1.7.0): T97 - linhas do overlay sem lista compartilhada
182ef3f fix(1.7.0): T96 - token do processo em SafeHandle
7cadeec fix(1.7.0): T95 - privilégio não concedido vira erro claro
b6202f3 fix(1.7.0): T94 - ADLX não recarrega depois de descartado
60b941d perf(1.7.0): T93 - CPU Sets usam a topologia já lida
a15c098 perf(1.7.0): T92 - contagens da topologia guardadas
22ab3dc perf(1.7.0): T91 - pedido ao PawnIO na pilha
8683811 fix(1.7.0): T90 - catches vazios do caminho quente classificados
a94f003 fix(1.7.0): T89 - janela da frente sem listar todos os processos
3a44322 fix(1.7.0): T88 - FrameStats sem boxing no histórico recente
487bd65 fix(1.7.0): T87 - aceite de risco do OC vale por placa
a3fc90a fix(1.7.0): T86 - AppSettings.Save grava uma cópia imutável
bccc5fd fix(1.7.0): T85 - failsafe das fans sem leitura de sensor
58d5334 fix(1.7.0): T84 - timeout real no powercfg
c9306a3 fix(1.7.0): T83 - async void sem crash em diagnóstico e fans
e9ef617 fix(1.7.0): T82 - HardwareMutex.TryEnter revalida o Dispose
```

O commit deste relatório fica acima desses.

## Próximos passos

Ver `docs/backlog-1.8.0.md`. O primeiro é a conferência física da tabela do início, a começar pelas cores do T79.
