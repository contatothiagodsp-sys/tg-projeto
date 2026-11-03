# Changelog

Histórico completo das versões anteriores em [NOVIDADES.md](NOVIDADES.md).

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
