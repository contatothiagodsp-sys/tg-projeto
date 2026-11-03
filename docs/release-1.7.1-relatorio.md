# Release 1.7.1: relatório

Data: 2026-11-03. Base: v1.7.0 (`1b172f3`). Tarefas T116 a T125, com um commit por tarefa.

## Resumo

- **Todas as 10 tarefas foram implementadas.** Nenhuma foi revertida.
- **Build Release do zero:** 0 avisos e 0 erros, no app e nos testes.
- **Testes:** 910 verdes em 3 execuções seguidas (eram 830).
- **O app não foi aberto e nenhuma captura real foi feita.** O código da janela em `MainWindow.BenchLab.cs` compila, mas nenhum teste o executa.

## Conferido

| O quê | Como |
|---|---|
| Estatística e vereditos | Testes com dados sintéticos, valores de tabela da distribuição t e comparação do Mann-Whitney com força bruta |
| Regras do fluxo, inclusive o reinício | Testes de `BenchLabFlow` |
| Histórico | Ida e volta pelo JSON das configurações |
| Relatório | Testes do HTML, inclusive rótulos com HTML e caminhos |
| Gráfico e página Bench | Render do XAML em PNG, conferido a olho. O render pegou o dropdown mostrando `ThgOptimization.Tweak` |

## Falta conferir no app real

1. Capturar A, capturar B, ver o gráfico e o veredito (item obrigatório da validação pré-push).
2. Fazer 3 capturas de cada lado e exportar o relatório.
3. Testar o HAGS: medir antes, aplicar, reiniciar, medir, ver o veredito e reverter.
4. Usar Ctrl+Shift+F10 com um jogo em tela cheia exclusiva, e confirmar que o som toca.
5. Clicar numa linha do histórico depois de fechar e reabrir o app.

## Desvios da especificação

Em cada caso, seguir o texto literal daria um resultado errado.

**T117: 3 contra 3 usa o t de Student, não o Mann-Whitney.** O menor p bilateral exato do Mann-Whitney é 2 / C(n1+n2, n1). Com 3 contra 3 ele é 0,10, então o teste nunca daria significativo, justamente na metodologia recomendada de 3 capturas. Ele entra quando consegue chegar a 0,05, como em 3 contra 5 ou 4 contra 4.

**T117: uma captura de cada lado dá "Inconclusivo".** Sem variância não há t nem d. O veredito só aparece a partir de 3 capturas no total, e os deltas são mostrados sempre.

**T117: a amostra é uma medida por captura, não cada frame.** Frames vizinhos são correlacionados, e com milhares deles qualquer diferença mínima pareceria significativa. O teste usa o FPS médio de cada captura.

**T117: veredito extra.** A especificação não cobria um resultado significativo com d entre 0,2 e 0,5. Ele vira "Melhora pequena". Diferença não significativa negativa vira "Piora dentro do ruído".

**T116: campos a mais.** A captura guarda P99,9, teste e lado (A ou B). A comparação guarda método, p, número de capturas, data e caminho do relatório. O formato pedido continua igual.

**T121: o HAGS passa por um reinício.** Ele só vale depois de reiniciar, então medir logo após aplicar mediria a configuração antiga. O botão fica bloqueado até o reinício ser detectado, e o teste fica salvo. Ao reverter, se o valor guardado for o ajuste ligado, o Lab também o desliga, porque antes do teste ele estava desligado.

**T123: som além do Toast.** O `Toast` se cala de propósito em tela cheia D3D, para não minimizar o jogo. Um som marca o início, o fim e as falhas da captura.

**T125: chave do modal.** O modal era por Major.Minor, e quem já viu a 1.7 não veria o Lab. `WhatsNew.Editions` resolve isso sem usar o terceiro número, que o CI usa como contador. O CI publica como `1.7.<execução>`, e o rodapé e o atualizador continuam mostrando "1.7".

## Limitações

- O veredito olha só o FPS médio. Um ajuste que melhore apenas o 1% low aparece nos deltas, mas não muda o veredito.
- O d de Cohen é relativo ao ruído: com capturas muito estáveis, uma diferença pequena em FPS pode dar "Melhora significativa". O delta em % fica ao lado.
- Os CSVs das capturas não são apagados quando saem do limite de 200 sessões.
