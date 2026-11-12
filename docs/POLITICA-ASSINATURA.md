# Política de confiança: assinatura e integridade

Vale a partir da versão 1.9.0. O código está em `Infrastructure/BinaryTrust.cs` e `Infrastructure/VerifiedLaunch.cs`.

## O que é conferido, e quando

| O que | Integridade | Quem assinou | Momento |
|---|---|---|---|
| Atualização do tHg | SHA-256 publicado pelo GitHub (obrigatório) | mesma chave do tHg em execução, se ele for assinado | ao baixar e de novo logo antes de executar, com o arquivo travado |
| Driver de vídeo (instalação silenciosa) | - | fabricante (NVIDIA ou AMD) | logo antes de executar, com o arquivo travado |
| Driver de vídeo guardado (página Driver, DDU) | SHA-256 gravado ao baixar | fabricante | ao abrir, com o arquivo travado |
| Chipset AMD | - | Advanced Micro Devices | ao baixar e de novo logo antes de executar, com o arquivo travado |

"Travado" quer dizer aberto compartilhando só leitura: enquanto a conferência e a criação do processo acontecem, o Windows recusa escrever, apagar ou renomear o arquivo. O arquivo que executa é o arquivo conferido.

## Os quatro estados de uma assinatura

`BinaryTrust.Inspect` devolve um destes, e eles nunca se misturam:

- **Sem assinatura** (`Unsigned`): o arquivo não tem assinatura Authenticode.
- **Válida** (`Valid`): o Windows (WinVerifyTrust) aceitou a assinatura: conteúdo íntegro, cadeia até uma raiz confiável, dentro da validade ou com carimbo de tempo, não revogada.
- **Inválida** (`Invalid`): há assinatura e o Windows a recusou (arquivo alterado, raiz não confiável, expirada sem carimbo, revogada).
- **Não verificável** (`Unverifiable`): a conferência falhou por motivo técnico. Não se sabe se o arquivo é confiável.

Inválida e não verificável **nunca** viram "sem assinatura". Onde a decisão depende da assinatura, as duas recusam.

## Atualização do tHg

1. O SHA-256 publicado precisa existir, ter 64 dígitos hexadecimais e bater com o arquivo. Sempre, assinado ou não.
2. Instalador com assinatura inválida ou não verificável é recusado, mesmo num tHg sem assinatura.
3. **tHg sem assinatura** (build local, ou release publicada sem certificado): vale o SHA-256.
4. **tHg assinado:** o instalador precisa ter assinatura válida feita com a **mesma chave pública** do tHg em execução. A identidade é o SHA-256 do `SubjectPublicKeyInfo` do certificado que assinou. O texto do certificado (`Subject`) serve só para mostrar na mensagem: um certificado diferente com o mesmo nome é recusado.
5. Se a assinatura do próprio tHg estiver inválida ou não puder ser conferida, a atualização é recusada.

### Renovação e troca de certificado

- **Renovação com a mesma chave:** passa sozinha. O certificado novo tem outra impressão digital, mas a chave pública é a mesma.
- **Troca de chave:** exige uma versão-ponte. Publique uma versão assinada com a chave atual que inclua o SHA-256 da chave nova em `BinaryTrust.SuccessorKeys`. Quem instalar essa versão aceita a seguinte, assinada com a chave nova. A lista vale porque está dentro de um tHg cuja assinatura acabou de ser conferida.
- **Chave perdida sem versão-ponte:** os tHg assinados com a chave antiga recusam a atualização automática e pedem para baixar o instalador na página oficial. É o comportamento desejado: sem a chave antiga não há como provar a continuidade.
- **Passar de sem assinatura para assinado:** funciona sem versão-ponte (regra 3). A partir da primeira versão assinada instalada, vale a regra 4.

Na 1.9.0 a lista `SuccessorKeys` está vazia, e um teste garante isso.

## Situação da assinatura das releases

A assinatura no `publicar.yml` é opcional: só acontece se os segredos `SIGN_PFX_BASE64` e `SIGN_PFX_PASSWORD` existirem no repositório. O projeto ainda não tem certificado de assinatura de código (ver `LEIA-ME.txt`), e as releases saem sem assinatura. Nelas a integridade da atualização é garantida pelo SHA-256 que o GitHub calcula e publica para cada arquivo da release, lido por HTTPS da API do GitHub.

## Limites conhecidos

- O instalador de driver agendado para depois de reiniciar (fim da DDU com reinício) é conferido ao agendar, mas não há como mantê-lo travado durante a reinicialização. Ele fica na pasta protegida do tHg, onde só administradores escrevem.
- Drivers são aceitos pelo nome do fabricante no certificado (campo `O=` ou `CN=` exato) com cadeia validada pelo Windows, não por chave fixa: os fabricantes trocam de certificado sem aviso.
