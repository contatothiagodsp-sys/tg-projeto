# Melhorias de UX/UI — plano em 3 fases

Auditoria feita sobre a versão 1.4.0 (WPF .NET 8, `MainWindow.xaml` com ~4.100 linhas e 29 arquivos parciais `MainWindow.*.cs`).

## Ponto de partida (o que já existe)

| Área | Situação encontrada |
|---|---|
| Paleta | `Theme.xaml` + `ThemeManager` trocando pincéis via `DynamicResource` (escuro/claro, só manual) |
| Tipografia | Segoe UI Variable Text no corpo, Cascadia Mono nos números, porém **18 tamanhos de fonte diferentes** (9,5 / 10,5 / 11,5 / 12,5 / 15 / 22…) |
| Espaçamento | Valores soltos (3, 6, 10, 14, 30…), sem escala; margem de página repetida 30 vezes |
| Ícones | Já vetoriais: Segoe Fluent Icons (fallback Segoe MDL2). Nenhum emoji/bitmap como ícone |
| Janela | `WindowChrome` sem moldura, **cantos retos, sem sombra**, barra de título nativa sempre clara no Alt+Tab |
| Navegação | Menu lateral já agrupado (Explode, Jogos, Desempenho, Ferramentas, Maker) + busca |
| Feedback | `ToastWindow` próprio já existe; ainda há **39 diálogos** (`ThgDialog`/`MessageBox`) no código da janela |
| Acessibilidade | Só **14 de 118 botões** com `ToolTip`; nenhum `AutomationProperties.Name` (botões só de ícone ficam mudos no leitor de tela) |
| Erros | **67 lugares** mostram `ex.Message` cru para o usuário |
| MVVM | Só a página Processador (`CpuViewModel`); o resto é code-behind |

## Fase 1 — Modernização visual ✅ (implementada)

1. **Moldura Windows 11** (`UI/Services/WindowEffects.cs`): cantos arredondados do DWM (`DWMWA_WINDOW_CORNER_PREFERENCE`), borda fina na cor `Line` do tema (`DWMWA_BORDER_COLOR`), modo escuro nativo (`DWMWA_USE_IMMERSIVE_DARK_MODE`) e sombra suave (`GlassFrameThickness="0,0,0,1"`). No Windows 10 os atributos são ignorados sem erro.
2. **Tema automático**: novo `ThemeMode` = `system` | `dark` | `light`. Em `system` lê `AppsUseLightTheme` e acompanha a troca no Windows na hora (`SystemEvents.UserPreferenceChanged`). Botão da barra alterna Igual ao Windows → Escuro → Claro, com ícone do modo atual e dica explicando o próximo clique. Migração: quem tinha escolhido o claro continua no claro; os demais passam a seguir o Windows.
3. **Escala tipográfica e de espaçamento**: tamanhos consolidados em 10 · 11 · 12 · 13 · 14 · 16 · 20 · 24 · 28 · 30 · 44 · 64 (51 ajustes); margens e paddings ≥ 3 px arredondados para múltiplos de 4 (651 ajustes; 1–2 px mantidos para alinhamento fino). Tokens em `Theme.xaml`: `FontCaption`, `FontBody`, `FontBodyStrong`, `FontSubtitle`, `FontTitle`, `PagePadding`, `SectionGap`. Estilos `H1`, `Sub`, `Label` e as 30 páginas usam os tokens.
4. **Ícones**: já eram Segoe Fluent Icons. Mantidos; o botão de tema ganhou o ícone de monitor (E7F4) para "igual ao Windows".

**Mica/Acrylic — não aplicado de propósito**: a identidade do tHg é o preto puro (#000) com cartões #0E0E0E. Mica exige fundo translúcido em toda a janela (`GlassFrameThickness=-1` + `DWMWA_SYSTEMBACKDROP_TYPE`), o que deixaria o fundo cinza-azulado conforme o papel de parede e brigaria com os cartões opacos e com as capturas de tela das telas LCD. Se quiser, entra como opção "Efeito Mica" desligada por padrão na Fase 2.

## Fase 2 — Experiência do usuário ✅ (implementada)

1. **Navegação**: a barra lateral já era por áreas de tarefa (Início, Meu PC, Fans/telas/RGB, Jogos, Desempenho, Otimizar, Drivers e apps, Segurança, Avançado) com abas no topo e busca Ctrl+K — mantida. Faltava **Configurações**: tema, iniciar com o Windows, fechar para a bandeja e atualizações só existiam no menu da bandeja (escondidos). Nova área/página `PageSettings` (`MainWindow.Settings.cs`) com tudo isso + versão instalada, "Procurar agora", "Abrir pasta de dados" e "Relatar um problema". O menu da bandeja continua com os mesmos atalhos.
2. **Feedback visual** (`UI/Services/UiFeedback.cs`): todo botão que se desabilita no próprio clique (padrão das 34 ações demoradas do app: limpar, analisar, baixar driver, benchmark, DDU…) ganha um indicador girando no canto direito até voltar a ficar ativo — sem editar cada página. Aparece só depois de 300 ms (ações rápidas não piscam) e some se o botão voltar, sumir da tela ou for descarregado. Leitor de tela recebe o estado "Em andamento". As páginas que já tinham barra de progresso e texto de etapa (Drivers, DDU, Maker, benchmarks) continuam com eles.
3. **Toast no lugar de diálogo**: avisos informativos que interrompiam com janela ("nenhum ajuste alterado ainda", "ajustes restaurados", "já está na versão mais nova", "instalador de driver aberto") viraram Toast. Confirmações destrutivas (limpar, remover apps/drivers, DDU, restaurar) e erros que exigem atenção continuam como diálogo — de propósito.
4. **Simplificação**: configurações do app reunidas num lugar só (antes: botão na barra + menu da bandeja). Tema passou a ter 3 estados explícitos na página.
5. **Acessibilidade**:
   - `ToolTip` explicando o que acontece em **todos** os botões (110 adicionadas; antes só 14 de 118 tinham).
   - Nome para leitor de tela (`AutomationProperties.Name`) automático nos botões só de ícone, a partir da dica.
   - Anel de foco do teclado na cor de destaque (substitui o tracejado padrão do WPF, que quase não aparecia no preto).
   - Menu lateral: Tab entra e sai do menu num passo só, setas ↑↓ percorrem as áreas (com volta ao início).

Ficou para depois (sem ganho que justifique agora): Mica opcional; esqueleto (skeleton) por cartão — o spinner no botão + os textos de etapa já cobrem as esperas.

## Fase 3 — Recursos e lógica ✅ (implementada)

1. **Thread da interface**:
   - **Sensores** (o maior achado): `SensorMonitor.Refresh` rodava a cada 1 s na thread da janela; com discos e placa-mãe a leitura leva 20–200 ms, ou seja, a janela, o overlay e as telas LCD travavam um pouco todo segundo. Agora `RefreshAsync` lê o hardware numa tarefa de fundo e só atualiza as linhas na janela; leituras sobrepostas se juntam (uma leitura completa pedida durante uma leve espera e lê de novo tudo).
   - Trava única do hardware (`_hw`) entre leitura e comando das fans: o LibreHardwareMonitor não é seguro entre threads, e leitura + escrita simultâneas no Super I/O podiam trocar o banco do chip. O fechamento também passa pela trava.
   - Menu da bandeja e página Configurações: `schtasks` (iniciar com o Windows) saiu da thread da janela; o menu usa o último estado lido e abre na hora.
   - Nome da CPU (WMI) na abertura do app agora é lido em segundo plano.
   - O Modo Jogo já tinha sido movido para segundo plano no commit anterior. O resto das ações demoradas (63 handlers) já usava `Task.Run`/`await` — conferido.
   - MVVM: mantido o piloto (`CpuViewModel`). Converter as outras 30 páginas não muda desempenho (o trabalho pesado já está fora da janela) e exigiria testar cada página no PC; fica para quando cada página for mexida.
2. **Memória e eventos**: a `MainWindow` vive o app inteiro, então os 54 `+=` dela não vazam. Os pontos de risco real foram conferidos: `SystemEvents` (tela e tema) desinscritos ao fechar, `ThemeManager.Changed` desinscrito, timers locais param em `finally`. **Bug corrigido**: nos benchmarks de GPU, ray tracing e simulação, se a cena falhasse (driver de vídeo reiniciado), o timer de progresso ficava rodando e os sensores ficavam **pausados até fechar o app**; agora tudo é liberado em `finally` e o motivo aparece na tela.
3. **Erros amigáveis** (`Infrastructure/FriendlyError.cs` + `Err(ex)` na janela): 66 mensagens que mostravam o texto técnico do .NET (em inglês) agora dizem o que aconteceu e o que fazer — acesso negado/antivírus, arquivo aberto em outro programa, disco cheio, sem internet, site fora do ar ou limitando acessos, tempo esgotado, WMI sem resposta, placa de vídeo parou de responder, arquivo corrompido. Mensagens que o próprio tHg já escreve em português passam intactas. No caso desconhecido, frase genérica + "Detalhe" curto (para o suporte entender o relato).

## Como testar

- Windows 11: cantos arredondados, sombra e borda fina; ao maximizar, cantos retos (padrão do Windows).
- Configurações do Windows → Personalização → Cores → trocar Claro/Escuro com o tHg aberto em "igual ao Windows": muda na hora.
- Botão de tema na barra: três estados, dica descreve o próximo.
- Percorrer as páginas procurando texto cortado ou botões apertados após a nova escala.
- Área **Configurações** no fim do menu: trocar tema, iniciar com o Windows (confere na tarefa agendada), fechar para a bandeja, procurar atualização.
- Clicar em ações demoradas (ANALISAR do TURBO, VERIFICAR TUDO em Drivers, INICIAR de benchmarks): indicador girando no botão.
- Passar o mouse nos botões: todos com dica. Navegar só com Tab/setas: anel azul visível.
- Página Sensores e telas LCD: a janela não deve mais dar "soquinhos" a cada segundo (arrastar a janela ou rolar uma lista deixa isso visível).
- Fans com curva própria: confirmar que as rotações seguem a curva como antes.
- Botão direito no ícone da bandeja: o menu abre na hora.
- Desconectar a internet e clicar em VERIFICAR TUDO (Drivers): a mensagem explica o problema em português.
