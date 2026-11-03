# Overclock experimental: de onde vem cada valor

Estado na 1.6.0: **implementado e testado só com simulação. Nenhum valor abaixo foi executado em hardware real pelo tHg.**
O app não chama estes tipos: eles têm `[Experimental("THGOC001")]`, e `OcGate.RealHardwareEnabled` é `false`.

## O que falta antes de ligar

1. Transporte real do SMU e do MSR pelo PawnIO. Não existe nesta versão: `ISmuTransport` e `IMsrTransport` só têm simulação.
   É preciso carregar os módulos assinados do PawnIO e segurar o mutex global de PCI.
2. Ligação com o ADLX (AMD GPU). `IAmdOverdriveTransport` também só tem simulação.
3. Teste em hardware real, família por família, com o `OcRollbackWatchdog` ligado a sensores e ao registro de eventos.
4. Só então trocar `OcGate.RealHardwareEnabled` e criar uma interface de usuário para isso.

## AMD: caixa de mensagens do SMU

Fonte: [ZenStates-Core](https://github.com/irusanov/ZenStates-Core) (`Hardware/SMU.cs`, `Hardware/Smu/Settings/Zen3Settings.cs`,
`Zen4Settings.cs`, `Zen5Settings.cs`, `Hardware/Smu/Commands/SetPsmMarginAllCores.cs`), consultado em 03/11/2026.
O projeto é GPL-3.0. Daqui vieram só fatos do protocolo (endereços, números de comando, ordem das escritas); nenhum código foi copiado.
Vale uma revisão do dono antes de distribuir, porque o tHg é MIT.

| Família | Caixa | MSG | RSP | ARG | Margem em todos os núcleos |
|---|---|---|---|---|---|
| Zen 3 (Vermeer) | MP1 | `0x3B10530` | `0x3B1057C` | `0x3B109C4` | `0x36` |
| Zen 4 (Raphael) | RSMU | `0x03B10524` | `0x03B10570` | `0x03B10A40` | `0x7` |
| Zen 5 (Granite Ridge) | RSMU | herdado do Zen 4 | herdado | herdado | herdado |

Ordem de envio: espera RSP diferente de 0, zera RSP, grava os argumentos, grava MSG, espera RSP e lê o status (`0x01` = OK).
Margem: 16 bits baixos do argumento, em complemento de dois.

A faixa do tHg é de -30 a 0, mais estreita que a do hardware: só undervolt.

## Intel: MSR 0x150

Codificação conhecida de ferramentas abertas de undervolt (por exemplo, intel-undervolt): `0x8000001100000000 | plano << 40 | offset`,
com offset de 11 bits com sinal a partir do bit 21, em passos de 1/1024 V. Plano 0 = núcleo, plano 2 = cache.
Exemplo conferido nos testes: -125 mV no núcleo = `0x80000011F0000000`.

Muitas CPUs bloqueiam esse MSR por causa do Plundervolt. O provedor confere por leitura e desfaz se o valor não bater.

## NVIDIA: NVML

`nvmlDeviceGetGpcClkVfOffset` e `nvmlDeviceSetGpcClkVfOffset`, da [referência da NVML](https://docs.nvidia.com/deploy/nvml-api/group__nvmlDeviceCommands.html).
Um tópico do fórum da NVIDIA cita a NVML dizendo que essas chamadas serão substituídas por `nvmlDeviceSetClockOffsets`; não confirmei na página oficial.
Revise isso antes de ligar. Exigem administrador.

## Driver: PawnIO, nunca WinRing0

O tHg já usa o PawnIO para ler sensores. Nenhum provedor usa WinRing0, e o SHIELD agora avisa quando esse driver está instalado.
Nada aqui grava em BIOS ou flash.

## Banco de hardware

O [BuildCores OpenDB](https://github.com/buildcores/buildcores-open-db) é CC BY-NC-SA 4.0 (não comercial, compartilha igual).
Embutir esses dados num app MIT é uma decisão de licença do dono, então a análise do setup usa uma tabela própria, por família.

## Limites (Seção 10.8)

| Componente | Temperatura | Outro limite |
|---|---|---|
| CPU AMD | 90 °C | 1,35 V |
| CPU Intel | 95 °C | 1,40 V |
| GPU NVIDIA | 83 °C | +300 MHz |
| GPU AMD | 85 °C | +10% |
