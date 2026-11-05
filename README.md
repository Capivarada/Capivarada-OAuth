# Capivarada Functions

Backend do Capivarada hospedado no Azure Functions. Este repositório substitui o app de recuperação de senha que estava no Render.

## Endpoints publicados

| Método | Rota | Uso |
| --- | --- | --- |
| GET | `/api/health` | Verificação pública de disponibilidade. |
| GET | `/api/password/callback?token=...` | Página de redefinição recebida pelo e-mail do PlayFab. |
| POST | `/api/password/reset` | Redefine a senha usando o token temporário do PlayFab. |
| GET | `/api/static/Logo_Capivarada.png` | Logo usada exclusivamente na página de redefinição. |
| GET | `/api/static/reset-password.css` | Estilo compartilhado das páginas de recuperação. |
| GET | `/api/leaderboards/{name}?count=10` | Ranking V2 autenticado. Aceita `RankingMatches`, `RankingEliminations`, `RankingWins` e `RankingLevel`. |
| GET | `/api/leaderboards/{name}/me` | Linha do jogador autenticado no ranking V2. |
| GET | `/api/profiles/me/progression` | Totais de progressão usados pela UI de perfil. |
| GET | `/api/friends` | Lista de amigos do jogador autenticado. |
| GET | `/api/friends/requests` | Solicitações recebidas e enviadas. |
| POST | `/api/friends/request` | Cria solicitação por apelido ou PlayFab ID. |
| POST | `/api/friends/accept`, `/reject`, `/cancel`, `/remove` | Gerencia uma solicitação ou amizade. |
| POST | `/api/matches/settle` | Atualiza estatísticas e progressão. Exclusivo para o futuro servidor de partida. |

## Autenticação dos endpoints do jogo

Toda chamada da Unity que não for pública precisa enviar os dois cabeçalhos abaixo:

| Cabeçalho | Origem na Unity | Necessário em |
| --- | --- | --- |
| `X-PlayFab-SessionTicket` | `LoginResult.SessionTicket` | ranking, perfil e amigos |
| `X-PlayFab-EntityToken` | `GetEntityToken` após o login | ranking e progressão |

O backend valida o Session Ticket contra o PlayFab antes de fazer qualquer consulta. A `PLAYFAB_SECRET_KEY` permanece somente no Azure; ela nunca deve ser gravada ou enviada pelo cliente Unity.

## Configuração local

1. Copie `local.settings.example.json` para `local.settings.json`.
2. Preencha `PLAYFAB_TITLE_ID` e `PLAYFAB_SECRET_KEY` somente no arquivo local.
3. Execute `func start`.

`local.settings.json` é ignorado pelo Git. Nunca coloque a chave secreta do PlayFab em arquivos versionados, Unity ou no cliente.

## Configuração no Azure

No Function App `capivarada-functions-dev`, em **Configurações > Variáveis de ambiente**, crie:

| Nome | Valor |
| --- | --- |
| `PLAYFAB_TITLE_ID` | Title ID do Capivarada no PlayFab. |
| `PLAYFAB_SECRET_KEY` | Secret Key de servidor do PlayFab. |
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` |
| `LEADERBOARD_MATCHES` | `RankingMatches` |
| `LEADERBOARD_ELIMINATIONS` | `RankingEliminations` |
| `LEADERBOARD_WINS` | `RankingWins` |
| `LEADERBOARD_LEVEL` | `RankingLevel` |
| `CAPIVARADA_MATCH_SERVER_SECRET` | Segredo longo, exclusivo do futuro servidor de partidas. |

O endpoint `/api/matches/settle` exige o cabeçalho `X-Capivarada-Match-Secret` com esse segredo. Ele **não** deve ser chamado pela Unity enquanto não existir um servidor de partidas confiável.

Após publicar, configure a URL de recuperação de senha do PlayFab como:

`https://<dominio-padrão-do-function-app>/api/password/callback`

## Estado atual

Ranking V2, progressão, amizades e recuperação de senha já estão hospedados neste projeto. A importação/loja Economy V2 está intencionalmente pausada até a definição final do catálogo de acessórios. O cliente Unity não recebe nem armazena a Secret Key do PlayFab.
