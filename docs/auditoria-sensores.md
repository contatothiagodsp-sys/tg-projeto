# Auditoria manual dos sensores

Confere se a leitura funciona de verdade neste PC, sem depender da página de Sensores.

## Como rodar

Rode antes de publicar, com o app real fechado, em um terminal aberto como administrador:

```powershell
$env:THG_HARDWARE_TESTS = "1"
dotnet test tests/ThgOptimization.Tests -c Release --filter SensorSmokeTests
```

Sem `THG_HARDWARE_TESTS=1`, como no CI, o teste `Sensor_open_on_this_machine_returns_rows` aparece como ignorado.

## Checklist

- [ ] Resultado esperado: `Rows.Count > 0`, com o teste aprovado.
- [ ] Se falhar, o motivo está em `thg.log`, na área `[sensores]`. O arquivo fica em `%ProgramData%\tHgOptimization\users\<SID>`.
- [ ] Ao abrir o app, o `thg.log` tem as duas linhas da abertura:
  - `OpenAsync: X ms, IsOpen=True`
  - `Refresh completo: N linhas, CpuTemp=..., CpuLoad=..., GpuTemp=..., GpuLoad=..., PawnIO=True/True`
- [ ] A página Sensores mostra `N sensores · atualizado HH:mm:ss`.

## Estados da página

| Situação | O que aparece |
| --- | --- |
| Hardware ainda em detecção | `Detectando o hardware… (pode levar 5 s)` |
| Detecção terminou sem sensores | `Nenhum sensor encontrado. Clique em RODAR DIAGNÓSTICO.` e o botão |
| Detecção falhou | `Sensores indisponíveis: <motivo>` e o botão |
| Leitura normal | `N sensores · atualizado HH:mm:ss` |

O botão RODAR DIAGNÓSTICO mostra se o PawnIO está instalado e ativo, o diagnóstico da CPU e a versão do LibreHardwareMonitor.
