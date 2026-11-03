# Release 1.6.0: relatório

Data: 03/11/2026. Execução local, a partir de `22bcf9d`, com repositório limpo na `main`.

## Resumo

- **Build e testes:** 0 erros e 0 avisos no build. Os testes passaram de 257 para 369, todos verdes.
- **Overclock não aplicado:** nenhum valor foi escrito em SMU, MSR ou driver. Todo o código novo de OC foi testado **só com simulação** e fica desligado no binário.
- **Interface não aberta:** o app exige administrador, este shell não é elevado e a versão instalada segura o mutex de instância única. O XAML foi validado por teste, mas **ninguém viu as telas novas funcionando**.
- **RAM não medida na 1.6.0:** ver [Métricas](#métricas).

## Hardware

| Item | Valor |
|---|---|
| Ambiente | PC local, Windows 11 Pro 26200, shell sem elevação |
| CPU | AMD Ryzen 7 9800X3D |
| GPU | AMD Radeon RX 9070 XT |
| Placa-mãe | X870 Pro-A WiFi |
| Overclock | **Não aplicado nesta execução** |

## Tarefas

Commits: `3880981` (T0, T56), `7e90aba` (T18, T19, T21 a T27), `cb8d180` (T20, T41 a T48, T50, T54, T57),
`1df0249` (T28 a T40, T49, T51, T53, T58 a T60) e `2d43723` (Seção 12 e teste de fumaça). Este relatório vem em um commit à parte.

| Tarefa | Situação | O que foi feito |
|---|---|---|
| T0 | Feita | `.gitignore` com `settings.json`, `.env` e `*.key` |
| T1–T17 | Já aplicadas | Commits `0eb9fde` a `22bcf9d`, com os símbolos presentes no código |
| T18 | Feita | `InterruptSteeringDisabled`, opcional, peso 0, marcado EXPERIMENTAL |
| T19 | Feita | Filas de mouse e teclado em 50, opcional, peso 0 |
| T20 | Feita | Aviso de risco e garantia no primeiro ajuste manual e na página OC |
| T21 | Feita | TURBO informa requisitos do Advanced Shader Delivery; não há interruptor para ligar |
| T22 | Feita | SHIELD confere a lista de bloqueio de drivers e a presença do WinRing0 |
| T23–T26 | Feitas | Core parking com peso 1; textos de MMCSS e HAGS revistos |
| T27 | Feita | Peso 0 desligado vira informação no TURBO; cópia ilegível não derruba o ajuste |
| T28–T30 | Feitas | Ordem de cores automática; dropdown só no Avançado |
| T31 | Feita | Auditoria de duplicação: `OcClockPanel` limpo e carga de hardware protegida; os demais já limpavam ou tinham trava |
| T32 | Sem mudança | Auditoria: ver [Desempenho](#desempenho-do-wpf) |
| T33–T36 | Feitas | Busca por ajuste, sem acento; badge APLICADO; contador |
| T37 | Sem mudança | Áreas, abas e Ctrl+K já existiam: não havia o que consolidar |
| T38–T40 | Feitas | `thg.log` com rotação e painel de memória |
| T41–T45 | Feitas, **só com simulação** | Limites, provedores, vigia de reversão, análise do setup |
| T46–T48 | Parciais | Protocolo e sondagem prontos; transporte real não existe (ver abaixo) |
| T49 | Feita | Memória devolvida com a janela escondida |
| T50 | Feita | `UpdateBottleneck` não coleta com a página fechada |
| T51 | Feita | `LightTick` sem alocar a cada quadro |
| T52 | Sem mudança | Listas ficam dentro do `ScrollViewer` da página ou em `UniformGrid`, o que impede virtualizar sem refazer o layout |
| T53 | Feita | `JsonSerializerOptions` reaproveitado |
| T54 | Feita | Registro de eventos e WMI da página OC fora da janela |
| T55 | Sem mudança | Coberta pela auditoria da T32 |
| T56 | Feita | NUL literal em `WindowsTweaks.cs` |
| T57 | Feita | Estouro de `uint` nos chips de teto de clock |
| T58–T60 | Feitas | Migração, novidades e handle liberado |
| T61–T75 | Não usadas | Reserva |

Nenhuma tarefa precisou ser revertida. Nenhum tweak foi removido.

## Bloqueado, com motivo

| Item | Motivo | Para destravar |
|---|---|---|
| Transporte real de SMU e MSR | Exige os módulos do PawnIO e teste em cada família. Escrever isso sem testar arriscaria o hardware dos usuários | Implementar `ISmuTransport` e `IMsrTransport` e testar em bancada |
| GPU AMD por ADLX | Sem ligação com o SDK; a interface só tem simulação | Criar a ligação e testar |
| BuildCores OpenDB | Licença CC BY-NC-SA 4.0 (não comercial, compartilha igual), em conflito com embutir num app MIT | Decisão do dono ou licença comercial |
| `SerializeTimerExpiration` | Não achei fonte confiável sobre o significado dos valores | Fonte oficial ou medição própria |
| Medição com dotnet-counters | Ver [Métricas](#métricas) | Rodar pelo procedimento abaixo |

## Pontos para revisar

1. **Tabela de RGB da Seção 2.3 não confirmada.** A ordem das cores depende da fita ou do fan, não só da marca da placa, e o OpenRGB pode já corrigir parte disso. Por isso o GRB vale só em conector endereçável de ASUS e Gigabyte, e quem atualiza começa com o automático desligado. Se o OpenRGB já corrigir, instalações novas nessas placas verão as cores trocadas até ajustar à mão. Falta testar em hardware.
2. **GPL-3.0.** Os endereços do SMU vieram do ZenStates-Core (GPL-3.0). Usei só fatos do protocolo, sem copiar código, mas vale sua avaliação, porque o tHg é MIT.
3. **`InterruptSteeringDisabled` não é documentado pela Microsoft.** O comentário de `WindowsTweaks.cs` promete só ajustes documentados; este e as filas são exceções pedidas, e estão marcados como EXPERIMENTAL e fora do TURBO.
4. **Deprecação na NVML.** Um tópico do fórum da NVIDIA diz que `nvmlDeviceSetGpcClkVfOffset` será substituída por `nvmlDeviceSetClockOffsets`. Não confirmei na página oficial.

## Métricas

| Métrica | Meta | Resultado |
|---|---|---|
| Repouso | até 80 MB | **Não medido na 1.6.0.** Referência: a 1.5.30 instalada tinha 54 MB e 72 MB de working set em duas leituras desta sessão, com `Get-Process` |
| Com jogo | até 150 MB | Não medido |
| Pico | até 400 MB | Não medido |
| Testes | todos verdes | 369 de 369 (eram 257) |

O ganho de memória da T49 é esperado, não comprovado. Para medir depois de atualizar: abra Configurações → Memória do app, ou rode `dotnet-counters monitor -n tHgOptimization` em um terminal de administrador. As devoluções de memória ficam em `thg.log`.

## Desempenho do WPF

| Técnica | Situação |
|---|---|
| `StaticResource` | **Não trocado.** `ThemeManager` substitui os pincéis em tempo real; com `StaticResource`, o tema claro/escuro deixaria de mudar na hora |
| Pincéis congelados | Já estão, nos pontos auditados |
| `DropShadow` | Só no cartão modal do `ThgDialog`, que não é atualizado |
| Storyboard contínua | Só o spinner, enquanto um botão está ocupado |
| Virtualização | `SensorGrid`, `LGameList` e `StressLog` já têm; demais listas, ver T52 |
| `ArrayPool` | Trocado por buffer reaproveitado no `LightTick`, mais simples para esse caso |

## Inventário de tweaks

| Tweak | Classificação | Decisão |
|---|---|---|
| Plano de energia | Atual | Mantido; já evita X3D com dois CCDs |
| Modo de Jogo | Atual | Mantido |
| HAGS | Refinável | Texto ajustado: depende do jogo e do driver |
| MMCSS | Refinável | Fora do TURBO; texto diz que costuma não mudar nada |
| Core parking | Refinável | Peso de 3 para 1 |
| Interrupções e filas de entrada | Gap | Adicionados como opcionais |
| Aviso de garantia | Gap | Adicionado |
| Advanced Shader Delivery | Gap | Informativo |
| `SerializeTimerExpiration` | Gap | Não adicionado |
| Demais ajustes | Atual | Sem mudança |

## Baseline de mercado consultada

- Advanced Shader Delivery: [Microsoft DirectX](https://devblogs.microsoft.com/directx/advanced-shader-delivery-is-available-now-across-hardware) (Windows 11 24H2; AMD Adrenalin 26.6.1, NVIDIA 621.01, Intel 32.0.101.9150; Store e app Xbox por enquanto).
- NVML: [referência da NVIDIA](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceCommands.html).
- SMU: [ZenStates-Core](https://github.com/irusanov/ZenStates-Core).
- Banco de hardware: [BuildCores OpenDB](https://github.com/buildcores/buildcores-open-db).
- Valores `InterruptSteeringDisabled` e `SerializeTimerExpiration`: **nenhuma fonte oficial encontrada**.
- Os demais itens das Seções 2.1 a 2.4 vieram do prompt e não foram reconferidos um a um.

## Migração de configurações

`SettingsVersion` passa a 2. Arquivos de versões anteriores ficam com `LightAutoOrder = false`, para as luzes não mudarem depois de atualizar. Instalações novas começam ligadas.

## Versão publicada

A primeira publicação apareceu como 1.6.31, porque o `publicar.yml` usa `Major.Minor.<número da execução>`. Isso foi corrigido em seguida: o usuário agora vê só **1.6**, no app, no instalador e na release. O número completo continua por dentro, porque o auto-update compara versões por ele, e fica visível nas dicas, nos diagnósticos e nos relatórios de falha. O título da release mantém o formato `1.6 (build 1.6.32)`, porque a 1.6.31 já instalada lê a versão nesse título.

Não dá para publicar uma 1.6.0 real para quem já atualizou: o `AppUpdater` ignora versões menores ou iguais à instalada. A tag `v1.6.0` não foi criada pelo mesmo motivo.

## Push

Feito com `git push origin main` depois da validação do checklist 13.3, logo após o commit deste relatório. A confirmação fica na mensagem final da execução, porque um relatório não contém o hash do próprio commit. **Esse push dispara o auto-update dos usuários.**

## `git log` antes deste relatório

```
2d43723 feat(1.6.0): T12 da Seção 12 - versão 1.6.0, changelog, novidades e teste de fumaça do XAML
1df0249 feat(1.6.0): T28-T40, T49, T51, T53, T58-T60 - RGB automático, busca, badges, registro e memória
cb8d180 feat(1.6.0): T20, T41-T48, T50, T54, T57 - OC experimental, análise do setup e aviso de risco
7e90aba feat(1.6.0): T18-T19, T21-T27 - tweaks novos, SHIELD e revisão pela baseline
3880981 fix(1.6.0): T0/T56 - .gitignore de segredos e byte NUL literal no fonte
22bcf9d T11: fonte Display pelo caminho completo do assembly
a3a9f5f T17: auditoria zero-overhead
10b8238 T16: overlay com acento ciano
cfd5b60 T15: cartão de destaque no painel, com sessão de jogo ao vivo
3bb6623 T14: LEDs de estado e alertas sem cartão invertido
bbb1217 T13: cartões, botões e chips na nova paleta
78f5c4c T12: menu lateral com borda de acento
deb3939 T11: tipografia display e estilos de métrica
677d374 T10: paleta gamer ciano no Theme.xaml e no ThemeManager
baa4ed1 T9: uma gravação das configurações por tick do Modo Jogo
1a635fd T8: menos lixo e menos trabalho no overlay
80188ea T7: programas de fundo por CPU Sets em vez de afinidade
5ea4746 T6: fim do Valorant pelo nome do PID
e91c2cd T5: uma leitura da lista de processos por tick do Modo Jogo
6bd631c T4: memória em espera não é esvaziada em partida ativa
ab06e30 T3: lista de captura já nasce no tamanho esperado
7d8a1e9 T2: controladora Lian Li aberta fora da janela
0eb9fde T1: FanTick fora da thread da janela
850530c OTIMIZA: não abre o Valorant nem o Vanguard ao enxugar a memória
177bc75 Testes: independentes da prioridade herdada e do tempo de saída do processo
c90c32c Modo Jogo: fim do Valorant e clientes da Riot pela lista do kernel
bd56a52 Launcher: -noborder na Unreal sem borda e -refresh para o CS2
aea4628 Modo Jogo: disco alto já na primeira abertura, com handle mínimo no jogo
721a3fd Modo Jogo: telas LCD a cada 3 s e janela sem desenhar com o tHg em segundo plano
e9a52b2 Frames: nome do processo sem abrir o jogo e métricas sem cópias de LINQ
```

## Próximos passos

1. Abrir a 1.6 e conferir as telas novas: card da página OC, RGB com Avançado, contador, Ctrl+K e novidades.
2. Medir a RAM em repouso, com jogo e no pico.
3. Testar a ordem automática de cores em placas ASUS e Gigabyte.
4. Decidir sobre OpenDB e GPL-3.0.
5. Só depois, implementar o transporte real do OC, testar em bancada e ligar a trava.
