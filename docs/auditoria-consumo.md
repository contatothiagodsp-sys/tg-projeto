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

Sem jogo, sem overlay e sem a página Sensores, depois de 5 s na bandeja, continuam ativos o timer principal (a cada 5 s), o Modo Jogo (3 s), o driver pendente (60 s) e a troca de perfil do teclado (3 s). Tudo que está rodando aparece em Configurações, no cartão O QUE O APP ESTÁ FAZENDO. Conferência manual, porque exige a janela real aberta.

## Última medição

Em 2026-11-03, no PC de desenvolvimento (Ryzen 7 9800X3D, RX 9070 XT), com o tHg 1.7.7 instalado aberto: 113 MB de memória em uso, 270 MB de memória privada e 0,16 % de CPU em 20 s. **A meta de 80 MB em repouso não está cumprida.** A CPU está dentro da meta. O consumo da 1.8.0 ainda precisa ser medido no app instalado.

## Comparação justa entre versões (2026-11-03)

A mesma janela, no mesmo teste (`MainWindow` sem o App, escondida há 75 s), que soma cerca de 67 MB de memória privada do próprio processo de teste:

| Versão | Em uso (WS) | Privada | Heap .NET |
| --- | --- | --- | --- |
| 1.7.7 | 286 MB | 198 MB | 21 MB |
| 1.8.0 | 302 MB | 218 MB | 23 MB |
| 1.8.0 com a coleta `Aggressive` | 257 MB | 173 MB | 23 MB |

- **Não compare com um processo aberto há horas.** O Windows vai tirando páginas pouco usadas, e o WS cai com o tempo: a 1.7.7 tinha 113 MB em uso e 270 MB privados, e a 1.8.0 recém-aberta tinha 376 MB em uso e 279 MB privados.
- **Os 54 a 72 MB da 1.5.30 não eram consumo menor.** Até a 1.6.2, o app empurrava as próprias páginas para o disco (`SetProcessWorkingSetSize`). O número caía, e cada tique as trazia de volta, com engasgo. Isso saiu na 1.6.3 e não deve voltar.
- **CPU com muito cache L3 segura mais memória.** O coletor dimensiona a geração nova pelo L3. Limitar com `DOTNET_GCgen0size` funciona, mas só por variável de ambiente: `System.GC.Gen0Size` e `System.GC.Gen0MaxBudget` no `runtimeconfig` são ignorados no .NET 8. `System.GC.ConserveMemory` não mudou nada, e desligar o PGO economiza só 7 MB à custa de desempenho.

## O que muda o consumo

- Na bandeja sem uso, o timer principal bate a cada 5 s e os sensores são lidos a cada 10 s.
- Com jogo em primeiro plano, a janela não redesenha nada e o painel só guarda amostras.
- A página Sensores só atualiza as linhas enquanto está aberta.
