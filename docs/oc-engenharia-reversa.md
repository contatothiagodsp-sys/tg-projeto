# Overclock experimental: de onde vem cada valor

Estado na 1.6.3: `OcGate.RealHardwareEnabled` é `true` e os transportes reais existem, mas **nenhuma escrita foi executada em hardware real.**
O app não chama estes tipos: eles têm `[Experimental("THGOC001")]` e não há tela para isso.

## O que falta antes de uma tela chamar estes provedores

1. Teste em hardware real, família por família, com o `OcRollbackWatchdog` ligado a sensores e ao registro de eventos.
2. Módulo do PawnIO com escrita de MSR. O `IntelMSR` da LibreHardwareMonitorLib 0.9.6 só tem `ioctl_read_msr`, e o de origem já tem `ioctl_write_msr`.
3. Conferir o Overdrive 8 em cada geração de GPU AMD. Se o padrão do clock máximo não vier em MHz, o transporte recusa.
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

## AMD GPU: ADL Overdrive 8

`atiadlxx.dll`, com as structs de `adl_structures.h` e os ids de `adl_defines.h` do [display-library](https://github.com/GPUOpen-LibrariesAndSDKs/display-library) (`OD8_COUNT` = 77, `OD8_GFXCLK_FMAX` = 0).
O offset em % é a diferença entre `OD8_GFXCLK_FMAX` e o padrão informado pelo driver.

Leitura real numa RX 9070 XT (driver atual): `Init` e `Current` respondem com `count` = 76, as variantes `X2` devolvem -9, e `OD8_GFXCLK_FMAX` vem como (mín -500, máx 1000, padrão 0, atual 0). Ou seja: nessa geração o valor é **um offset em MHz, não um clock**, e não há base para calcular %. O transporte recusa em vez de adivinhar. Para ajustar essas placas, o provedor precisa trabalhar em MHz.
 O layout de `AdapterInfo` e os parâmetros de `ADL2_Main_Control_Create` e `ADL2_New_QueryPMLogData_Get` não foram conferidos na fonte: vieram do conhecimento geral da biblioteca.

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
| GPU NVIDIA | 83 °C | 0 ou de +100 a +300 MHz |
| GPU AMD | 85 °C | +10% |
