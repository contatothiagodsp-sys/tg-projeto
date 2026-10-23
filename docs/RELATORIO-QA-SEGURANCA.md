# Relatório de QA e segurança — tHg Optimization 1.3.0

Data: 22/10/2026 · Escopo: código-fonte completo (C#/.NET 8 WPF), com foco nos itens pedidos.
Método: leitura de todo fluxo que executa processos, lê arquivos graváveis pelo usuário, baixa binários, aloca memória
grande ou mexe no sistema; correções compiladas e verificadas (compilação parcial com stubs, já que o ambiente de análise
não é Windows). Itens marcados **[corrigido]** estão no código desta versão.

## 1. Memória

| Achado | Gravidade | Situação |
|---|---|---|
| Stress test de RAM alocava blocos de 128 MB como arrays do .NET. Ao parar, os arrays ficavam no heap de objetos grandes (LOH) e o processo continuava com GBs (17,8 GB no print do teste). | Alta | **[corrigido]** Blocos pedidos direto ao Windows (`VirtualAlloc`, classe `NativeBuffer`) e devolvidos com `VirtualFree` no `finally` do worker. O worker confere o cancelamento a cada 1 MB (antes, a cada 128 MB). `Stop()` espera o worker e chama `MemoryRelease.Trim()` (GC com compactação do LOH + `SetProcessWorkingSetSize`). |
| Benchmark de memória alocava 2,25 GB em arrays do .NET (latência 256 MB + banda 2×1 GB). | Média | **[corrigido]** Mesma troca por `NativeBuffer` com `using`; trim no fim do benchmark. |
| Medidor de FPS (`FrameMonitor.GetStats`), chamado várias vezes por segundo pelo overlay: copiava 10 s de frames, criava listas e ordenava com LINQ a cada chamada, e chamava `Process.GetProcessById` (percorre todos os processos do Windows) toda vez. | Média (lixo constante para o GC durante o jogo) | **[corrigido]** `ArrayPool`, ordenação no lugar, nome do processo em cache. |
| `GpuBurn`, `GpuGameBench`, `GpuRtBench`, `DiskBench`: recursos D3D11/D3D12/DXC e buffers alinhados. | — | Auditado: todos já descartados em `finally` (lista `keep`/`disposables` em ordem inversa, `NativeMemory.AlignedFree`). Sem mudança. |
| `AmdSnapshot`: `ZipArchive`/streams. | — | Auditado: tudo em `using`. |

## 2. Segurança (o app roda como administrador)

O risco central de um app elevado é **escalonamento de privilégio**: um programa comum (sem administrador) influenciar o
que o tHg executa ou carrega.

| Achado | Gravidade | Situação |
|---|---|---|
| Configurações em `%AppData%` (gravável por qualquer processo do usuário). O tHg, elevado, lia dali o que executar: programas auxiliares dos jogos, instalador de driver pendente, e valores a restaurar que viravam parte de comandos. | **Alta** (escalonamento de privilégio) | **[corrigido]** Configurações, logs e backups em `%ProgramData%\tHgOptimization\users\<SID>`, com permissão de escrita só para Administradores e SYSTEM (`SecureFolder`). Migração automática da pasta antiga na primeira abertura. Pastas criadas por outro dono ou que sejam atalhos (junctions) são postas de lado e recriadas. |
| `SHIELD → Desfazer`: `Set-MpPreference -PUAProtection {valor salvo}` montado com texto do arquivo de configurações. | **Alta** (injeção de comando PowerShell como administrador) | **[corrigido]** Só aceita 0, 1 ou 2. Nome do perfil de firewall só de uma lista fixa; nome da conta Convidado validado. |
| Restaurações do plano de energia e do DNS usavam valores salvos direto nos argumentos do `powercfg`/`netsh`. | Média | **[corrigido]** Só GUID válido para o `powercfg`; DNS só com endereços IP válidos e nome de adaptador sem aspas. |
| DLL do iCUE (Corsair) carregada pelo processo elevado a partir de `%AppData%`. | **Alta** (DLL hijacking) | **[corrigido]** Pasta protegida; a assinatura é conferida na cópia final (a que será carregada), não na temporária. Download só de HTTPS do GitHub. |
| Instaladores de driver (NVIDIA, AMD, chipset) baixados em `%TEMP%`, conferidos e depois executados: dava para trocar o arquivo entre a verificação e a execução. | **Alta** (TOCTOU) | **[corrigido]** Download em pasta protegida; só HTTPS; nome de arquivo saneado. |
| Verificação do fabricante da assinatura com `Contains` ("Advanced Micro Devices Falso Ltda" passaria). | Média | **[corrigido]** Compara o campo O= ou CN= inteiro. A cadeia continua validada pelo `WinVerifyTrust`. |
| Atualizador: aceitava instalar sem SHA-256 se o GitHub não informasse. | Média | **[corrigido]** Sem SHA-256 publicado, a atualização é recusada; só HTTPS de github.com / githubusercontent.com; pasta protegida. |
| Jogos com argumentos e programas auxiliares abriam **como administrador** (herdavam do tHg). | Média | **[corrigido]** Abrem com o token da Área de Trabalho (`Unelevated`, `CreateProcessWithTokenW`), sem administrador; se falhar, abre do jeito anterior. |
| Drivers seriais embutidos extraídos em `%TEMP%` antes do `pnputil`. | Baixa (o `pnputil` exige driver assinado) | **[corrigido]** Extraídos na pasta protegida. |
| `WindowsCleanup`: exclusão em pastas fixas, sem seguir atalhos de pasta (`AttributesToSkip = ReparsePoint`); apps removidos só da conta atual, nome vindo do próprio Windows e escapado. | — | Auditado, sem mudança. |
| `DduCleaner`: nomes de pacotes e serviços são constantes do código; `Directory.Delete` do .NET não segue junctions. | — | Auditado, sem mudança. |
| Leitura de imagens/pacotes USB (buffer overflow): o código é gerenciado; os trechos `unsafe` (topologia da CPU, buffers nativos) validam tamanhos antes de ler. | — | Auditado. |

## 3. Estabilidade

- Exceções globais (`App.xaml.cs`): janela, threads e tarefas já registradas em `falhas.txt` (agora na pasta protegida), com aviso na próxima abertura.
- Corrigidos nesta rodada (vindos de testes reais): Modo Jogo tratando o cliente do LoL como jogo e rebaixando a prioridade da partida; limpeza de memória de todos os processos na abertura do jogo; avisos por cima de jogo em tela cheia; menu da bandeja na área de ícones ocultos; plano de energia; Widgets em chave protegida.
- Condições de corrida revisadas: `FrameMonitor` (lock único para filas e gravações; nomes com lock próprio), `StressTester` (Stop espera os workers antes de liberar), Modo Jogo (timer na thread da janela), atualização (flag `_updating`).

## 4. Rollback

Já existente e mantido: cada ajuste guarda o valor original antes da primeira alteração (`TweakBackup`), com "Desfazer"
por página (TURBO, SHIELD, Limpeza do Windows, DNS, perfis NVIDIA, Windows Update da DDU) e ponto de restauração opcional
antes do TURBO e da DDU. Esses backups agora ficam na pasta protegida.

## 5. Novos módulos da 1.1.0

| Módulo | Riscos avaliados | Como foi tratado |
|---|---|---|
| Fans (`FanControl.cs`) | Fan presa em rotação baixa; oscilação; falha de leitura. | Só saídas que o LibreHardwareMonitor expõe como controláveis (Super I/O). Temperatura suavizada, subida limitada e descida com histerese e atraso. Sem leitura ou CPU ≥ 90 °C → 100%. Ao fechar (`SensorMonitor.Dispose`), `SetDefault` em todas as saídas. Valores fora da faixa do chip são limitados (`MinSoftwareValue`/`MaxSoftwareValue`). Curva personalizada validada (0–110 °C, 0–100%, ao menos 2 pontos). |
| Diagnóstico USB (`UsbInventory.cs`) | — | Somente leitura (WMI). Não abre nem escreve em nenhum dispositivo. |
| Estúdio LCD (`LcdStudio.cs`) | Imagem/GIF maliciosa ou enorme (consumo de memória); estouro no RGB565. | Arquivo limitado a 64 MB; GIF limitado a 600 quadros e a 120 milhões de pixels no total; decodificação pelo WIC do Windows; conversão RGB565 com aritmética `checked` e tamanho exato largura×altura×2. |
| Envio às telas | Mandar dados num protocolo errado pode travar a tela/hub até desligar da tomada. | **Desligado nesta versão** (botão inativo). Ver seção 6. |
| Pausar programas (`ProcessPause`) | Pausar processo do sistema, anti-cheat ou o próprio jogo; processos presos se o tHg fechar. | Lista de nunca pausar (sistema, anti-cheats, Discord, OBS, Steam, o jogo e o tHg); PIDs pausados salvos e retomados ao fechar o jogo, no `Dispose` e na próxima abertura. |
| HDR / OLED (`DisplayExtras.cs`) | Estruturas nativas com tamanho errado. | Tamanhos conferidos com compilador C (caminho 72 bytes, alvo no deslocamento 20, cabeçalhos 32/24, APPBARDATA 48). Estado anterior guardado (`oled.`) com "Desfazer". |
| MSI em rede/USB (`iomsi`) | Placa antiga sem MSI parar de funcionar. | Fora do TURBO automático (peso 0), só PCI, com valor anterior guardado por dispositivo; restauração aceita apenas caminhos de interrupção PCI. |
| RGB (`LightPump`) | Travar a janela ou o jogo esperando um dispositivo; pacotes misturados no socket do OpenRGB. | Thread própria de baixa prioridade, "o último quadro vence"; escrita no socket do OpenRGB com lock. |

## 6. Novas chamadas nativas e protocolos da 1.2.0

| Item | Risco | Tratamento |
|---|---|---|
| HID (`HidDevice.cs`: SetupAPI, `CreateFile`, `HidD_GetPreparsedData`, `HidP_GetCaps`) | Aparelho que não responde travar o app; escrever no aparelho errado. | Só caminhos com o VID/PID exato e, na controladora TL, só a interface de página de uso 0xFF1B; tamanho do relatório lido do próprio aparelho; toda escrita e leitura com tempo limite (2 s / 300–500 ms) via E/S assíncrona cancelável. Estruturas conferidas em C (SP_DEVICE_INTERFACE_DATA 32 bytes, cbSize do detalhe = 8, HIDP_CAPS 64 bytes). |
| Telas TL LCD (04FC:7393) | Imagem grande demais; pacote errado. | Protocolo do lian-li-linux (MIT): relatório 0x02 de 512 bytes, cabeçalho de 11 bytes, 501 bytes de dados, JPEG ≤ 64 KB (a qualidade baixa sozinha até caber), confirmação de cada pacote na imagem fixa. |
| Controladora TL (0416:7372) | Fan parada. | Porcentagem limitada a 0–100 e controlada pelas mesmas curvas com proteção de 100% (CPU ≥ 90 °C ou sem leitura); ao fechar o tHg, 60%. |
| Serial Turing (`TuringSerial.cs`) | Porta errada; dados de tamanho errado. | Só nomes `COMn`; imagem com tamanho exato largura×altura×2 conferido antes do envio; timeouts de 2 s. Na TURZX CA21 o protocolo é marcado como TESTE (sem documentação do fabricante); comandos desconhecidos são ignorados pela tela. |
| `ntdll!NtSetInformationProcess` (prioridade de disco/memória) | Prioridade presa num processo. | Só no jogo e nos processos rebaixados pelo ProBalance; restaurados (disco 2, memória 5) ao fechar o jogo. |
| Serviços SysMain/WSearch | Ficarem parados. | Só para quem estava rodando; lista salva e religada ao fechar o jogo, no `Dispose` e na próxima abertura. |
| NVML (perfis de OC) | Reaplicar um ajuste instável em todo boot. | `OcWatchdog`: aviso gravado na pasta protegida antes de aplicar e apagado após 2 min estável; se ficar (PC travou) ou houver evento 4101 (TDR) nas últimas 24 h, o perfil automático é desligado e o usuário é avisado. |

**Não implementado, por decisão técnica** (pedidos da especificação da 1.2.0):
- *Cabeçalho DES-CBC "slv3tuzx"*: vale para os aparelhos Lian Li `1CBE:xxxx` (SL V3/TL V2 sem fio, HydroShift II, Universal Screen). As telas do usuário (TL LCD com fio e TURZX CA21) não usam esse protocolo; mandar esse cabeçalho a elas não teria efeito.
- *Comando 108 = brilho*: no protocolo serial, 108 desliga a tela; o brilho é o comando 110 (usado aqui).
- *EmptyWorkingSet em todos os processos ao abrir o jogo*: removido na 1.0 depois de causar engasgos e o LoL minimizar no teste real; o Windows devolve essa memória sozinho quando o jogo pede.
- *ProcessPagePriority = 5 no jogo*: 5 já é o padrão do Windows (sem efeito). Em vez disso, os processos de fundo rebaixados descem para 2.
- *FSO + HIGHDPIAWARE automático em todo jogo*: no Windows 11, desligar as otimizações de tela cheia tira o modelo flip e pode piorar a latência; e HIGHDPIAWARE quebra a interface de alguns jogos. Continua como opção por jogo na Biblioteca.
- *Offsets de clock por NVAPI e overclock de placas AMD*: a NVAPI de offsets não é pública; nas Radeon o caminho oficial é o AMD ADLX (SDK em C++ que exige uma ponte nativa). Fica para uma versão própria.

## 7. Telas LCD: estado do suporte

- **Lian Li UNI FAN TL LCD (04FC:7393), 3 telas no PC do usuário:** envio validado pelo usuário (1.2.0). Na 1.3.0 cada
  fan é um destino próprio: identidade pelo comando 62 (porta da controladora e posição na corrente, mesmo protocolo
  do lian-li-linux); sem resposta, o caminho do aparelho no Windows vira a chave. **A leitura da posição ainda precisa
  ser confirmada no hardware real** — se a fan não responder, os nomes ficam "Fan TL LCD 1/2/3" na ordem do Windows.
- **Observação sobre o modelo:** o relatório USB do usuário mostra 3× 04FC:7393 "UNI FAN TL LCD" e a controladora TL
  0416:7372. Isso é a linha **TL LCD com fio**, não a **SL Wireless LCD** (receptor 1CBE:0005, protocolo diferente). O
  receptor SL Wireless é reconhecido e avisado como não suportado; nenhum comando foi presumido entre as gerações.
- **TURZX 2,1"/2,8" (1A86:CA21 dormindo → 1D6B:0121 acordada):** protocolo revisão C (1.2.2), aguardando teste real.
- **Turing 3,5"/5" (1A86:5722 / 7523):** protocolo revisão A documentado.
- Rotação passou a ser feita na imagem (0/90/180/270°) para todas as telas, sem comando de hardware novo. Em telas
  retangulares só 0° e 180° ficam disponíveis.

## 7.1 Arquitetura da informação (1.3.0)

Auditoria: a barra lateral tinha 30 destinos (3 soltos no topo + grupos HARDWARE, GAME, DESEMPENHO, FERRAMENTAS e
MAKER). Problemas: o grupo HARDWARE misturava informação (Sistema, Sensores) com ação (Fans, Telas); FERRAMENTAS era um
"diversos" com 7 itens de assuntos diferentes (otimização, limpeza, drivers, DDU, apps, RGB); TURBO e Otimização/Limpeza,
que mexem nas mesmas coisas do Windows, estavam em lugares diferentes; Fans e Telas longe da Integração RGB; o diagnóstico
USB (ferramenta técnica) dividia página com o estúdio das telas (uso diário); e o painel inicial repetia verificações que
o TURBO já faz, sem levar a lugar nenhum.

| Função | Antes | Agora | Decisão |
|---|---|---|---|
| Visão geral | topo | Início | Virou central de ações ("O que resolver") |
| TURBO | topo | Otimizar → TURBO | Junto de Ajustes, Limpeza e Inicialização; nota na área |
| SHIELD | topo | Segurança | Nota na área |
| Sistema, Sensores, Placa de vídeo, Discos, Energia, Rede | HARDWARE | Meu PC → abas | Só informação e diagnóstico |
| Fans, Telas LCD e USB | HARDWARE | Fans, telas e RGB → abas | Separados da informação |
| Integração | FERRAMENTAS | Fans, telas e RGB → Iluminação RGB | Movido e renomeado |
| Diagnóstico USB | dentro de "Telas LCD e USB" | Avançado → Diagnóstico USB | Separado em página própria |
| Modo Jogo, Biblioteca, FPS estimado, Gargalo, Overlay | GAME | Jogos → abas | Mantidos juntos |
| Benchmark, Stress test, Teste de engasgos, Overclock | DESEMPENHO | Desempenho → abas | Mantidos juntos |
| Otimização, Limpeza do Windows, Inicialização | FERRAMENTAS | Otimizar → abas | Fundidos com o TURBO |
| Drivers, DDU, Instalar apps | FERRAMENTAS | Drivers e apps → abas | Agrupados por assunto |
| Portas COM, Drivers seriais | MAKER | Avançado → abas | Junto do diagnóstico USB |
| Verificações repetidas no painel | Painel + TURBO | TURBO/SHIELD; o painel resume e leva até lá | Duplicação removida |

Implementação: os botões antigos de navegação continuam existindo (escondidos) e cada aba aciona o botão original, então
carregamento sob demanda, atalhos, desfazer e confirmações de cada página não mudaram (`MainWindow.Areas.cs`).

## 8. Limitações da verificação

O ambiente de análise não é Windows: a compilação foi feita com versões simuladas das bibliotecas do Windows (WPF, WMI, ETW,
LibreHardwareMonitor) e o restante passou por verificação de sintaxe. A compilação definitiva é a do `GERAR-INSTALADOR.bat`.

Na 1.3.0 entraram na compilação com stubs: `MainWindow.Areas.cs`, `MainWindow.Lcd.cs`, `LianLiTl.cs`, `LcdStudio.cs`,
`AppSettings.cs`, Turbo e Shield. `MainWindow.Dash.cs` (gráficos WPF) teve o trecho novo ("O que resolver") verificado
contra os tipos reais do projeto; o resto do arquivo depende de muitas classes de desenho do WPF e fica para a compilação
no Windows. O XAML foi validado (XML bem formado, sem `x:Name` repetido, todo evento com método correspondente, todo
estilo existente).
