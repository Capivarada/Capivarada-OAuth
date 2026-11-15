# E-mails de segurança do Capivarada

Os IDs ficam centralizados nas configurações da Function App — nunca no cliente
Unity ou em uma cena:

| App setting | Uso |
| --- | --- |
| `CAPIVARADA_EMAIL_TEMPLATE_LOGIN_ALERT` | aviso enviado após um login autenticado |
| `CAPIVARADA_EMAIL_TEMPLATE_ACCOUNT_DELETION` | confirmação para exclusão de conta |

## Modelos no PlayFab

Crie os dois modelos em **Game Manager > Content > Email Templates** e copie os
IDs para as app settings acima. Os HTMLs prontos estão em
[`email-templates/login-alert.html`](email-templates/login-alert.html) e
[`email-templates/account-deletion.html`](email-templates/account-deletion.html).
O envio requer que o SMTP add-on do título esteja configurado e que o jogador
tenha um *contact e-mail*; o cliente já o cadastra no registro.

### Aviso de login

Use um assunto inequívoco, por exemplo `Novo login na sua conta Capivarada`, e
explique que quem não reconhece o acesso deve trocar a senha. Não inclua senha,
ticket, PlayFabId ou outro segredo no corpo.

### Exclusão de conta

Crie este modelo como **Account Recovery**. O corpo precisa conter o link
`$ConfirmationUrl$`, por exemplo:

```html
<p>Recebemos uma solicitação para excluir sua conta Capivarada.</p>
<p>Esta ação remove o perfil, progresso e dados deste título e não pode ser desfeita.</p>
<p><a href="$ConfirmationUrl$">Confirmar exclusão da conta</a></p>
```

Configure o **Callback URL** como:

```
https://capivarada-functions-dev-d9djf2f2dqe4b9hu.westus2-01.azurewebsites.net/api/account/deletion/confirm
```

O PlayFab redireciona para esse callback acrescentando um token de e-mail de
curta duração. A Function valida o token com o PlayFab antes de chamar
`Server/DeletePlayer`; portanto digitar `EXCLUIR` no jogo não exclui nada por si
só. A exclusão remove a conta e dados do título Capivarada, não uma conta mestre
que o jogador eventualmente use em outros títulos.

## Publicação das configurações

Depois de salvar os templates, defina os dois IDs em **Function App >
Environment variables / Application settings** e reinicie a Function App. Sem o
ID do aviso de login, o endpoint retorna `202` sem enviar e-mail; sem o ID de
exclusão, a solicitação de exclusão retorna `503` e nada é removido.
