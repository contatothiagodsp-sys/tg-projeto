# Backlog da 1.8.0

Só registro: nada daqui foi implementado na 1.7.0.

- **Latency Intelligence:** DPC e ISR por driver, apontando o culpado.
- **Smart Scheduler 2.0:** CPUs com mais de um grupo de processadores. Hoje o plano fica com o escalonador do Windows nesses casos.
- **Safe Overclock Center:** tela ligada aos provedores de `Core/Oc`, com o `OcRollbackWatchdog` conectado. Depende dos critérios de `OcGate.PendingCriteria`.
- **Hardware Scenes:** RGB, LCD e fans por jogo.
- **Input-to-Photon Latency:** latência do clique até a tela.
- **Recovery Center:** um lugar só para desfazer o que o tHg mudou.

## Pendências que vieram da 1.7.0

- Conferir no hardware as regras de GRB de `ColorOrderAuto.Regras`, aparelho por aparelho. Ver `docs/release-1.7.0-relatorio.md`.
- Medir com `dotnet-counters` as alocações de `GetStats`, Alt+Tab e polling do SMU no app real.
- Decidir se `FrameStats` e `FrameStatistics` viram struct: são os 280 bytes que ainda sobram por leitura.
- Classificar os catches vazios fora do caminho quente.
