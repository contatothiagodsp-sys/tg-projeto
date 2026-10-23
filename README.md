<p align="center"><img src="docs/banner.png" alt="tHg Optimization" width="760"></p>

# tHg Optimization — v1.3.0

**Desempenho, segurança e diagnóstico para PC gamer.** Um app para Windows 10/11 que mede o PC, mostra o que está
segurando o desempenho (do XMP desligado na BIOS ao monitor de 144 Hz rodando em 60 Hz), fecha brechas de segurança e
configura o Windows e o driver de vídeo como os jogadores profissionais, sempre com **desfazer**.

> Instalação passo a passo: **[INSTALAR.md](INSTALAR.md)**.
>
> Baseado no [RESET EXPLODE](https://github.com/GustavoReset/ResetExplode) de Gustavo (GustavoReset), licença MIT.
> O aviso de copyright original está mantido em [LICENSE](LICENSE).

## Organização (v1.3)
A barra lateral tem 9 áreas por tarefa e as páginas de cada uma ficam em abas no topo (`MainWindow.Areas.cs`):

| Área | Abas |
|---|---|
| Início | Visão geral com "O que resolver" (pendências do TURBO, SHIELD, sensores, erros de hardware, portas COM) |
| Meu PC | Sistema · Sensores · Placa de vídeo · Discos · Energia · Rede |
| Fans, telas e RGB | Fans · Telas LCD (uma configuração por tela) · Iluminação RGB |
| Jogos | Modo Jogo · Biblioteca · FPS estimado · Gargalo · Overlay |
| Desempenho | Benchmark · Stress test · Engasgos · Overclock |
| Otimizar | TURBO · Ajustes e memória · Limpeza · Inicialização |
| Drivers e apps | Drivers · DDU · Instalar apps |
| Segurança | SHIELD |
| Avançado | Diagnóstico USB · Portas COM · Drivers seriais |

**Ctrl+K** busca qualquer função por nome ou palavra-chave.

## O que tem na v1.1
- **TURBO** · nota gamer de 0 a 100 por área (Hardware, BIOS, Windows, Rede). Detecta monitor abaixo da taxa máxima (corrige sozinho),
  cabo na placa-mãe em vez da placa de vídeo, XMP/EXPO desligado, single channel, PCIe em x8/x4, Resizable BAR, Windows em HD,
  Wi-Fi/cabo ruim, driver antigo e mais. Guia da BIOS para a sua placa-mãe e botão **Reiniciar na BIOS**.
  **Corrigir tudo** cria ponto de restauração e tem **Desfazer tudo** (`GamerAudit.cs`, `DisplayTuner.cs`, `MainWindow.Turbo.cs`).
- **SHIELD** · nota de segurança: antivírus e assinaturas, proteção contra adulteração, apps indesejados (PUA), SmartScreen, firewall,
  UAC, Windows Update, Secure Boot e TPM (exigidos pelo Vanguard e outros anti-cheats), Integridade de Memória, proteção do LSA,
  BitLocker, pontos de restauração, AutoRun, SMBv1, Área de Trabalho Remota, Assistência Remota, Wi-Fi aberto, pastas compartilhadas,
  conta Convidado, **cheats/cracks em Downloads** (verifica com o Defender) e **programas iniciando da pasta Temp**.
  **Blindar** liga tudo que é seguro ligar sozinho; nunca desliga proteção (`SecurityAudit.cs`, `MainWindow.Shield.cs`).
- **Modo Jogo** (GAME) · automático: detecta o jogo, sobe a prioridade, limpa a memória em espera, ativa alto desempenho, opcionalmente
  prende o jogo nos núcleos com 3D V-Cache (Ryzen X3D) ou nos núcleos P (Intel híbrido) e desfaz tudo ao fechar. Mede FPS médio e 1% low de
  cada sessão e mostra o **antes e depois do TURBO** por jogo (`GameMode.cs`, `MainWindow.GameMode.cs`).
- **Perfis pro no driver NVIDIA** · por jogo (CS2, Valorant, LoL, Fortnite, Apex, Overwatch 2, R6, CoD, Marvel Rivals, PUBG, Rocket League,
  Dota 2, The Finals e AAA como Cyberpunk e RDR2): baixa latência, desempenho máximo, filtragem em alto desempenho, V-Sync/G-SYNC com trava de
  FPS certa para o seu monitor. Detecta os jogos instalados e grava pela NVAPI oficial, com restauração (`NvDriverProfiles.cs`).
  Para AMD, receita completa do Adrenalin.
- **DDU** (FERRAMENTAS) · remoção completa do driver de vídeo NVIDIA, AMD ou Intel, agora ou no Modo de Segurança (o app agenda, limpa e volta sozinho), com backup, bloqueio temporário do Windows Update e sem tocar em drivers de chipset (`DduCleaner.cs`, `MainWindow.Ddu.cs`).
- **Cache de shaders** · limpa os caches do DirectX/NVIDIA/AMD/Intel e avisa quando o driver foi atualizado.
- **OTIMIZA** · 13 ajustes reversíveis do Windows, com cuidado especial para Ryzen X3D de dois CCDs e notebooks.
- **Teste de engasgos** · latência de DPC/ISR por driver pelo ETW do kernel, como o LatencyMon, com a correção de cada driver (`LatencyTest.cs`).
- **Gravação de frametime** · 60 s de todos os frames: médio, 1%/0,1% low, P99, engasgos e CSV; sessões exportáveis (`FrameAnalysis.cs`).
- **Rede** · ping a servidores de jogos e comparador de DNS com troca reversível (`NetExtra.cs`).
- **Limpeza do Windows** · Copilot, Recall, Widgets, telemetria, anúncios, temporários e apps de brinde, tudo com desfazer (`WindowsCleanup.cs`).
- **Ryzen X3D e AMD** · driver de chipset, otimizador do 3D V-Cache, guia de BIOS X3D, ReBAR/SAM em qualquer placa e guia do Adrenalin.
- **Atualização automática** pelo GitHub Releases, com verificação de SHA-256 (`AppUpdater.cs`).
- **Fans** · curvas com histerese nas saídas da placa-mãe (`FanControl.cs`, `MainWindow.Fans.cs`).
- **Telas LCD e USB** · diagnóstico USB com tabela TURZX/Lian Li e estúdio de imagem/GIF com sensores e prévia (`UsbInventory.cs`, `LcdStudio.cs`).
  A tabela de IDs da Lian Li vem do projeto [lian-li-linux](https://github.com/sgtaziz/lian-li-linux) (MIT).
- **Segurança** · pasta só de administrador (`SecureFolder.cs`), processos sem administrador (`Unelevated.cs`), memória nativa (`NativeBuffer.cs`).

## Histórico (como RESET EXPLODE, v0.16)
- **Inicialização** (FERRAMENTAS): veja, desative ou remova os programas que abrem junto com o Windows, com opção de desfazer.
- **Instalar apps** (FERRAMENTAS): Steam, Chrome, Discord, Lightshot, 7-Zip, WinRAR e VLC de uma vez depois de formatar, pelo winget.
- **MAKER** (nova categoria): Portas COM (fantasmas e números COM presos, que saiu de Otimização) e Drivers seriais
  (CH340, CP2102, FTDI, Prolific e Arduino: diagnóstico, reinstalar, trocar ou remover versão do driver, Arduino IDE).
- **Abertura mais rápida**: a janela aparece em ~0,5 s (antes ~4,6 s); o hardware é lido em paralelo, em segundo plano.
- **Overlay corrigido**: fica por cima de jogos em janela sem borda como GTA V e Cyberpunk 2077 e segue o monitor do jogo.

Detalhes em [NOVIDADES.md](NOVIDADES.md) (o mesmo texto vai nas notas da release).

App WPF (.NET 8) com:
- **Informações do sistema** (categorias + detalhes): SO, placa-mãe/BIOS, CPU, RAM (por módulo), GPU, discos — via WMI.
  Em Armazenamento, cartões por unidade (C:, pendrive, HD externo, rede) com anel de cheio/vazio, tipo e disco físico (`DriveMap.cs`).
- **Avisos de dispositivo** (`ToastWindow.xaml`): ao conectar/remover pendrive, HD externo ou qualquer dispositivo USB (ex.: Arduino),
  aparece um aviso no canto da tela e a lista de unidades se atualiza (via `WM_DEVICECHANGE`, sem ficar consultando).
- **Sensores em tempo real**: temperatura, clock, carga, voltagem, ventoinhas, consumo etc. — via LibreHardwareMonitorLib. Atualiza a cada 1 s, com filtro por tipo.
- **Placa de vídeo** (estilo GPU-Z): chip, arquitetura, processo, die, transistores, shaders/SMs/Tensor/RT, ROPs/TMUs, fillrates, TFLOPS,
  memória (tamanho, tipo, barramento, clock, banda, Resizable BAR), clocks, PCIe (máximo e em uso), limites de energia,
  VBIOS, driver, assinatura WHQL, CUDA, DirectX (feature level), APIs e recursos da arquitetura.
  Sensores ao vivo da GPU + P-state, PerfCap, NVENC/NVDEC. Fontes: NVML (NVIDIA), DXGI/Direct3D 12, WMI/PnP, registro e banco de specs em `GpuSpecDb.cs`.
- **Stress test**: carga de CPU em todos os threads (FPU + inteiros) e/ou RAM (grava e verifica padrões em 40–80% da memória livre).
  Para sozinho quando a temperatura da CPU atinge o limite configurado, quando o tempo acaba ou se aparecer erro de memória.
  Mostra temperatura atual/máxima, carga, consumo, gráfico dos últimos 5 min e um log dos eventos.
- **Stress de GPU** (inspirado no FurMark, `GpuBurn.cs`): torus com pelos renderizado por *shells* em Direct3D 11 (Vortice),
  com overdraw massivo, cadeia de FMAs no pixel shader e alpha blending, sem limite de FPS, numa janela própria.
  Opções: resolução (720p/1080p/1440p/tela cheia), MSAA (off/4x/8x), intensidade (Normal/Alta/Extrema) e limite de temperatura da GPU.
  Mostra FPS, temperatura, consumo e PerfCap; ao final registra frames e FPS médio. ESC ou fechar a janela encerra o teste.
  Medido numa RTX 5070 (1080p, 4x MSAA, Alta): ~99% de uso e 220–232 W de 250 W.
- **Jogos**: FPS esperado em 32 jogos populares para a sua GPU + CPU, por resolução (1080p/1440p/4K), gráficos e upscaling (DLSS/FSR).
  Com chave da [HowManyFPS API](https://howmanyfps.com/developer-api) (pedida em info@howmanyfps.com) mostra FPS médio e 1% low medidos em sessões reais;
  sem chave, usa uma estimativa local aproximada (`GameCatalog.cs`). A chave fica cifrada com DPAPI em `%AppData%\tHgOptimization` e as respostas em cache por 7 dias.
- **Detector de gargalo**: equilíbrio teórico GPU × CPU; leitura ao vivo (carga da GPU, CPU total, núcleo mais ocupado, VRAM, RAM,
  temperaturas, limitador térmico) com veredito; e análise de sessão (1/3/5 min) com % do tempo limitado por GPU, CPU, VRAM, RAM,
  temperatura ou trava de FPS, mais recomendações (`BottleneckAnalyzer.cs`).
- **Overlay de jogos** (estilo RTSS/Afterburner, `OverlayWindow.xaml`): FPS, frametime, 1% low, gráfico de frametime, GPU/CPU/RAM/VRAM.
  Janela transparente, sempre no topo e que ignora cliques; atalho global **Ctrl+Shift+O**. **Itens escolhidos pelo usuário** (`OverlayMetrics.cs`,
  `MainWindow.Overlay.cs`): FPS/frametime/1% low/gráfico, GPU (uso, temperatura, hotspot, clocks do núcleo e da memória, consumo, tensão,
  ventoinha, PerfCap), VRAM, CPU (uso, núcleo mais ocupado, temperatura, clock médio e máximo, consumo), RAM, nome do jogo e hora. Volta para o topo a cada troca de janela ativa
  (jogos em janela sem borda também se marcam como "sempre no topo") e fica no monitor do jogo. Não aparece em tela cheia exclusiva. O FPS de qualquer jogo é medido via ETW
  (eventos Present de DXGI/D3D9, como o PresentMon, `FrameMonitor.cs`), sem injetar nada no jogo.
- **BENCH** (`BenchRenderer.cs`): benchmark de CPU no princípio do Cinebench, com renderizador próprio. Path tracer monocromático
  (iluminação global, sombras suaves, metal e vidro com refração), 960×540 a 1024 amostras/pixel, imagem em blocos de 32 px
  distribuídos entre threads do centro para fora. Multi-core (todas as threads) e single-core (1 thread, imagem inteira, ~1min40s num 7700X),
  1 passada ou mínimo de 10 min. Nota = amostras/s ÷ 10.000, determinística (sementes fixas por pixel). Histórico dos últimos 10 resultados.
  Referência: Ryzen 7 7700X ≈ 4.500 multi / 530 single.
- **BENCH · benchmarks de jogo**:
  - *Cena de jogo (GPU)* (`GpuGameBench.cs`): vale em Direct3D 11 com terreno fractal, 40 mil árvores por instancing, sombras 4096² com PCF,
    água, neblina e pós HDR; câmera em trajeto fixo por tempo, 1080p/1440p/4K, 30/60 s. Nota = FPS médio × 10. RTX 5070 em 1080p ≈ 680 FPS.
    Os sensores ficam pausados durante o teste (consultas ao driver geravam engasgos no 1% low).
  - *Ray tracing (GPU)* (`GpuRtBench.cs`, `MainWindow.Rt.cs`): Direct3D 12 com **DXR 1.1** (raios inline num compute shader), usa os núcleos
    de RT (RTX 20+, Radeon RX 6000+, Intel Arc). 4 mil objetos (fosco, metal, espelho, luzes), path tracing com sombra suave, oclusão de
    ambiente, reflexos e iluminação indireta; TLAS refeita a cada frame. Presets Normal/Alta/Extrema (2/4/8 amostras, 2/3/4 rebatidas).
    **Confiabilidade**: aquecimento sem medir + 3 passadas; nota pela mediana (FPS × 10), variação entre passadas (confiável abaixo de 3%),
    tempo de frame pelos timestamps da GPU. Referência: RX 9070, Alta 1440p, modo Padrão = 878 pts (87,8 FPS), variação 0,5%.
  - *Simulação de jogo (CPU)* (`GameSimBench.cs`): 30 mil unidades em frente de batalha; thread principal (grade, lógica, preparo do desenho)
    + workers (física, colisão, IA). Nota = FPS da simulação. Ryzen 7 7700X ≈ 290 FPS.
  - *Memória e carregamento* (`MemoryBench.cs`): latência e banda da RAM; carregamento de mapa (pacote de 512 MB + mil arquivos de 64 KB)
    lido sem cache do Windows, com passada prévia para o antivírus e 8 threads. Arquivos temporários apagados no final.
  - *Disco* (`DiskBench.cs`): leitura e escrita sequencial (1 MB) e aleatória 4K (QD1 e QD32), sem cache do Windows
    (`FILE_FLAG_NO_BUFFERING`), em arquivo temporário de 512 MB / 1 GB / 4 GB na unidade escolhida; apagado no final. Histórico dos últimos resultados.
- **OC** (`MainWindow.Oc.cs`, `OcCheck.cs`): ajuste de limite de energia e travamento de clock da GPU NVIDIA (via NVML) com botão de restaurar,
  e **verificação de estabilidade** de overclock (CPU, GPU ou ambos; 5/15/30 min) com limites de segurança de temperatura, leitura de erros
  de hardware (WHEA, reset de driver de vídeo), veredito, histórico e guia de BIOS conforme a placa-mãe e a CPU.
- **OTIMIZA** (`Optimizer.cs`, `WindowsTweaks.cs`): um botão que limpa a memória em espera (como o ISLC) e enxuga a RAM dos
  processos, e fecha os programas marcados (pré-marca só os seguros: OneDrive, Teams, Widgets, Adobe...; programas com janela são
  fechados sem forçar). Ajustes do Windows com interruptor e "Restaurar originais": plano Alto desempenho, Modo de Jogo, desativar
  Game DVR, HAGS, otimizações para jogos em janela, aceleração do mouse. Nada que reduza a segurança (Defender, VBS, UAC, Update).
- **MAKER** (Arduino, ESP32 e conversores USB-serial):
  - *Portas COM* (`UsbCleaner.cs`): lista e remove registros de dispositivos USB/seriais ausentes ("fantasmas" de Arduino,
    ESP32, CH340, FTDI, CP210x...) e libera os números COM presos no ComDB, com backup para desfazer. Resolve a IDE do Arduino
    não achar a placa ou a porta ir para COM20+.
  - *Drivers seriais* (`MakerDrivers.cs`, `MainWindow.Maker.cs`): diagnóstico das placas conectadas com **CH340/CH341, CP2102/CP210x,
    FTDI, Prolific PL2303 e Arduino** pelo código do Gerenciador de Dispositivos (sem driver, código 10, 43, FTDI falsificado com PID 0000),
    com orientação para cada caso; **REINSTALAR** (remove e detecta de novo); lista das versões de driver guardadas no Windows com
    **REMOVER** (backup antes, para voltar a uma versão anterior ou desinstalar de vez) e "Restaurar removidos"; **drivers oficiais incluídos no app** (CH340 4.0, CP210x 11.6,
    FTDI 2.12.36.20 e Arduino, em `Drivers`, veja `Drivers/LEIA-ME.md`) que instalam sem internet; e link para a página oficial de cada fabricante. **Arduino IDE**: instalar/atualizar pelo winget (traz os drivers das placas oficiais),
    fechar programas que prendem a porta (monitor serial, PuTTY, Cura...) e limpar o cache de índices quando a IDE trava em "Downloading index".
- **Inicialização** (`StartupManager.cs`, `MainWindow.Startup.cs`): lista os programas que abrem com o Windows (chaves Run do usuário e
  de todos os usuários, 32 e 64 bits, e pastas Inicializar) com descrição e fabricante. Interruptor para ativar/desativar igual ao
  Gerenciador de Tarefas (chaves StartupApproved) e botão REMOVER, com cópia em `%AppData%\tHgOptimization` para "Restaurar removidos".
- **Instalar apps** (`AppInstaller.cs`, `MainWindow.Apps.cs`): instala os essenciais pós-formatação (Steam, Google Chrome, Discord,
  Lightshot, 7-Zip, WinRAR, VLC) pelo **winget**, em silêncio e um de cada vez, mostrando o resultado de cada um. Detecta o que já está
  instalado (Aplicativos instalados do Windows). Para incluir outro app, acrescente uma linha em `AppInstaller.Catalog()` com o ID do winget.
- **Saúde dos discos (S.M.A.R.T.)** (`DiskHealth.cs`, `MainWindow.Disks.cs`): página própria com um cartão por disco físico —
  veredito (Bom/Atenção/Ruim) com os motivos, temperatura, horas ligado, ciclos, desgaste do SSD, setores realocados, erros de leitura/escrita
  e previsão de falha. Fontes: Windows Storage (MSFT_PhysicalDisk, MSFT_StorageReliabilityCounter) e MSStorageDriver_FailurePredictStatus.
- **Relatório exportável** (`ReportBuilder.cs`): botão **EXPORTAR RELATÓRIO** em Sistema. Gera um arquivo HTML (com a marca tHg) ou JSON
  com as informações do sistema, RAM por módulo, discos e saúde dos discos.
- **RAM detalhada**: resumo (capacidade, canais, tipo DDR4/DDR5/LPDDR5, velocidade) e, por módulo, fabricante, part number, formato,
  voltagem, clock configurado e localização do slot.
- **Energia** (`BatteryInfo.cs`, `MainWindow.Net.cs`): bateria de notebook com desgaste (capacidade atual × de projeto), ciclos de carga, estado,
  autonomia, tensão e potência, com veredito; fonte de energia (tomada/bateria), plano de energia do Windows e consumo dos sensores de CPU e GPU.
  Em PC de mesa mostra só o cartão de energia.
- **Rede** (`NetworkTest.cs`): diagnóstico de lag em ~30 s — latência, jitter e perda de pacotes até o roteador e até a internet (1.1.1.1 e 8.8.8.8),
  tempo de DNS, velocidade de download/upload (servidor público speed.cloudflare.com) e latência sob carga (bufferbloat). O veredito aponta se o
  problema está na rede local (Wi-Fi/cabo/roteador), no provedor ou se a conexão está estável.
- **INTEGRAÇÃO** (`LightBackends.cs`, `CorsairSdk.cs`, `CorsairSdkInstaller.cs`, `MainWindow.Integ.cs`): controle de iluminação e efeitos
  de todo o PC numa tela só (em teste). Pontes: **Corsair iCUE / iCUE LINK** (iCUE SDK v4; a DLL é baixada pelo programa, com confirmação e
  verificação de assinatura), **OpenRGB** (servidor SDK local; é o caminho para **MSI Mystic Light, Gigabyte RGB Fusion, ASUS Aura**, RAM, Razer,
  Logitech e outras, já que essas marcas não têm SDK público) e **Razer Chroma** (API REST do Synapse). Efeitos: cor fixa, respiração, arco-íris em onda,
  e cores que seguem a **temperatura da CPU/GPU** ou a **carga da CPU**, com brilho e velocidade. Ao devolver ou fechar, cada fabricante reassume as luzes.
  Para MSI/Gigabyte/ASUS: abra o OpenRGB com o servidor ligado e feche MSI Center, Armoury Crate e RGB Fusion.
- **GAME** (`GameLibrary.cs`, `GameLauncher.cs`, `Vdf.cs`, `MainWindow.Launcher.cs`): inicializador que **detecta os jogos instalados** (Steam, Epic Games, GOG,
  Ubisoft Connect e `.exe` adicionados à mão) e guarda um **perfil por jogo**: prioridade do processo, plano de energia Alto desempenho só durante o jogo,
  GPU dedicada e sem otimizações de tela cheia (por `.exe`, no registro do usuário), **menu de gráficos** (modo de janela, resolução, taxa de atualização e extras
  convertidos em argumentos de inicialização no estilo Source/CS2, Unity ou Unreal) e **programas auxiliares** (ferramentas de terceiros que abrem antes do jogo
  e fecham junto). Em jogos da Steam, um botão grava os argumentos nas **opções de inicialização** (`localconfig.vdf`, com backup, fechando a Steam antes).
  O botão JOGAR aplica o perfil, inicia e desfaz o que for temporário quando o jogo fecha.

## Interface
- Visual preto e branco minimalista em **blocos** (cartões arredondados, inspirado em painéis de dashboard). Um único menu lateral
  com *Visão geral* no topo e categorias **tHg** recolhíveis (começam fechadas; clique para ver as sub-abas):
  - *Visão geral* (`MainWindow.Dash.cs`): gráfico de área CPU × GPU (temperatura, carga ou consumo; 1/5/15 min),
    desempenho do BENCH, tabela de componentes, anéis de RAM/VRAM/disco, eventos recentes e saúde do PC.
  - **TURBO** (`MainWindow.Turbo.cs`): nota gamer, diagnóstico de hardware/BIOS/Windows/rede e correção em um clique.
  - **SHIELD** (`MainWindow.Shield.cs`): nota de segurança e "Blindar".
  - **HARDWARE** · Sistema, Sensores, Placa de vídeo, Discos, Energia, Rede.
  - **GAME** · Modo Jogo, Biblioteca, FPS estimado, Gargalo, Overlay.
  - **DESEMPENHO** · Benchmark, Stress test, Overclock.
  - **FERRAMENTAS** · Otimização, Inicialização, Instalar apps, Drivers, DDU, Integração.
  - **MAKER** · Portas COM, Drivers seriais.
- Botão de **modo escuro/claro** no canto superior direito (`ThemeManager.cs`); a escolha fica salva. O overlay de jogos e os avisos de dispositivo ficam sempre escuros.
- Leitura ao vivo de temperatura e carga da CPU e da GPU sempre visível no rodapé do menu.
- Sensores com busca e filtros por tipo (chips); tema em `Theme.xaml`.

## Base utilizada
- Interface, stress test e gráficos: código próprio.
- Sensores: [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL 2.0) — mantenha o aviso de licença ao distribuir.
- Informações do sistema: WMI do Windows.
- Medição de FPS: [TraceEvent](https://github.com/microsoft/perfview) (MIT), da Microsoft, para ler eventos ETW.
- Renderização do stress de GPU: [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT), bindings de Direct3D 11 para .NET. Direct3D 12 e DXC (compilador de shaders da Microsoft) também via Vortice, no teste de ray tracing.

## Requisitos
- Windows 10/11
- .NET 8 SDK (https://dotnet.microsoft.com/download)
- Driver **PawnIO** (temperatura/consumo da CPU): `winget install namazso.PawnIO`
  A LibreHardwareMonitorLib 0.9.6+ usa o PawnIO no lugar do antigo WinRing0, que o Windows Defender bloqueia.

## Como rodar e gerar o instalador
Guia completo para quem não programa: **[INSTALAR.md](INSTALAR.md)**.
```
dotnet run --project tHgOptimization.csproj
```
O app pede privilégio de administrador (necessário para sensores de baixo nível e para os ajustes).
`publicar.bat` gera `dist\tHgOptimization.exe` (arquivo único, com o .NET embutido) e, com o Inno Setup 6 instalado,
`dist\tHgOptimization-Setup-<versão>.exe` (instalador de um clique: uma tela com INSTALAR, barra de progresso, atalhos, PawnIO e o app abre no fim; com desinstalador). Script do instalador: `installer/tHgOptimization.iss`.

### Release automática no GitHub
A cada push na `main`, o GitHub Actions (`.github/workflows/publicar.yml`) compila, gera o instalador e atualiza a release
**latest** com `tHgOptimization-Setup.exe`, o `.exe` portátil e um `.zip`. Uma tag (`git tag v1.0.0` e `git push --tags`) gera uma release fixa.

### Logo
A arte (robô tHg e o letreiro THG OPTIMIZATION) fica em `docs/arte/`. `docs/arte/gerar-arquivos.py` gera a partir dela o ícone,
as variações em `Assets/`, as imagens do instalador e o banner (`docs/banner.png`).

### Atualização automática
O GitHub Actions compila com `-p:UpdateRepo=<dono>/<repo>` e numera cada envio (`1.1.<número da execução>`); o app instalado confere a
release **latest** e se atualiza sozinho. Para uma versão "redonda", mude `<Version>` no `.csproj` e crie a tag (ex.: `v1.2.0`). `GERAR-INSTALADOR.bat` lê a versão do `.csproj`, então o instalador sai sempre com a mesma versão do app.
Cópias geradas no próprio PC (`GERAR-INSTALADOR.bat`) não têm o repositório e só avisam como atualizar.

## Observações
- Se algum sensor não aparecer, depende do suporte da biblioteca à sua placa-mãe.

## Já feito
- ✔ Informações do sistema, sensores, placa de vídeo, jogos, gargalo, overlay
- ✔ Stress test com limite de temperatura (CPU, RAM, GPU)
- ✔ Benchmarks: CPU, GPU, simulação, memória e **disco**
- ✔ Saúde dos discos (S.M.A.R.T.)
- ✔ Relatório exportável (HTML/JSON)
- ✔ OC (limite de energia, clock e verificação de estabilidade)
- ✔ Energia (bateria e fonte) e Rede (latência, perda e velocidade)
- ✔ INTEGRAÇÃO: iluminação Corsair iCUE, OpenRGB (MSI/Gigabyte/ASUS) e Razer Chroma (primeira versão, em teste)
- ✔ DRIVER: inventário de drivers, NVIDIA (verifica a versão, baixa, confere a assinatura e abre o instalador), AMD/ASUS/MSI/Gigabyte pela página oficial, Windows Update e backup (`DriverManager.cs`, primeira versão, em teste)
- ✔ GAME: inicializador com detecção de jogos, perfil por jogo e opções de inicialização da Steam (primeira versão, em teste)
- ✔ Inicialização do Windows: ativar, desativar e remover programas, com desfazer (v0.16)
- ✔ Abertura rápida: janela na hora, hardware carregado em paralelo (v0.16)
- ✔ Instalar apps essenciais pós-formatação pelo winget (v0.16)
- ✔ MAKER: portas COM e drivers seriais CH340/CP210x/FTDI/Prolific/Arduino (v0.16)
- ✔ TURBO: nota gamer, diagnóstico de hardware/BIOS/Windows/rede, guia da BIOS e correção em um clique (v1.0)
- ✔ SHIELD, Modo Jogo automático, perfis pro no driver NVIDIA, cache de shaders, instalador (v1.0)
- ✔ Teste de engasgos, gravação de frametime com CSV, rede (servidores e DNS), Limpeza do Windows, X3D/AMD, MUX e atualização automática (v1.0)

## Próximas ideias
- Comparar resultados de benchmark entre PCs / histórico de relatórios
- Leitura direta de SPD / Super I/O
- INTEGRAÇÃO: perfis salvos, efeitos por jogo, SDKs nativos da MSI/ASUS/Gigabyte se forem liberados
- Alertas de temperatura, bateria e saúde de disco em segundo plano
- Assinatura digital do instalador (certificado de código), para o SmartScreen não avisar no primeiro download

## Licença

MIT — veja o arquivo [LICENSE](LICENSE).
