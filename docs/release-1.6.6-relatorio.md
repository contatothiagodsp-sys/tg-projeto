# Reengenharia de desempenho: 1.6.4 → 1.6.6

Partiu de `ad6127c` (1.6.4). Versões: `272b910` (1.6.5) e o commit da 1.6.6. Sem push e sem release.

## Testes

| | Antes (1.6.4) | Agora (1.6.6) |
|---|---|---|
| Testes aprovados | 432 de 432 | 658 de 658 |
| Warnings no build Debug | 0 | 0 |
| Warnings no build Release | não medido | 0 |

Foram 5 rodadas seguidas em Debug e 1 em Release sem falha. Uma versão anterior do teste de handles falhou uma vez por rodar em paralelo com os outros, e agora ele roda isolado.

Testes antigos com a expectativa alterada, porque a missão muda o comportamento:

- `Instalacao_nova_so_liga_com_12_threads_ou_mais`: `GameModeAvoidCpu0` agora é falso de fábrica.
- `Gate_constant_is_on_in_this_release` virou `..._is_off_...`.
- `Gate_lets_a_real_provider...` e os três `Amd_real_mode`/`Zen3_real_mode` usam `OcGate.AllowInTests()`.
- Três testes de erro de hardware esperam a restauração com `WaitForRollback`, porque ela agora é enfileirada.

## Benchmark medido

Só o caminho do evento de frame, em memória, sem ETW: código atual contra uma cópia do caminho da 1.6.4. Build Release, 3 milhões de eventos por execução, 3 execuções alternadas, 1 gravação ativa e 1 leitor a 20 Hz. Valores: mediana (mínimo a máximo).

Rodada 1:

| Caminho | ns por evento | P99,9 (ns) | Pior evento (µs) | Bytes alocados no total |
|---|---|---|---|---|
| 1.6.4 | 117,7 (113,5 a 157,5) | 500 (500 a 1400) | 4473 (2528 a 12540) | 96.002.048 |
| Atual | 40,1 (37,8 a 42,1) | 200 (200 a 200) | 498 (17 a 6131) | 7.480 |

Rodada 2:

| Caminho | ns por evento | P99,9 (ns) | Pior evento (µs) | Bytes alocados no total |
|---|---|---|---|---|
| 1.6.4 | 122,1 (71,5 a 146,4) | 1000 (1000 a 1400) | 10205 (2561 a 11397) | 96.006.536 |
| Atual | 41,0 (38,7 a 41,9) | 900 (900 a 900) | 29 (29 a 32) | 7.480 |

O que dá para afirmar:

- **Alocação:** de 32 bytes por evento para quase zero. O teste `Present_path_does_not_allocate` confirma 0 bytes em 10 mil eventos. A causa antiga era a lambda da limpeza, que capturava `now` e criava uma closure a cada frame.
- **Tempo médio do evento:** cerca de 3 vezes menor nas duas rodadas.
- **P99,9 e pior evento:** variam muito entre rodadas e sofrem preempção do sistema. A tendência é favorável, mas o número não serve como ganho garantido.

Para repetir: `$env:THG_BENCH="saida.md"; dotnet test tests\ThgOptimization.Tests -c Release --filter FramePathBenchmark`.

## Não medido

Nada abaixo foi medido, e nenhuma melhoria nesses itens é declarada:

- FPS, frametime, 1% low e DPC/ISR em jogo.
- Working Set, Private Bytes, GC, CPU, threads e handles do app aberto.
- Custo real por evento numa sessão ETW.

O app pede elevação e esta sessão não é elevada, então ele não foi aberto. A janela só foi montada a partir do XAML, no teste de fumaça.

## Pendente de validação física

1. Abrir o app como administrador e conferir as telas novas: Iluminação → Avançado, Overlay → perfis, Configurações → Diagnóstico.
2. `FrameMonitor` com sessão ETW real e jogo aberto.
3. Modo de apresentação com jogo aberto. Nunca coletou um evento real nesta sessão: os nomes de campo vêm do PresentMon, e a resposta só foi testada com contagens simuladas.
4. Modo Jogo de ponta a ponta: não foi executado porque grava em IFEO e muda plano de energia e prioridades da máquina. As partes têm testes separados.
5. CPUs de dois CCDs e híbridas: testadas com topologias simuladas.
6. OC: escrita real, TDR real e reversão. A trava continua fechada.

## Limitações conhecidas

- **Regras de RGB:** `ResolveOrder` aceita regras, mas o app não carrega nenhuma das configurações e não há tela para isso. Hoje a configuração é por código, e não há regra embutida porque nenhuma foi comprovada.
- **Topologia por CPUID:** só o fabricante vem de CPUID. Os CCDs vêm do domínio de L3 informado pelo Windows.
- **Vários grupos de processadores:** a topologia enxerga, mas o plano fica com o padrão do Windows.
- **Hardware Composed Independent Flip:** não há ligação entre planos ativos e o processo, por isso a confiança é sempre baixa.
- **DPC e ISR:** por CPU, sem dizer qual driver causou. O app não muda afinidade de interrupção.
- **Timer:** o `GameModeTimer` existente serve de flag e continua ligado de fábrica, como já era.
- **Catch vazio:** restam 264 no projeto, em código fora desta missão. Os caminhos críticos alterados registram a falha, mas a varredura geral não foi feita.
- **Fans:** sem leitura por mais de 15 s, a curva vai a 100%. É a proteção que já existia, mantida de propósito.
