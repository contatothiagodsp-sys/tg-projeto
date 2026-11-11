---
name: graphify
description: Gera um único arquivo TXT (codigo_fonte_app.txt) com todo o código-fonte do projeto, para enviar a outra IA avaliar. Use quando o usuário digitar /graphify ou pedir para "juntar todo o código num txt", "exportar o código-fonte" ou parecido.
---

# /graphify: exportar o código-fonte num único TXT

Gera `codigo_fonte_app.txt` com todos os arquivos de texto do projeto, cada um
com cabeçalho, mais um índice no começo e a lista do que foi excluído.

## Como rodar

Na raiz do repositório:

```bash
python3 -I .claude/skills/graphify/scripts/graphify.py . "<saída>"
```

Escolha a `<saída>` assim:

1. Se o usuário indicou um caminho nos argumentos (`$ARGUMENTS`), use esse caminho.
2. Senão, se existir a pasta Downloads do usuário (`~/Downloads`, ou
   `%USERPROFILE%\Downloads` no Windows), grave em `~/Downloads/codigo_fonte_app.txt`.
3. Senão (por exemplo, sessão na nuvem, sem acesso ao computador do usuário),
   grave no diretório de rascunho (scratchpad) da sessão, ou em `/tmp` se não
   houver um, e entregue o arquivo como anexo com a ferramenta de envio de
   arquivos. Explique que não dá para gravar direto na pasta Downloads dele.

Nunca grave o TXT dentro do repositório nem faça commit dele.

## O que o script inclui e exclui

- Inclui: os arquivos versionados no git (`git ls-files`; sem git, percorre as
  pastas) que forem texto: código (`.cs`, `.xaml`, `.py`, `.js`, `.ts`...),
  configuração (`.csproj`, `.json`, `.yml`, `.iss`, `.manifest`...), scripts
  (`.bat`, `.sh`), docs (`.md`, `.txt`, `LICENSE`).
- Exclui: binários (imagens, fontes, `.ico`, `.bmp`, `.dll`, `.sys`, `.cat`,
  qualquer arquivo com byte nulo), lockfiles, `.log`, `*.min.js`, `*.map`,
  `.env` reais (`.env.example` entra), pastas de build/dependências
  (`node_modules`, `bin`, `obj`, `dist`, `build`, `.git`...), os `.inf` de
  drivers de terceiros em `Drivers/` e as licenças de fonte em `Assets/Fonts/`.

## Depois de gerar

Informe ao usuário: onde o arquivo ficou, o tamanho, quantos arquivos entraram
e um resumo do que ficou de fora (o próprio TXT tem a lista completa).
