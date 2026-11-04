# Auditoria manual de consumo

Metas: 80 MB em repouso, 150 MB com jogo aberto, 400 MB no pico. CPU em repouso abaixo de 2 % em 4 núcleos. Primeira pintura em menos de 1,5 s.

O CI não mede isto: depende do PC, da carga do momento e do app real aberto.

## Como medir

1. Abra Configurações, card Memória do app, e clique em MEDIR AGORA (10 s). O resultado também fica em `thg.log`, na área `[memória]`.
2. Para medir o repouso, deixe só na bandeja, sem jogo, sem overlay e sem a página Sensores, e compare no Gerenciador de Tarefas depois de 1 minuto. O botão mede com a janela aberta, que gasta mais que a bandeja.
3. Tempo de montagem da janela, a partir do XAML, com os outros programas fechados:

```powershell
$env:THG_HARDWARE_TESTS = "1"
dotnet test tests/ThgOptimization.Tests -c Release --filter ResourceBudgetTests
```

## Timers parados

Sem jogo, sem overlay e sem a página Sensores, depois de 5 s na bandeja: só o timer principal continua batendo, a cada 5 s. Conferência manual, porque exige a janela real aberta.

## O que muda o consumo

- Na bandeja sem uso, o timer principal bate a cada 5 s e os sensores são lidos a cada 10 s.
- Com jogo em primeiro plano, a janela não redesenha nada e o painel só guarda amostras.
- A página Sensores só atualiza as linhas enquanto está aberta.
