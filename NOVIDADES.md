## tHg Optimization 1.5.0

**Visual no padrão do Windows 11**
- Janela com cantos arredondados, sombra suave e borda fina na cor do tema; barra de título escura/clara nativa no Alt+Tab.
- Tema "Igual ao Windows" (novo padrão), Escuro ou Claro: no primeiro, o tHg troca sozinho quando você muda o tema do
  Windows. Quem tinha escolhido o claro continua no claro.
- Tipografia e espaçamentos padronizados (escala de tamanhos única e margens em múltiplos de 4).

**Nova área Configurações**
- Tema, iniciar com o Windows, fechar no X para a bandeja, procurar atualizações automaticamente e "Procurar agora",
  versão instalada, pasta de dados e "Relatar um problema" — antes parte disso só existia no menu da bandeja.

**Retorno visual e acessibilidade**
- Ações demoradas (analisar, limpar, baixar driver, benchmarks, DDU…) mostram um indicador girando no próprio botão.
- Avisos que só informavam viraram notificação no canto da tela em vez de janela.
- Todos os botões têm dica explicando o que fazem; botões só de ícone têm nome para leitor de tela; anel de foco azul
  ao navegar com Tab; setas ↑↓ percorrem o menu lateral.

**Mais leve e mais fluido**
- A leitura dos sensores saiu da thread da janela: janela, overlay e telas LCD não dão mais um "soquinho" a cada segundo.
  Leitura e comando das fans passam por uma trava única (o chip das fans nunca recebe duas conversas ao mesmo tempo).
- Modo Jogo: a preparação ao abrir o jogo (plano de energia, serviços) roda em segundo plano, sem travar a janela;
  em notebooks com Modern Standby vai direto ao modo "Melhor desempenho" e devolve o modo anterior ao fechar o jogo;
  o próprio tHg entra em modo de eficiência (EcoQoS) enquanto o jogo roda.
- Menu da bandeja abre na hora (o estado de "Iniciar com o Windows" é lido em segundo plano).

**Correções**
- Benchmarks de GPU, ray tracing e simulação: se a cena falhasse (driver de vídeo reiniciado), os sensores ficavam
  pausados até fechar o app. Agora tudo volta ao normal e o motivo aparece na tela.
- Mensagens de erro em português claro, dizendo o que fazer (acesso negado/antivírus, arquivo em uso, disco cheio, sem
  internet, site fora do ar, tempo esgotado, placa de vídeo parou de responder…), no lugar do texto técnico em inglês.

## tHg Optimization 1.4.0

**Nova aba Meu PC → Processador (só leituras reais)**
- Ficha lida do Windows: nome, soquete, núcleos físicos, threads, virtualização na BIOS, família/modelo/stepping e
  revisão do microcódigo (registro `CentralProcessor\0`), e caches L1 (dados e instruções), L2 e L3 com a quantidade e
  o tamanho de cada uma (GetLogicalProcessorInformationEx). A página Sistema também passou a mostrar L1 e o microcódigo.
- Ao vivo, pelos sensores: temperatura do pacote/Tctl e de cada CCD, consumo do pacote (W), Core VID, Vcore, carga total
  e, por núcleo, clock, temperatura (quando a CPU tem sensor por núcleo) e uso de cada thread. Sensor sem leitura aparece
  como "—"; nada é estimado.
- Plano de energia ativo, core parking como está no plano e quantos processadores lógicos estão estacionados agora
  (contador do Windows). Botões para o benchmark, o stress test e os ajustes de núcleos.

**Iluminação RGB: OpenRGB abre sozinho**
- Se ninguém responde na porta 6742, o tHg procura o OpenRGB.exe (Arquivos de Programas, `%LocalAppData%\Programs`,
  pasta do app, Downloads e Área de Trabalho), abre em modo servidor (`--server --server-port 6742`) sem administrador
  e espera ele terminar de detectar os aparelhos antes de montar a lista. Se o OpenRGB já estiver aberto sem o
  servidor, a página diz onde ligar.
- Aviso de conflito quando o SignalRGB ou o L-Connect 3 estão abertos (eles seguram os controladores USB HID), com o
  botão "Fechar SignalRGB para liberar os dispositivos", que fecha o programa e reconecta tudo.

**Telas LCD sempre ativas durante o jogo, sem engasgo de GC**
- A atualização contínua das fans Lian Li TL LCD e da TURZX 2,8" continua durante o jogo.
- O quadro é desenhado sempre no mesmo bitmap e convertido em buffers reaproveitados de cada tela (JPEG, BGRA, RGB565);
  os pacotes HID de 512 bytes e os buffers da porta serial também são reaproveitados. Antes, cada segundo alocava
  ~1 MB por tela no heap de objetos grandes.
- Falha temporária de envio (porta COM ocupada, USB reiniciando) não tira mais a tela da atualização contínua: ela
  reconecta na rodada seguinte (depois de 3 falhas seguidas, tenta a cada 5 s). Vale para TURZX, Turing por COM e TL LCD.

**Modo Jogo: EcoQoS nos programas de fundo**
- Enquanto o jogo roda, navegadores (Chrome, Edge, Firefox, Opera, Brave, Vivaldi), launchers (Steam, Epic, Battle.net,
  EA, Ubisoft, GOG) e o Discord entram no modo de eficiência do Windows (EcoQoS); quem abrir depois também entra. Ao
  fechar o jogo, todos voltam ao controle normal. Opção na página do Modo Jogo (ligada por padrão).
- O jogo fica explicitamente fora do EcoQoS (HighQoS) durante a sessão; a prioridade alta de disco continua.

**Segurança, estabilidade e nitidez**
- Interface nítida em monitores com escala de 125%, 150% ou 200% e ao arrastar entre monitores diferentes (DPI por monitor).
- Atualização do app: além do SHA-256 publicado pelo GitHub, a assinatura digital do instalador novo tem de ser do mesmo
  emissor do tHg instalado.
- O instalador de driver baixado é conferido de novo (SHA-256 e assinatura da NVIDIA/AMD) logo antes de abrir, inclusive
  no fim da DDU.
- Downloads de drivers, da atualização e do SDK da Corsair tentam de novo sozinhos em falhas passageiras de rede e
  param de insistir num servidor fora do ar.
- Conectar o Razer Chroma e o OpenRGB não trava mais a janela.
- Se o tHg fechar com erro, na abertura seguinte ele oferece abrir um relatório no GitHub já preenchido (você revisa
  antes de enviar; seu nome de usuário do Windows é removido).

## tHg Optimization 1.3.0

**Organização nova: tarefas em vez de 30 itens soltos**
- A barra lateral agora tem **9 áreas**: Início, Meu PC, Fans/telas/RGB, Jogos, Desempenho, Otimizar, Drivers e apps,
  Segurança e Avançado. As páginas de cada área viram **abas no topo** (ex.: Otimizar → TURBO · Ajustes e memória ·
  Limpeza · Inicialização). Cada área lembra a última aba aberta.
- **Busca de funções (Ctrl+K)**: digite "xmp", "fps", "ch340", "widgets", "turzx"… e vá direto para a página. Enter abre
  o primeiro resultado, Esc limpa.
- As notas do TURBO e do SHIELD aparecem nas áreas **Otimizar** e **Segurança**.
- **Início virou central de ações**: o cartão "O que resolver" junta as pendências mais graves do TURBO e do SHIELD,
  sensores da CPU sem leitura, erros de hardware (WHEA/driver de vídeo) dos últimos 7 dias e portas COM presas. Cada
  item abre a página onde se resolve; o botão principal vira "Resolver no TURBO" ou "Resolver no SHIELD". Sem pendências,
  mostra "Nada pendente".
- **Diagnóstico USB** saiu da página das telas e foi para **Avançado** (é ferramenta de diagnóstico, não de uso diário).

**Telas LCD: cada tela com o seu conteúdo**
- Cada tela reconhecida vira um **cartão** com nome, resolução e estado real (conectada, em espera, imagem aplicada às
  HH:MM, erro com o motivo).
- As fans **UNI FAN TL LCD** são identificadas pela posição que a própria fan informa (porta da controladora e posição
  na corrente): "Fan 1 · porta 2", "Fan 2 · porta 2"… O nome não muda se o Windows reordenar os aparelhos.
- Selecione a tela e configure **imagem/GIF, encaixe, sensores, brilho e rotação só dela**; a prévia usa a resolução
  dessa tela (400×400 nas TL, 480×480 redonda na TURZX).
- **Aplicar nesta tela** envia só para a escolhida; **Aplicar em todas** envia o conteúdo de cada uma para a própria tela.
  A atualização contínua também respeita o conteúdo de cada tela.
- A configuração de cada tela fica salva; na primeira vez, cada tela herda a imagem usada na versão anterior.
- Receptor do UNI FAN **SL Wireless** (1CBE:0005) é reconhecido e avisado como ainda não suportado para imagem (o
  protocolo é outro e não foi validado).

## tHg Optimization 1.2.2

- **Tela TURZX redonda 2,1"/2,8" (do cooler)**: envio de verdade pelo protocolo "revisão C" (o mesmo das Turing 5" e 8,8"
  novas). A tela fica "dormindo" como 1A86:CA21 e acorda como outra porta COM (1D6B:0121): o tHg acorda sozinha, se
  identifica com ela, ajusta o brilho e envia a imagem em 480×480 (BGRA), com opção de girar 0/90/180/270°.
  Funciona também na atualização contínua (sensores ao vivo).

## tHg Optimization 1.2.1

- **Configuração do AMD Software para qualquer Radeon**: o gerador não depende mais do formato de uma versão do driver.
  Ele procura as opções de jogo em todos os arquivos do instantâneo exportado (RX 5000, 6000, 7000, 9000 e versões antigas
  e novas do Adrenalin), mantém o tipo de cada valor e a marca UTF-8 dos arquivos, e explica o que fazer quando o
  instantâneo vem sem as opções de jogo ou foi compactado de novo em .rar.

## tHg Optimization 1.2.0

**Telas: envio de verdade**
- **Fans Lian Li UNI FAN TL LCD** (04FC:7393): imagem, GIF e sensores direto nas telas das fans, todas de uma vez, com brilho.
  Protocolo HID da própria Lian Li (o mesmo do L-Connect), com confirmação de cada pacote e tempo limite de 2 s.
- **Telas Turing / TURZX por porta COM** (3,5"/5" em 1A86:5722 e 1A86:7523): protocolo serial "revisão A" (RGB565).
  Na TURZX redonda CT21INCH (1A86:CA21) o mesmo protocolo fica como **teste**: o fabricante não documenta o dela.
- **Atualização contínua**: 1 quadro por segundo em segundo plano (sensores ao vivo), também com o tHg na bandeja.

**Fans**: as portas da **controladora Lian Li UNI FAN TL** (0416:7372) entram na página Fans, com as mesmas curvas e
histerese da placa-mãe (o L-Connect 3 precisa estar fechado). Ao fechar o tHg, as portas ficam em 60%.

**Modo Jogo**
- Disco do jogo com prioridade alta; programas de fundo rebaixados pelo ProBalance também perdem prioridade de disco e de memória.
- Opção de pausar o SysMain e o Windows Search durante o jogo (religados ao fechar, inclusive se o tHg fechar no meio).

**OTIMIZA**: opção de desligar o MPO (Multiplane Overlay), para quem tem tela piscando ou engasgos em janela sem borda.

**OC (NVIDIA)**: 5 perfis de energia/teto de clock, aplicar o perfil 1 ao abrir o tHg e proteção: se o PC travar logo depois de
um ajuste ou o driver de vídeo cair (TDR), o perfil não é reaplicado e o tHg avisa.

## tHg Optimization 1.1.0

**Fans** (HARDWARE) · nova
- Curvas Silencioso, Equilibrado, Turbo ou Personalizada em cada saída de fan da placa-mãe, pela temperatura da CPU, da GPU ou da mais quente.
- **Histerese**: temperatura suavizada, subida rápida e descida só depois de a temperatura cair alguns graus abaixo do último pico
  por alguns segundos (tudo ajustável). Picos de 1 s não fazem mais as fans acelerarem e pararem.
- Segurança: sem leitura de temperatura ou CPU acima de 90 °C, 100%; ao fechar o tHg, as fans voltam ao controle da BIOS.

**Telas LCD e USB** (HARDWARE) · nova
- Diagnóstico USB: lista tudo com VID/PID, interface, driver e porta COM e reconhece telas TURZX/Turing e mais de 50 produtos
  Lian Li (fans UNI FAN, coolers Galahad/HydroShift, controladoras, dongles). "Salvar relatório" gera o arquivo para identificar
  o modelo e o protocolo.
- Estúdio da tela: imagem ou GIF animado, recorte automático para a resolução exata (redonda 480×480, 400×400, 320×480,
  800×480, 480×1920), sensores por cima (temperatura e uso da CPU e GPU, hora) e **prévia ao vivo**. Exporta JPEG e RGB565.
  O envio direto para a tela depende do protocolo de cada modelo (ver relatório de QA).

**Modo Jogo**
- Pausar programas pesados durante o jogo (ex.: SignalRGB, L-Connect, Overwolf): ficam congelados e voltam ao fechar o jogo.
- Monitor na taxa máxima (Hz) quando o jogo abre.
- Notificações do Windows silenciadas durante o jogo.
- Não trata mais o cliente do LoL (e outros launchers) como o jogo; não enxuga a memória dos processos nem esvazia o cache na
  abertura do jogo; memória em espera só esvaziada com a RAM realmente cheia.

**Tela**: HDR liga/desliga em um clique; proteção OLED (barra de tarefas escondida, tela desligando após 5 min), com desfazer.

**OTIMIZA**: modo MSI também para placas de rede e controladoras USB (com desfazer).

**RGB**: cores enviadas numa thread própria de baixa prioridade (a janela e o jogo nunca esperam um dispositivo lento).

**Desempenho e segurança**
- Stress test e benchmark de RAM com memória fora do .NET: liberada na hora ao parar (antes ficava retida, até 17 GB).
- Medidor de FPS sem gerar lixo de memória a cada leitura.
- Configurações, downloads e a DLL do iCUE em pasta que só administradores alteram; valores restaurados validados antes de
  virar comando; assinatura do fabricante comparada por inteiro; atualização só com SHA-256; jogos abertos sem administrador.
  Detalhes em docs/RELATORIO-QA-SEGURANCA.md.

**AMD**: chipset AMD instalado/atualizado sozinho; configuração pronta para importar no AMD Software; guias alinhados.

**Correções**: plano de energia (Desempenho Máximo criado e reconhecido pelo GUID real), Widgets em Windows 11 recente,
menu da bandeja na área de ícones ocultos, erros de compilação no WPF.

## tHg Optimization 1.0

O RESET EXPLODE agora é o **tHg Optimization**: nova marca, novo ícone e três frentes, **desempenho, segurança e diagnóstico**.
As configurações de quem já usava o RESET EXPLODE são copiadas na primeira abertura (inclusive os valores originais dos ajustes do Windows).

**TURBO** · nota gamer de 0 a 100
- Hardware: monitor abaixo da taxa máxima (**corrige com um clique**), cabo na placa-mãe em vez da placa de vídeo, single channel, pouca RAM,
  placa de vídeo em x8/x4, Windows em HD, pouco espaço, notebook na bateria.
- BIOS: **XMP/EXPO desligado** (lê a velocidade da etiqueta pelo código do pente) e **Resizable BAR**, com guia para ASUS, MSI, Gigabyte e ASRock,
  aviso de CSM/MBR e botão **Reiniciar na BIOS**.
- Windows e rede: ajustes do OTIMIZA, driver de vídeo antigo, perfis do driver, Modo Jogo, paginação, inicialização, Wi-Fi e cabo em 100 Mbps.
- **Corrigir tudo** com ponto de restauração e **Desfazer tudo**.

**SHIELD** · nota de segurança (novo)
- Antivírus e assinaturas, proteção contra adulteração, bloqueio de apps indesejados (mineradores escondidos em cheats), SmartScreen.
- Firewall, UAC, Windows Update, Secure Boot e TPM 2.0 (exigidos por anti-cheats como Vanguard e Javelin), Integridade de Memória,
  proteção do LSA, criptografia do disco, pontos de restauração, AutoRun de pendrive.
- SMBv1, Área de Trabalho Remota, Assistência Remota (golpe do falso suporte), Wi-Fi aberto ou WEP, pastas compartilhadas, conta Convidado.
- Feito para gamers: procura cheats, cracks e "geradores" em Downloads (e verifica com o Defender) e programas iniciando da pasta Temp
  ou por PowerShell disfarçado, o caminho típico dos ladrões de conta de Steam e Discord.
- **Blindar** liga o que é seguro ligar sozinho; nada é desligado e tudo tem desfazer.

**Modo Jogo** (GAME) · novo
- Automático: quando um jogo da biblioteca vai para a tela, sobe a prioridade, limpa a memória em espera, ativa alto desempenho e, se você quiser,
  fecha apps de fundo e prende o jogo nos núcleos com 3D V-Cache (Ryzen X3D) ou nos núcleos P (Intel híbrido). Ao fechar o jogo, tudo volta.
- **Perfis pro no driver NVIDIA**, por jogo, gravados pela NVAPI oficial: baixa latência, desempenho máximo, filtragem em alto desempenho e
  V-Sync/G-SYNC com a trava de FPS certa para o seu monitor. Jogos instalados são detectados (Steam, Epic, GOG, Ubisoft e Riot).
  Para AMD, a receita completa do Adrenalin.
- **Antes e depois**: cada sessão grava FPS médio e 1% low; depois do TURBO, a página compara jogo a jogo.
- **Cache de shaders**: limpa os caches do DirectX, NVIDIA, AMD e Intel e avisa quando o driver de vídeo foi atualizado.

**OTIMIZA** · 8 ajustes novos (13 no total), com cuidado para Ryzen X3D de dois CCDs e notebooks.

**DDU** (FERRAMENTAS) · nova
- Remove por completo o driver de vídeo NVIDIA, AMD ou Intel, como o Display Driver Uninstaller: dispositivos, pacotes do Windows, serviços,
  pastas do painel e cache de shaders, com backup antes.
- Modo de Segurança automático: o app reinicia nele, limpa e volta ao Windows normal sozinho.
- Impede o Windows Update de instalar um driver por conta própria até você instalar o novo ("Liberar Windows Update" desfaz).

**Visual** · avisos no tema do app, cores de estado (verde, amarelo, vermelho) nas notas, itens e variações, menu sem repetir "tHg".

**Bandeja do sistema** · nova
- Fechar a janela deixa o tHg rodando ao lado do relógio: Modo Jogo, overlay, atalho e avisos continuam funcionando.
- Clique no ícone abre; botão direito: Overlay, Modo Jogo, "Fechar o X para a bandeja", "Iniciar com o Windows" e Sair.
- A dica do ícone mostra a temperatura da CPU e da GPU. Abrir o tHg de novo traz a janela da bandeja.

**Overlay** · atalho configurável: clique no atalho na página Overlay e pressione o novo (padrão Ctrl + Shift + O).

**Drivers e DDU**
- Placas reconhecidas pelo ID mesmo sem driver: RX 5000 a 9000, RX 500, Vega, RTX 20 a 50, GTX 10 e 16.
- O driver baixado fica guardado: depois da DDU, o tHg oferece "Instalar agora" ao abrir.
- Depois que o driver é instalado, o tHg apaga sozinho o instalador e as pastas extraídas (C:\AMD, C:\NVIDIA), que somam de 1 a 3 GB.
  Botão "Apagar instaladores" na DDU para fazer na hora.

**Mais FPS e menos engasgos**
- Plano **Desempenho Máximo** (Ultimate Performance) em desktops; Alto desempenho em notebooks.
- **Placa de vídeo em modo MSI** (interrupções por mensagem), como o MSI Utility.
- **VRR em janela**: G-SYNC / FreeSync em jogos DirectX 11 em janela ou sem bordas.
- **Windows Update sem enviar para outros PCs**: sem picos de upload que aumentam o ping.
- Modo Jogo: **timer em 0,5 ms** enquanto joga (como o TimerResolution) e **memória em espera esvaziada** quando falta RAM livre
  (como o ISLC), sem tirar memória do jogo. A limpeza de RAM ao abrir o jogo não mexe mais no próprio jogo.
- Perfis NVIDIA: **cache de shaders ilimitado** em cada jogo.

**Nova marca** · logo do robô tHg e o letreiro THG OPTIMIZATION no app, no ícone, na bandeja e no instalador.

**Teste de engasgos** (DESEMPENHO) · novo
- Mede por 60 s quanto tempo cada driver segura o processador (DPC e interrupções), como o LatencyMon, e aponta o culpado:
  rede, Wi-Fi, vídeo, áudio, USB, disco, BIOS ou anti-cheat, cada um com a correção mais comum.
- O resultado entra no TURBO.

**Medição de FPS profissional**
- Botão **Gravar 60 s** (também no menu da bandeja): conta regressiva, grava todos os frames do jogo em foco e mostra FPS médio,
  **1% low, 0,1% low**, P99 e **engasgos** (frames acima de 2,5× a mediana), comparando com a gravação anterior do mesmo jogo.
- Cada gravação vira um CSV em Documentos\tHgOptimization\Gravacoes; as sessões do Modo Jogo também exportam para CSV.
- As sessões do Modo Jogo passam a medir 0,1% low e engasgos de todos os frames, não só de amostras.

**Modo Jogo**
- **Prioridade do perfil do jogo** respeitada, **CPU Sets** (o jogo fica nos núcleos certos sem travar a afinidade) e
  **ProBalance**: programas de fundo que monopolizam a CPU durante o jogo descem de prioridade e voltam ao fechar (OBS, Discord,
  anti-cheats e o Steam nunca são tocados).
- **DLSS mais novo forçado** pelo driver NVIDIA (placas RTX) nos jogos com DLSS, como o "DLSS Override" do app da NVIDIA.

**Mais leve em segundo plano** · com a janela fechada ou minimizada, ou durante o jogo, só CPU, GPU e memória são lidos, e com metade
da frequência. O medidor de FPS só liga quando algo precisa dele (overlay, Modo Jogo, Gargalo ou gravação).

**Rede para jogos**
- Ping (com variação) até AWS São Paulo, Google, Cloudflare, EUA e Europa, pela conexão TCP que passa por firewalls.
- **Comparador de DNS** com consultas reais aos domínios de Steam, Riot, Epic, Discord e outros; troca o DNS do adaptador com
  um clique e **volta ao anterior** (automático ou fixo) quando quiser.

**Limpeza do Windows** (FERRAMENTAS) · nova, tudo com desfazer
- Desliga Copilot, **Recall**, Widgets e notícias, telemetria (e o serviço DiagTrack), anúncios e apps instalados sozinhos, ID de
  publicidade, resultados do Bing na busca do Iniciar, Edge rodando escondido e apps da Store em segundo plano.
- Limpa temporários do usuário e do Windows, relatórios de erro, cache de entrega de atualizações, cache de shaders e a lixeira,
  mostrando o tamanho de cada um antes.
- Remove apps que vieram de brinde (Notícias, Dicas, Solitaire, Clipchamp, Candy Crush, TikTok, Disney+…) só da sua conta,
  com **Reinstalar removidos**. Xbox, Game Bar, Loja, Fotos e Calculadora nunca aparecem.

**Ryzen X3D e AMD**
- TURBO confere o **driver de chipset da AMD** contra a versão mais nova do site da AMD e **instala ou atualiza sozinho**
  (baixa de drivers.amd.com, confere a assinatura da AMD, instala em silêncio e apaga o instalador) e, nos X3D de dois CCDs (7900X3D, 7950X3D, 9900X3D, 9950X3D),
  o **AMD 3D V-Cache Performance Optimizer** e o plano Equilibrado que ele exige (corrige com um clique).
- **Guia de BIOS para X3D**: EXPO 6000 CL30 com FCLK 2000 (1:1), Curve Optimizer passo a passo, PBO nos 9000X3D e o "X3D Turbo Mode".
- **Resizable BAR / Smart Access Memory** agora detectado também em placas AMD e Intel.
- Placas Radeon: **configuração pronta para importar no AMD Software**: o tHg lê o instantâneo exportado pelo Adrenalin e devolve
  uma cópia com Anti-Lag, filtragem em Desempenho, Chill/Boost/Enhanced Sync/Fluid Motion Frames desligados e sem anúncios, sem mexer
  em CPU, BIOS, telas ou perfis; avisa quais jogos têm perfil próprio que ignora o global.
- Placas Radeon: **guia do AMD Software (Adrenalin)** para jogos (Anti-Lag, Chill, Boost, Enhanced Sync, FreeSync com trava de FPS,
  SAM) e, nas RX 9000, o **FSR 4**.

**Notebooks** · TURBO avisa quando a tela passa pelo vídeo integrado e mostra como ligar o **MUX** (modo dedicado) no app da
ASUS, Lenovo, MSI, Acer, Dell/Alienware, HP e Gigabyte.

**Atualização automática** · o tHg procura versão nova no GitHub (a cada 12 horas), avisa e atualiza com um clique: baixa, confere o
SHA-256 publicado pelo GitHub, instala em silêncio e abre de novo, mantendo as configurações. Também pelo menu da bandeja ou clicando na
versão no canto do menu.

**Instalador**: `tHgOptimization-Setup.exe` com atalhos, desinstalador e opção de instalar o PawnIO.

## Histórico · v0.16 (ainda como RESET EXPLODE)

**Nova página · FERRAMENTAS › Inicialização**
- Lista os programas que abrem junto com o Windows: chaves Run do registro (usuário, todos os usuários, 32 e 64 bits)
  e pastas Inicializar, com descrição, fabricante e aviso quando o arquivo do programa não existe mais.
- Interruptor para ativar/desativar, igual ao Gerenciador de Tarefas: a entrada continua lá e pode ser religada.
- Botão **REMOVER** com confirmação; o programa continua instalado e tudo pode ser desfeito em **Restaurar removidos**.

**Nova página · FERRAMENTAS › Instalar apps**
- Os essenciais depois de formatar o PC: Steam, Google Chrome, Discord, Lightshot, 7-Zip, WinRAR e VLC.
- Instala pelo winget (instalador de pacotes da Microsoft que já vem no Windows): versão oficial mais recente,
  sem janelas, um app de cada vez, com o resultado de cada um na lista.
- Mostra o que já está instalado; se o winget ainda não existir (PC recém-formatado), um botão abre a Microsoft Store
  para atualizar o "Instalador de Aplicativo".

**Nova categoria · MAKER** (Arduino, ESP32 e conversores USB-serial)
- **Portas COM**: a limpeza de dispositivos fantasmas e de números COM presos saiu de Otimização e ganhou página própria.
- **Drivers seriais**: diagnóstico das placas com CH340/CH341, CP2102/CP210x, FTDI, Prolific PL2303 e Arduino
  (sem driver, código 10, código 43, FTDI falsificado travado em PID 0000), com a orientação de cada caso.
- **REINSTALAR** remove o dispositivo e faz o Windows detectar de novo com o melhor driver.
- Lista as versões de driver guardadas no Windows para cada chip, com **REMOVER** (backup antes): volta para uma versão
  anterior quando a nova não funciona com clones, ou desinstala de vez. **Restaurar removidos** desfaz.
- **Drivers incluídos no app**: CH340 4.0, CP210x 11.6, FTDI 2.12.36.20 e Arduino, oficiais e assinados, instalam sem internet
  (ideal logo depois de formatar), com a placa conectada ou não. Também abre a página oficial de cada fabricante.
- **Arduino IDE**: instalar/atualizar pelo winget, fechar programas que prendem a porta COM e limpar o cache de índices
  quando a IDE trava em "Downloading index".

**Novo benchmark · Ray tracing (GPU)**
- Teste em Direct3D 12 com DXR 1.1 que usa os núcleos de ray tracing (RTX 20+, Radeon RX 6000+, Intel Arc): 4 mil objetos,
  reflexos de reflexos, sombras suaves, oclusão de ambiente e iluminação indireta, com a cena refeita a cada frame.
- Feito para comparar com confiança: aquecimento sem medir, 3 passadas iguais, nota pela mediana e variação entre passadas
  (selo de "resultado confiável" abaixo de 3%); tempo de frame medido pelo relógio da própria GPU.
- Presets Normal, Alta e Extrema em 1080p, 1440p e 4K; histórico comparando só resultados do mesmo preset e resolução.
- Referência: RX 9070, Alta 1440p = 878 pts (87,8 FPS), variação 0,5%.

**Overlay personalizável**
- Na aba Overlay, escolha o que aparece: FPS, frametime, 1% low, gráfico; GPU (uso, temperatura, hotspot, clock do núcleo,
  clock da memória, consumo, tensão, ventoinha, limitador NVIDIA); VRAM (usada, %, temperatura); CPU (uso, núcleo mais
  ocupado, temperatura, clock médio, clock máximo, consumo); RAM (GB, %); nome do jogo e hora. Atalhos SÓ FPS, PADRÃO e TUDO.
- Cada componente vira uma linha só com os itens escolhidos; a escolha fica salva e vale na hora.

**GAME**
- Novo atalho em Extras: **-allow_third_party_software** (CS2), que libera captura de jogo do OBS, ReShade e overlays
  bloqueados pelo modo confiável; o aviso sobre o Fator de Confiança aparece ao passar o mouse.

**Abertura mais rápida**
- A janela aparece em ~0,5 s (antes ~4,6 s). Informações do sistema, sensores e placa de vídeo carregam em paralelo,
  em segundo plano, e cada página é preenchida quando a sua parte fica pronta.
- O aviso "sem sensor da CPU" não aparece mais por engano enquanto o hardware ainda está sendo detectado.

**Correção · Overlay de jogos**
- O overlay agora fica por cima de jogos em janela sem borda que também se marcam como "sempre no topo"
  (ex.: GTA V e Cyberpunk 2077), inclusive depois de Alt+Tab.
- Aparece no monitor onde o jogo está, não só no monitor principal.
