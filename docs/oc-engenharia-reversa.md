# Overclock experimental: de onde vem cada valor

Estado na 1.6.3: `OcGate.RealHardwareEnabled` é `true` e os transportes reais existem, mas **nenhuma escrita foi executada em hardware real.**
O app não chama estes tipos: eles têm `[Experimental("THGOC001")]` e não há tela para isso.

## O que falta antes de uma tela chamar estes provedores

1. Teste em hardware real, família por família, com o `OcRollbackWatchdog` ligado a sensores e ao registro de eventos.
2. Módulo do PawnIO com escrita de MSR. O `IntelMSR` da LibreHardwareMonitorLib 0.9.6 só tem `ioctl_read_msr`, e o de origem já tem `ioctl_write_msr`.
3. Primeira escrita de offset na GPU AMD, com o vigia armado. Só as leituras do ADLX foram conferidas em hardware.
4. Confirmar que o `SensorMonitor` acha o VSOC em cada placa. Sem leitura, o limite de 1,25 V não tem como bloquear.

## AMD: caixa de mensagens do SMU

Fonte: [ZenStates-Core](https://github.com/irusanov/ZenStates-Core) (`Hardware/SMU.cs`, `Hardware/Smu/Settings/Zen3Settings.cs`,
`Zen4Settings.cs`, `Zen5Settings.cs`, `Hardware/Smu/Commands/SetPsmMarginAllCores.cs`), consultado em 03/11/2026.
O projeto é GPL-3.0. Daqui vieram só fatos do protocolo (endereços, números de comando, ordem das escritas); nenhum código foi copiado.
Vale uma revisão do dono antes de distribuir, porque o tHg é MIT.

| Família | Caixa | MSG | RSP | ARG | Margem em todos os núcleos | Ler a margem de um núcleo |
|---|---|---|---|---|---|---|
| Zen 3 (Vermeer) | MP1 | `0x3B10530` | `0x3B1057C` | `0x3B109C4` | `0x36` | sem leitura simples: o tHg recusa |
| Zen 4 (Raphael) | RSMU | `0x03B10524` | `0x03B10570` | `0x03B10A40` | `0x7` | `0xD5` |
| Zen 5 (Granite Ridge) | RSMU | herdado do Zen 4 | herdado | herdado | herdado | herdado |

Leitura: o argumento é a máscara do núcleo, `(ccd << 28) | (núcleo << 20)`, então o núcleo 0 do CCD 0 é `0`. A resposta volta no primeiro argumento.

Ordem de envio: espera RSP diferente de 0, zera RSP, grava os argumentos, grava MSG, espera RSP e lê o status (`0x01` = OK).
Margem: 16 bits baixos do argumento, em complemento de dois.

A faixa do tHg é de -30 a 0, mais estreita que a do hardware: só undervolt.

## Intel: MSR 0x150

Codificação conhecida de ferramentas abertas de undervolt (por exemplo, intel-undervolt): `0x8000001100000000 | plano << 40 | offset`,
com offset de 11 bits com sinal a partir do bit 21, em passos de 1/1024 V. Plano 0 = núcleo, plano 2 = cache.
Exemplo conferido nos testes: -125 mV no núcleo = `0x80000011F0000000`.

Muitas CPUs bloqueiam esse MSR por causa do Plundervolt. O provedor confere por leitura e desfaz se o valor não bater.

## NVIDIA: NVML

`nvmlDeviceSetClockOffsets` e `nvmlDeviceGetClockOffsets`, com a struct `nvmlClockOffset_v1_t` da [referência da NVML](https://docs.nvidia.com/deploy/nvml-api/structnvmlClockOffset__v1__t.html).
Driver sem elas cai em `nvmlDeviceSetGpcClkVfOffset` e `nvmlDeviceGetGpcClkVfOffset`. Exigem administrador.
Um [tópico do fórum da NVIDIA](https://forums.developer.nvidia.com/t/nvmldevicegetminmaxclockofpstate-nvmldevicesetclockoffsets-issues/318332) relata que o offset vale para todos os P-states e que o getter devolvia 0 antes do driver 570.
O valor da versão da struct (`24 | 1 << 24`) vem do meu conhecimento do `nvml.h`: não foi conferido no cabeçalho. Sem GPU NVIDIA aqui, nada disso rodou.

## AMD GPU: ADLX

`amdadlx64.dll`, carregada só da pasta do sistema. Vtables conferidas no [SDK do ADLX](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX) (`SDK/Include`, versão 2.0.0.125): `IADLXSystem`, `IADLXGPUList`, `IADLXGPUTuningServices`, `IADLXManualGraphicsTuning2_1`, `IADLXPerformanceMonitoringServices` e `IADLXGPUMetrics`.
A versão passada ao `ADLXInitialize` vem do `ADLXQueryFullVersion`: o driver desta máquina traz a DLL 1.5.0.124, mais antiga que o SDK consultado.

O cabeçalho diz: "Start from Navi4+, the maximum frequency is an offset from the base frequency". O offset é `GetGPUMaxFrequency - GetGPUMaxFrequencyDefault`, o que vale também para as gerações em que o valor é um clock absoluto.
`ADLX_RESET_NEEDED` aparece com o Auto Tuning ligado. O exemplo da AMD chama `ResetToFactory` nesse caso; o tHg recusa, porque isso apagaria os outros ajustes do usuário.

## WHEA e TDR

`HardwareErrorMonitor` assina o log System com os mesmos critérios de `OcCheck`: `Microsoft-Windows-WHEA-Logger`, `Display` 4101, `nvlddmkm` 13 e 153, e qualquer evento de `amdkmdag`. Esse último critério é largo de propósito: um evento informativo do driver também desfaz o ajuste.

## Driver: PawnIO, nunca WinRing0

O tHg já usa o PawnIO para ler sensores. Nenhum provedor usa WinRing0, e o SHIELD agora avisa quando esse driver está instalado.

`PawnIoTransport` abre `\\.\PawnIO` (o PawnIO marca esse nome como obsoleto, então `\\?\GLOBALROOT\Device\PawnIO` fica de reserva) e usa os códigos de [`pawnio_um.h`](https://github.com/namazso/PawnIO/blob/master/PawnIO/include/pawnio_um.h):
`IOCTL_PIO_LOAD_BINARY` = `0xA1B22084` e `IOCTL_PIO_EXECUTE_FN` = `0xA1B22104`. A entrada são 32 bytes com o nome da função, seguidos dos valores de 64 bits.
Os módulos vêm embutidos na LibreHardwareMonitorLib. Funções usadas, de [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules): `ioctl_read_smu_register` e `ioctl_write_smu_register` (`RyzenSMU`, restritas a uma lista de endereços que inclui a página `0x3B10xxx`), `ioctl_read_msr` e `ioctl_write_msr` (`IntelMSR`).
Nada aqui grava em BIOS ou flash.

## Banco de hardware

O [BuildCores OpenDB](https://github.com/buildcores/buildcores-open-db) é CC BY-NC-SA 4.0 (não comercial, compartilha igual).
Embutir esses dados num app MIT é uma decisão de licença do dono, então a análise do setup usa uma tabela própria, por família.

## Limites (Seção 10.8)

| Componente | Temperatura | Outro limite |
|---|---|---|
| CPU AMD | 90 °C | 1,35 V e VSOC de até 1,25 V |
| CPU Intel | 95 °C | 1,35 V |
| GPU NVIDIA | 83 °C | de 0 a +300 MHz |
| GPU AMD | 85 °C | de 0 a +250 MHz, até +300 com confirmação |


## Auditoria da 1.6.6

- `OcGate.RealHardwareEnabled` voltou a `false`. Os transportes conferem a trava também: PawnIO não abre o driver, e ADLX e NVML não recebem escrita. Para abrir, cumprir `OcGate.PendingCriteria`.
- ADLX conferido com os headers oficiais do SDK (repositório GPUOpen-LibrariesAndSDKs/ADLX, ramo main, em 2026-11-03): todos os slots usados estão certos, `ADLX_RESET_NEEDED` é 18, as exportações são cdecl e os métodos stdcall, `adlx_bool` tem 1 byte. O driver instalado pode ser de outra versão do SDK, e por isso `IADLXManualGraphicsTuning2_1` é pedida por QueryInterface.
- Corrigido no ADLX: resposta de erro com ponteiro preenchido sem Release, leitura de offset e de temperatura sem reabrir depois de TDR, soma do offset sem proteção contra estouro, e DLL carregada sem conferir a assinatura.
- A confirmação pós-escrita vale também para desfazer: `Restored` só depois de ler o valor de volta. `Restore` sem ajuste anterior não grava nada.
- Escrita real continua sem teste em hardware.
