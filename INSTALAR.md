# Como instalar o tHg Optimization

## Só quero usar o app
Baixe e instale: **[tHgOptimization-Setup.exe](https://github.com/contatothiagodsp-sys/tg-projeto/releases/latest/download/tHgOptimization-Setup.exe)** (link fixo, sempre a versão mais nova).
Depois disso ele se atualiza pelo próprio app: a cada envio para a `main` sai uma versão nova, e o app avisa em até algumas horas
(ou na hora, clicando na versão no canto do menu). Se o Windows mostrar "O Windows protegeu o computador", clique em
**Mais informações → Executar assim mesmo**.

O resto desta página é para quem quer **compilar o código**.

---

Você recebeu o **código-fonte**. Para virar um instalador (`tHgOptimization-Setup-1.5.1.exe`) ele precisa ser compilado uma vez.
Há dois caminhos com o mesmo resultado: o **B** é um clique no seu PC; o **A** deixa o GitHub gerar a cada atualização.

---

## Caminho A · O GitHub compila para você (recomendado)

Você não instala nada de programação no PC. O GitHub gera o instalador sozinho a cada atualização do código.

1. **Crie uma conta** em [github.com](https://github.com) (se ainda não tiver).
2. **Crie um repositório**: botão **New** → nome `tHgOptimization` → *Private* ou *Public* → **Create repository**
   (deixe sem README, sem .gitignore e sem licença: o projeto já tem).
3. **Instale o Git** no Windows (uma vez só). Abra o *Prompt de Comando* e rode:
   ```
   winget install Git.Git
   ```
   Feche e abra o Prompt de novo.
4. **Extraia o `tHgOptimization-1.5.1.zip`** em uma pasta, por exemplo `C:\Projetos\`. Vai aparecer `C:\Projetos\tHgOptimization`.
5. **Envie o código** (troque `SEU-USUARIO` pelo seu usuário do GitHub):
   ```
   cd C:\Projetos\tHgOptimization
   git remote set-url origin https://github.com/SEU-USUARIO/tHgOptimization.git
   git push -u origin main
   ```
   Na primeira vez abre uma janela para entrar na sua conta do GitHub.
6. **Espere a compilação**: no site do repositório, aba **Actions**. O fluxo *Publicar* leva uns 5 minutos.
   Se ele falhar em "Atualizar release", vá em **Settings → Actions → General → Workflow permissions**, marque
   **Read and write permissions**, salve e clique em **Re-run jobs**.
7. **Baixe o instalador**: aba **Releases** (lado direito da página inicial do repositório) → **tHg Optimization (versão mais recente)** →
   `tHgOptimization-Setup.exe`.

Daqui para a frente, toda alteração enviada com `git push` gera um instalador novo na mesma release.

---

## Caminho B · Gerar no seu próprio PC (um clique)

1. Extraia o `tHgOptimization-1.5.1.zip` e abra a pasta `tHgOptimization`.
2. Dê **dois cliques em `GERAR-INSTALADOR.bat`**. Na primeira vez ele instala sozinho o .NET 8 SDK e o Inno Setup pelo winget
   (aceite as janelas de permissão), compila e abre a pasta `dist` com:
   - `tHgOptimization-Setup-1.5.1.exe`: o instalador;
   - `tHgOptimization.exe`: a versão portátil (roda sem instalar, de um pendrive por exemplo).
3. Se aparecer erro, rode o `.bat` de novo uma vez (às vezes o Windows só enxerga o .NET recém-instalado na segunda vez).

---

## Instalando (um clique)

1. Abra o `tHgOptimization-Setup.exe`.
2. Se o Windows mostrar **"O Windows protegeu o computador"**, clique em **Mais informações → Executar assim mesmo**
   (acontece com todo programa novo sem certificado de assinatura pago).
3. Clique em **Instalar**. É a única tela: a barra de progresso copia o app, cria os atalhos, instala o driver de sensores PawnIO
   e o tHg Optimization abre sozinho no fim, pedindo permissão de administrador (aceite).

## Primeiros 5 minutos no app

1. **TURBO** (menu da esquerda): veja a nota gamer. Clique em **TURBO: CORRIGIR** para o que o app arruma sozinho
   (cria um ponto de restauração antes). Itens de BIOS, como **XMP/EXPO**, trazem o caminho exato na sua placa-mãe e o botão **Reiniciar na BIOS**.
2. **SHIELD**: veja a nota de segurança e clique em **BLINDAR**.
3. **GAME → Modo Jogo**: clique em **LIGAR**. Com placa NVIDIA, confira se o monitor tem G-SYNC/FreeSync, marque os jogos e clique em
   **APLICAR PERFIS PRO**. Nos jogos com NVIDIA Reflex (CS2, Valorant, Fortnite, Apex…), deixe o Reflex em **Ligado + Boost** no menu do jogo.
4. Reinicie o PC uma vez para todos os ajustes valerem.
5. Jogue normalmente: o Modo Jogo mede cada sessão e, com o tempo, mostra o **antes e depois** do TURBO.

## Atualizar ou desinstalar

- **Atualizar sozinho** (caminho A, pelo GitHub): o tHg confere se há versão nova ao abrir e avisa. Clique na versão, no canto do
  menu, ou em *Procurar atualização* no menu da bandeja: ele baixa, instala em silêncio e abre de novo. Configurações e histórico ficam.
- **Atualizar à mão** (caminho B, gerado no PC): gere o instalador de novo e rode por cima; configurações e histórico ficam.
- **Desinstalar**: se quiser o Windows exatamente como antes, use primeiro **Desfazer tudo** (TURBO), **Desfazer** (SHIELD),
  **Desfazer tudo** (Limpeza do Windows), **Voltar ao DNS anterior** (Rede) e **Restaurar driver** (Modo Jogo). Depois: *Configurações → Aplicativos → tHg Optimization → Desinstalar*.

## Próximos passos (para distribuir para outras pessoas)

- **Assinatura digital**: um certificado de assinatura de código (ex.: Azure Trusted Signing ou um certificado OV) tira o aviso do SmartScreen.
  O instalador e o `.exe` podem ser assinados com `signtool` depois do `publicar.bat`.
- **Licença**: o projeto é baseado no RESET EXPLODE (licença MIT). Mantenha o arquivo `LICENSE` com os dois avisos de copyright
  ao distribuir: a licença permite usar, modificar e vender, desde que o aviso continue junto.
