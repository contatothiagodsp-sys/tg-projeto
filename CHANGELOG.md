# Changelog

Histórico completo das versões anteriores em [NOVIDADES.md](NOVIDADES.md).

## [1.6.6] - 2026-11-03

### Overclock (`Core/Oc`), ainda sem tela nem chamada no app
- **`OcGate.RealHardwareEnabled` voltou a `false`.** Provedores e transportes conferem a trava, e o simulado continua disponível. O que falta para abrir está em `OcGate.PendingCriteria`.
- `Restore` só diz "restaurado" depois de ler o valor de volta, e sem ajuste anterior não grava nada. Quando desfazer uma tentativa falha, a mensagem diz.
- Vigia: o evento de hardware só marca e enfileira uma restauração, sem trava presa durante a espera pelo driver, com 3 tentativas, reabertura após TDR, resultado em `LastRollback` e encerramento com espera limitada.
- `HardwareMutex`: só a thread dona solta, soltar duas vezes não derruba o app, e `Dispose` e mutex abandonado têm tratamento.
- ADLX: slots conferidos com os headers oficiais, sem erro. Corrigidos o vazamento de referência no `QueryInterface`, a falta de reabertura em leitura após TDR, o estouro na soma do offset e o carregamento sem conferir a assinatura da DLL.
- `HardwareErrorMonitor`: remove a assinatura ao descartar, e um assinante com erro não atrapalha os outros.

**Nenhuma escrita foi executada em hardware.** Os testes usam simulação, e o ADLX é falso, em memória.

### Fans
- `FanControlEngine`, `FanSensorReader`, `FanOutputController` e `FanPageViewModel`: `Start` idempotente, `StopAsync` com espera real, nunca dois laços ao mesmo tempo, e nenhum comando às saídas depois do cancelamento.
- Leitura perdida por um instante mantém a última válida por 15 s. Passado isso, vale a proteção de sempre (100%).

### Consumo do app
- Sessão ETW só com quem usa o resultado: Modo Jogo sem jogo aberto não mantém mais a sessão ligada.
- Na bandeja, sem uso, o timer da janela bate a cada 5 s em vez de 1 s.
- Overlay não consulta o driver de vídeo nem percorre os sensores se nenhum item escolhido depende disso.
- Timer do teclado AULA: uma leitura da lista do kernel a cada 3 s, sem objetos `Process` por nome.
- Modo Jogo libera os handles do processo do jogo e da janela anterior. Uma etapa que falha no fim da sessão não impede as outras.

### Overlay e diagnóstico
- Novos itens opcionais: 0,1% low, P99, P99,9, engasgos por minuto, pressão de memória, modo de apresentação e estado do ETW.
- Perfis ECONOMIA, EQUILIBRADO (o ritmo de antes) e DETALHADO. Mudam a tela, não a coleta.
- Configurações → Diagnóstico, só leitura e sob demanda: topologia, pressão de memória, compressão e Page Combining, DPC e ISR por CPU, adaptadores de rede, latência até o roteador e modo de apresentação do jogo.

### Corrigido
- O evento de frame alocava 32 bytes por frame na 1.6.4: a limpeza de processos capturava uma variável numa lambda. Corrigido na 1.6.5, medido nesta.

## [1.6.5] - 2026-11-03

### Iluminação
- Ordem das cores identificada por aparelho e por zona: contrato da ponte, metadados e regras comprovadas, com confiança e motivo na linha. Fabricante da placa-mãe não implica GRB. Nenhuma regra embutida: as cinco pontes recebem RGB e convertem por conta própria.
- A escolha manual saiu da linha e foi para Avançado. Escolhas antigas continuam valendo (`SettingsVersion` 3).
- `LightPump`: dois buffers fixos por aparelho, sem alocar por quadro. Encerramento espera a thread, `Post` depois do fim é recusado, e um quadro atrasado não retoma as luzes depois de devolvê-las. O pump nunca era descartado.
- Limite de atualização configurável, com teto de 30 por segundo.

### Modo Jogo
- `CpuTopologySnapshot` e `CpuSetPlanner`: plano de CPU pela topologia real (CCDs, L3, SMT, núcleos P e E, grupos), sem olhar nome de CPU nem jogo.
- CCD único: o jogo não recebe restrição, e o processo nem é aberto para isso. O CS2 não perde mais o núcleo 0 à força.
- `GameModeAvoidCpu0` agora é só escolha do usuário. Quem tinha o valor ligado com 12 threads ou mais volta a desligado, porque ele podia ser só o padrão antigo. A opção continua na tela.
- Programas de fundo e clientes da Riot por CPU Sets, sem afinidade rígida como reserva. Se o Windows recusa, o processo fica como está.
- `ITimerResolutionLease`: consulta os limites, respeita o NTSTATUS, um pedido por sessão e uma devolução. Nenhum ganho de FPS é prometido.

### Frames
- O evento de frame não usa mais trava nem aloca: um escritor e leitores sem bloqueio. Limpeza de processos saiu do evento, para uma rotina a cada 30 s.
- Sessão ETW só com as keywords de Present (antes, todas). `PresentMultiplaneOverlay_Start` agora também conta como frame.
- Novas métricas por janela e por gravação: P50, P90, P95, P99, P99,9, FPS instantâneo, histograma e os dois tipos de low, com nomes separados. Percentil pelo posto mais próximo.
- Gravação com buffer fixo, que avisa quando frames ficam de fora, e reserva pelo FPS medido se ele for maior que o esperado.
- `StutterClassifier`: frametime elevado, pico isolado, recorrente, oscilação, queda de FPS e possível pressão de memória, com critérios configuráveis. Sem medida da condição, a resposta é "causa não identificada".
- `IPresentationModeMonitor`: amostra sob demanda do modo de apresentação, com ids e campos do PresentMon. **Só testado com contagens simuladas e numa sessão real sem jogo: falta conferir com um jogo aberto.**

### Testes
- O registro dos testes não vai mais para o `thg.log` real, e `AppLog.UseFile` não sofre corrida com outros testes.

## [1.6.4] - 2026-11-03

### Overclock (`Core/Oc`), ainda sem tela nem chamada no app
- GPU AMD passa a trabalhar em MHz: 0 ou de +100 a +250, de +251 a +300 só com confirmação explícita, nunca acima de +300. Offsets negativos são recusados.
- `AmdAdlxOverdriveTransport` substitui o transporte por ADL. Com o Auto Tuning do Adrenalin ligado, recusa em vez de voltar a placa ao padrão de fábrica.
- `HardwareErrorMonitor`: erro WHEA ou TDR desfaz o ajuste na hora em que o evento chega, mesmo depois da confirmação do usuário.
- Vigia tenta desfazer 3 vezes, porque o driver leva um tempo para voltar de um TDR, e avisa quando não consegue.

**Nenhuma escrita foi executada na GPU.** Só as leituras foram conferidas numa RX 9070 XT.

## [1.6.3] - 2026-11-03

Reúne as correções planejadas para a 1.6.1, a 1.6.2 e a 1.6.3.

### Corrigido
- Engasgos causados pelo próprio app: `MemoryRelease.Trim` só compacta o heap do .NET e não força mais as páginas para o disco.
- O OTIMIZA só limpa a memória em espera. A RAM dos programas abertos não é mais tocada (`EmptyWorkingSet` saiu do app).
- Iluminação: fim da troca dupla de canais. O padrão é sempre RGB, porque o OpenRGB já converte na ponta do driver. Ordens escolhidas à mão continuam valendo.
- Handles: chaves de registro do DirectX fechadas na hora, e os objetos WMI liberados assim que o dado é lido, em todo o app.
- `UpdateRecentErrors` e `BuildOcGuide` registram a falha em vez de derrubar o app.

### Alterado
- Curvas de fan em `FanEngine`: um `PeriodicTimer` fora da janela lê as temperaturas, calcula e aplica. A janela só mostra o resultado, com a página de fans aberta.
- Com a página de fans fechada, a placa-mãe não é lida. O app na bandeja lê os sensores a cada 10 s, mesmo com curvas ativas.
- Página de iluminação: Controle Global e Dispositivos Conectados em cartões, com a ordem das cores sempre visível em cada aparelho.
- Efeitos de RGB: a cor é calculada fora do laço de LEDs e a ordem dos canais é resolvida uma vez.
- Menos alocação por quadro: `GetStats`, `CpuClocks`, `BackgroundCores` e `ClientDemoter`. O gráfico do overlay usa pontos congelados.

### Overclock (`Core/Oc`)
- `OcGate.RealHardwareEnabled = true`. **O app ainda não tem tela nem chamada para esses provedores, então nada muda para o usuário.** Não houve teste de escrita em hardware real.
- Transporte real: `PawnIoTransport` (SMU e MSR) e `HardwareMutex` (`Global\Access_PCI`).
- NVML: `nvmlDeviceSetClockOffsets`/`GetClockOffsets`, com a API antiga para driver sem elas.
- Vigia com `StartBackgroundMonitor` e `StopBackgroundMonitor`.
- Limites: VSOC de até 1,25 V na CPU AMD, 1,35 V na CPU Intel, NVIDIA em 0 ou de +100 a +300 MHz.
- Margem real lida do SMU no Zen 4 e no Zen 5. O Zen 3 recusa o ajuste, porque não tem leitura simples.

### Limitações conhecidas
- O módulo `IntelMSR` da LibreHardwareMonitorLib 0.9.6 só lê: o undervolt Intel falha sem aplicar nada até haver um módulo com escrita.

## [1.6.0] - 2026-11-03

### Adicionado
- Busca global (Ctrl+K) acha cada ajuste pelo nome, com ou sem acento.
- Badge APLICADO nos ajustes e contador de aplicados em Ajustes e Limpeza.
- Página Overclock: análise do setup (só leitura) e aviso de risco e garantia antes do primeiro ajuste manual.
- Iluminação: ordem de cores automática por fabricante em instalações novas, com a escolha manual no modo Avançado.
- Ajustes opcionais, fora do TURBO: `InterruptSteeringDisabled` e filas de mouse e teclado. Efeito não comprovado.
- TURBO: item informativo sobre Advanced Shader Delivery.
- SHIELD: lista de bloqueio de drivers vulneráveis e aviso sobre WinRing0.
- Configurações: painel de memória do app. Registro local com rotação (`thg.log`, nada é enviado).
- Janela "O que há de novo", uma vez por versão Major.Minor.
- Migração de configurações com número de versão no arquivo.

### Experimental (desligado nesta versão)
- `Core/Oc`: provedores de CPU AMD, CPU Intel, GPU NVIDIA e GPU AMD, limites rígidos e vigia de reversão.
  **Testado só com simulação. O app não chama esse código e nenhum build aplica overclock por ele** (`OcGate.RealHardwareEnabled = false`).
  Falta o transporte real do SMU e do MSR, a ligação com o ADLX e o teste em hardware. Fontes em [docs/oc-engenharia-reversa.md](docs/oc-engenharia-reversa.md).

### Alterado
- O app, o instalador e o título da release mostram só a versão `Major.Minor` (1.6). O terceiro número continua subindo por dentro, porque o auto-update depende dele, e aparece nas dicas, nos diagnósticos e nos relatórios de falha.
- Core parking pesa 1 no TURBO (era 3), com texto mais honesto. Textos de MMCSS e HAGS revistos.
- Ajustes de peso 0 desligados aparecem como informação no TURBO, não como problema.
- Memória devolvida ao Windows com a janela escondida há 30 s, fora de jogo e teste, no máximo a cada 10 min.
- Gargalo não coleta com a página fechada. Efeitos de RGB não alocam a cada quadro. Página OC lê registro de eventos e WMI fora da janela.

### Corrigido
- Byte NUL literal em `WindowsTweaks.cs`, que fazia o git e o ripgrep tratarem o arquivo como binário.
- Chips de teto de clock: estouro de `uint` quando o driver informa um clock máximo muito baixo.
- Carga de hardware protegida contra segunda execução, que duplicaria chips e motores de fundo.
- Cópia de ajuste ilegível no arquivo de configurações não derruba mais o ajuste.
- Handle de processo não liberado ao devolver memória.
