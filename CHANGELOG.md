# Changelog

Histórico completo das versões anteriores em [NOVIDADES.md](NOVIDADES.md).

## [1.8.1] - 2026-11-03

### Desempenho
- **Menos memória com o app escondido:** a compactação do heap agora devolve ao Windows a memória livre do coletor, sem tirar da RAM nada que esteja em uso. No mesmo teste, o app escondido caiu de 217 para 173 MB de memória privada (44 MB a menos), com pausa menor.

## [1.8.0] - 2026-11-03

Build e testes verdes. A janela real foi aberta e navegada por um teste em 26 páginas, sem exceções, com o tHg 1.7.7 aberto ao lado e configurações numa pasta temporária. Fans, Telas LCD, Iluminação, Gargalo, Overlay, RESET MAKER e USB não foram abertas, para não disputar hardware com o app instalado, e nenhuma sessão de jogo real foi gravada. Detalhes em [docs/auditoria-sensores.md](docs/auditoria-sensores.md) e [docs/auditoria-consumo.md](docs/auditoria-consumo.md).

### Sensores
- **Página nova:** favoritos com gráfico, filtros por peça, tipo e estado, busca sem acentos e grupos recolhíveis (T1).
- **Abertura registrada:** quanto a detecção levou e o que foi lido ficam em `thg.log`, na área `[sensores]`. Sem sensores, a página diz o motivo e oferece RODAR DIAGNÓSTICO (T0).
- **Unidades corrigidas:** rede em Mbit/s, e não MB/s; disco em B/s, KB/s ou MB/s; Dados em GB separados de Dados em MB; temporização e multiplicador com unidade (T2).
- **Nível (Level):** valor de 0 a 1 agora aparece em porcentagem. Disco e bateria ficam como estão, porque a biblioteca já os entrega de 0 a 100 (T2).

### Consumo
- O painel não redesenha com o jogo em primeiro plano, a dica da bandeja não repete o que a página mostra, e a leitura só da placa-mãe não refaz os valores principais (T3).
- **MEDIR AGORA (10 s)** em Configurações (T3).
- **O QUE O APP ESTÁ FAZENDO:** cada tarefa repetitiva, com ritmo e estado, e PAUSAR TUDO QUE NÃO FOR ESSENCIAL (T19).
- Cartões-fantasma enquanto o hardware carrega, e tempos da abertura no registro (T20).

### Desempenho em jogos
- **Timer de 0,5 ms por jogo:** depois de 3 sessões, o tHg oferece rodar uma sem o timer e compara. Sem ganho medido, desliga para aquele jogo (T17).
- **Integridade de Memória:** impacto estimado pelos testes publicados, conforme o que a CPU tem no hardware, com MEDIR IMPACTO antes e depois do reinício. O tHg nunca desliga a proteção (T12).
- **MSI AM5:** aviso sobre a opção Microsoft Security Level 2, dizendo que a perda de FPS não está comprovada (T13).
- **X3D de dois CCDs:** confere otimizador, plano e Modo de Jogo, com aviso ao abrir o jogo (T14).
- **Agendamento de GPU:** recomendação por VRAM e CPU, com medição. "Corrigir tudo" não liga mais quando a recomendação para a placa é o contrário (T15).
- **Memória:** guia por plataforma e VALIDAR EXPO/XMP (15 min) (T16).
- **Prioridade do agendador e da GPU:** só guia e medição. Aplicar pede confirmação e guarda o valor anterior (T18).

### Novo
- **Histórico:** frametime, temperaturas e clocks de cada sessão no mesmo eixo, com engasgos marcados, os 5 piores frames, zoom, CSV no formato do PresentMon e PNG (T7).
- **Pacote de diagnóstico:** um ZIP para pedir ajuda, sem o seu nome, sem chaves e sem caminhos de programas. Nada é enviado (T8).
- **Editor visual da curva de fan**, com TESTAR 10 s e o texto em Avançado (T9).
- **Overlay por jogo:** layout próprio, sem mexer na escolha geral (T10).
- **Referência no BENCH:** compara sua nota com a medida na mesma peça e copia em Markdown. Por enquanto há só as medições que o projeto documenta (RX 9070, RTX 5070 e Ryzen 7 7700X, cada uma em um PC). Sem medição para a peça, nada aparece (T11).
- DESFAZER no aviso de restaurar a curva de fan, Ctrl+Shift+C copia o cartão em Markdown, e as sessões ganham título com a data (P1 a P3).

### Testes
- Toda página é montada e faz layout a partir do XAML real, a versão não pode ficar escrita no código, e todo registro precisa de área (P4 a P6).
- Teste manual que abre a janela real, com as configurações numa pasta temporária.
- Testes de alocação e de sparkline deixam de depender de outros testes em paralelo.

## [1.7.7] - 2026-11-03

Auditoria da 1.7.6. Build e testes verdes. O app não foi aberto com estas mudanças antes de publicar: detalhes em [docs/auditoria-1.7.6.md](docs/auditoria-1.7.6.md).

### Segurança
- **Limpeza do Windows não atravessa mais atalhos de pasta:** se uma pasta de cache, ou o caminho até ela, for uma junction ou um link simbólico, ela é ignorada, com aviso na tela e no registro. Antes, a limpeza como administrador apagava o que estava do outro lado do atalho.

### Corrigido
- **Botões não ficam mais travados depois de uma falha**, e a falha não derruba o app: limpeza, cache de shaders, teste de engasgos, DDU, Telas LCD, iluminação, benchmark de ray tracing e verificador de OC.
- **Configurações:** ler histórico e listas enquanto o Modo Jogo grava não causa mais erro. Um `settings.json` com uma lista em `null` não trava mais a abertura nem a migração de versões antigas.
- **Diagnósticos não travam mais** quando um programa filho deixa a saída aberta: o prazo agora vale até o fim.
- **Reset de OC:** diz separadamente o resultado de energia e de clock, e o aviso de travamento só é desarmado se os dois voltaram ao padrão.
- **Ao fechar o app:** todos os timers param antes de os subsistemas desligarem.
- **Avisos e diálogos:** o aviso volta ao canto certo mesmo já visível, e o diálogo não escolhe mais o overlay ou uma janela invisível como dona.
- Ícone da bandeja não destrói mais o ícone compartilhado do Windows. Overlay e aviso usam as funções de estilo de janela de 64 bits.

### Desempenho
- **Painel:** o gráfico e os anéis reaproveitam os elementos a cada segundo, em vez de recriá-los.
- **Prévia do LCD:** reaproveita o bitmap, em vez de alocar cerca de 9 MB/s com um GIF de 480×480.
- **Sensores:** overlay e página do processador percorrem a lista uma vez só. Leituras simultâneas não aplicam mais fora de ordem.
- **Performance Lab:** a mediana não copia mais os valores para o heap.
- Verificador de OC lê o registro de eventos fora da janela, e o vigia de OC não prende mais uma thread entre as tentativas de desfazer.

## [1.7.6] - 2026-11-03

### Segurança
- **Pastas protegidas não aceitam mais proteção pela metade:** se o Windows não confirmar a dona e as regras de cada nível, de `%ProgramData%\tHgOptimization` até a pasta pedida, a operação é recusada e o motivo vai para o registro. Antes, o tHg criava a pasta e seguia sem proteção. Sem a pasta das configurações, o app avisa e fecha sem alterar nada.
- Pasta nova já nasce com as regras certas, sem o intervalo em que um programa comum podia escrever nela.

## [1.7.5] - 2026-11-03

Correções de hardware, instalador silencioso de drivers e correções da auditoria. Build e testes verdes. Nada foi conferido em hardware real: hub Lian Li, ordem das cores, RECONECTAR e INSTALAR TUDO ainda dependem de uso de verdade.

### Adicionado
- **INSTALAR TUDO na página Driver:** baixa e instala sem janelas os drivers novos de NVIDIA e AMD, com progresso e cancelamento. Só executa arquivo assinado pelo fabricante daquele driver. Depois de confirmar, cria um ponto de restauração.
- **RECONECTAR na página Telas LCD:** solta as portas COM e reenvia a imagem sem reiniciar o app. Quando outro programa prende a porta, o status e um aviso dizem qual fechar.
- **Hubs Lian Li UNI FAN (0CF2:A102 a A105)** por USB direto, sem o OpenRGB. Experimental: o protocolo não foi medido em um hub real.
- **Reserva nativa de clock e carga da CPU**, para quando a biblioteca de sensores não entrega esses valores.
- **Aviso no topo da aba Sensores** quando o driver PawnIO não está ativo ou quando registradores não respondem.

### Alterado
- **Ordem das cores:** GRB passa a ser o padrão em conector endereçável (placa-mãe, fita e cooler, em qualquer tipo de zona) e em ponte que grava os canais direto. O TESTAR CORES confere, o modo Avançado corrige, e desligar a ordem automática volta a RGB.
- **Página de iluminação:** Controle Global primeiro, depois Dispositivos, e Integrações recolhidas no fim da página.
- Clock e Carga sem leitura válida aparecem como "Indisponível", em vez de um valor vazio.

### Corrigido
- Falha em um sensor ou chip não interrompe mais a leitura dos outros.
- Abrir a porta da tela LCD tenta 3 vezes e nunca deixa o handle aberto depois de uma falha.
- Modo Jogo não mantém handles de programas de fundo durante a partida, e confere o nome do processo antes de restaurar, porque o PID pode ter sido reaproveitado.
- Gravar as configurações não cruza mais com alterações em outra thread: toda alteração de coleção usa a mesma trava do Save.
- Limpar o cache de shaders confere cada subpasta antes de apagar, e nunca atravessa um atalho de pasta (junction).

## [1.7.4] - 2026-11-03

### Alterado
- **Um único número de versão.** O `<Version>` do `.csproj` é o que aparece no app, no instalador, nas propriedades do `.exe`, nos relatórios de falha e na release. O contador do CI e a exibição só de Major.Minor deixaram de existir.
- Push sem mudar a versão só roda os testes, e não gera release. Para publicar, mude a versão e crie a seção dela neste arquivo.

### Transição
- As compilações antigas, numeradas pelo CI até 1.7.37, são numericamente maiores que 1.7.4. O título da release leva um número de transição que só elas leem, e ele sai sozinho a partir da 1.7.38.

## [1.7.1] - 2026-11-03

Performance Lab: benchmark A/B com significância estatística. Build e testes verdes. A captura, o atalho e o teste de ajuste em jogo real ainda não foram conferidos: ver `docs/release-1.7.1-relatorio.md`.

### Adicionado
- **Performance Lab no topo da página Bench:** grave a mesma cena em duas configurações, com repetições, e veja se a diferença é real ou ruído.
- **Veredito estatístico:** Mann-Whitney exato ou t de Student, com o d de Cohen. A amostra é uma medida por captura. Uma captura de cada lado dá "inconclusivo", e 3 de cada lado usam o t de Student, porque o Mann-Whitney não alcança p < 0,05 com tão poucos valores.
- **Gráfico A/B:** frametime das duas configurações no mesmo eixo, com P99 e P99,9 de cada uma.
- **Relatório HTML** em `Documentos/tHgOptimization/BenchLab`, com o gráfico embutido.
- **Testar um ajuste:** mede antes, aplica, mede depois e permite reverter. Ajuste que exige reiniciar, como o HAGS, só é medido depois do reinício.
- **Histórico A/B** com as últimas 10 comparações. Clicar abre o relatório.
- **Atalho global** Ctrl+Shift+F10 para iniciar e parar a captura, com um som de aviso, porque o tHg não mostra aviso visual sobre jogo em tela cheia.

### Alterado
- Os benchmarks de cada peça foram para "Benchmarks individuais (avançado)". O botão da página da CPU continua abrindo direto neles.
- A janela de novidades aparece uma vez para quem já tinha visto a 1.7, para apresentar o Lab.

### Corrigido
- `MinimalCombo` mostrava o nome do tipo em vez do `DisplayMemberPath`.

## [1.7.0] - 2026-11-03

Correções de três auditorias independentes. Build e testes verdes, mas **o app não foi aberto nesta release e nenhuma escrita chegou ao hardware**. `OcGate.RealHardwareEnabled` continua `false`.

### Segurança
- **Overclock fail-closed.** Sem leitura válida e recente de temperatura, Vcore e VSOC (na CPU AMD), nada é aplicado. NaN, infinito e amostra com mais de 5 s são recusados. Os sensores são lidos com o lock do hardware na mão.
- **Energia e clock da GPU são tudo ou nada.** Estado guardado, gravação, leitura de volta e reversão se o driver não obedecer. "Aplicado" só depois da confirmação.
- **Vigia de OC:** o aviso em disco dura 30 min em vez de 2, e Kernel-Power 41 nas últimas 24 h também bloqueia o perfil automático. Fechar o app normalmente apaga o aviso.
- **Aceite de risco por placa:** trocar de GPU pede o aceite de novo, e o perfil automático não aplica sem ele. Quem aceitou antes da 1.7 adota a placa atual.
- **Curve Optimizer por núcleo da BIOS é preservado:** com margens diferentes entre os núcleos, o ajuste é recusado.
- **Fans:** sem leitura de sensor por mais de 15 s, o controle volta à BIOS. Saída que não aceita recebe 100% (CPU e bomba) ou 80% (demais), com aviso na tela.
- Privilégio não concedido pelo Windows agora é um erro claro, e o token do processo não vaza.

### Corrigido
- **Restauração da prioridade do jogo:** ela volta ao fim da sessão, a não ser que o usuário tenha mudado depois.
- **Correções de race condition:** `ReportHardwareError` contra `Stop`, `HardwareMutex.TryEnter` contra `Dispose`, e ADLX recarregando depois de descartado.
- Exceções em `async void` no diagnóstico e na abertura do hub Lian Li não derrubam mais o app. App fechando durante a abertura solta o handle HID.
- `powercfg` tem prazo real de 5 s: ler a saída de forma síncrona fazia o prazo nunca contar.
- `AppSettings.Save` grava uma cópia das listas, feita de uma vez.
- `HasPreferredCores` distingue "buffer insuficiente" de acesso negado.
- Catches vazios do caminho quente classificados em Debug, Warn ou Error.
- Hero card mantém o gradiente no hover, e o chip selecionado usa `OnAccent`.
- Chips de clock mostram o teto em uso, mesmo depois de reabrir o app.
- Falha intermitente no teste de leitura concorrente do `FrameMonitor`.

### Adicionado
- **RGB auto-detection operacional:** `ColorOrderAuto.Regras` em produção, respeitando `LightAutoOrder` e mostrando a origem da decisão. GRB vale só nos conectores endereçáveis de ASUS, Gigabyte e ASRock, com confiança média, e o botão **Testar cores** acende vermelho, verde e azul. Precisa de conferência física: ver o relatório.
- Busca (Ctrl+K) rola até o ajuste e o destaca.
- Item Modo de apresentação do overlay coleta sozinho com o jogo aberto, sem Alt+Tab.
- `AutomationProperties.Name` nos botões da janela.

### Alterado
- Offset de GPU aceita passos pequenos: de 0 a +300 MHz na NVIDIA e de 0 a +250 na AMD (até +300 com confirmação).
- X3D de dois CCDs sempre prefere o CCD com V-Cache, mesmo com "Prender o jogo" desligado. A opção deixa de ter efeito nessas CPUs.
- Filas menores de mouse e teclado não são mais oferecidas. Quem aplicou continua vendo o ajuste, para poder desfazer.
- `clean.bgapps`, `comp.hpet` e `comp.ulps` agora começam com `[EXPERIMENTAL]`.
- Diagnóstico mudou de Configurações para Desempenho · Engasgos. `Get-MMAgent` virou a classe WMI `PS_MMAgent`, com o PowerShell de reserva.
- Página de RGB na ordem Integrações, Dispositivos e Controle Global.

### Desempenho
- Listas longas virtualizadas: em lista sintética de 63 itens, o layout da PageOptimize caiu de 71 para 16,6 ms.
- Sem alocação por IOCTL do PawnIO, sem LINQ nas contagens da topologia, sem `GetSystemCpuSetInformation` a cada aplicação de CPU Sets, sem listar todos os processos a cada Alt+Tab e sem boxing no histórico de frametimes.
- `CpuInfo.Caches` usa `ArrayPool`, e a contagem de frames usa `Interlocked`.

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
