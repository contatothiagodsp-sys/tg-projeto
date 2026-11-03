# Changelog

Histórico completo das versões anteriores em [NOVIDADES.md](NOVIDADES.md).

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
