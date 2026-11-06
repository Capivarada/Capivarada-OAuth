using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public sealed class FriendsFunction : AuthenticatedFunction
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly PlayFabServerClient playFab;
    private readonly NicknameIndexService nicknameIndex;
    public FriendsFunction(PlayerSessionService sessions, PlayFabServerClient playFab, NicknameIndexService nicknameIndex) : base(sessions) { this.playFab = playFab; this.nicknameIndex = nicknameIndex; }

    [Function("ListFriends")]
    public async Task<HttpResponseData> List([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "friends")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        try
        {
            JsonElement response = await playFab.CallAsync("GetFriendsList", new { PlayFabId = session!.Value.PlayFabId });
            List<FriendSummary> friends = new();
            if (response.TryGetProperty("Friends", out JsonElement values)) foreach (JsonElement item in values.EnumerateArray())
            {
                string? id = item.TryGetProperty("FriendPlayFabId", out JsonElement value) ? value.GetString() : null;
                if (!string.IsNullOrWhiteSpace(id)) friends.Add(await playFab.GetSummaryAsync(id));
            }
            return await Ok(request, new { friends });
        }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    [Function("ListFriendRequests")]
    public async Task<HttpResponseData> Requests([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "friends/requests")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        try
        {
            FriendRequestState state = await playFab.GetFriendStateAsync(session!.Value.PlayFabId);
            return await Ok(request, new { incoming = await Summaries(state.Incoming, true), outgoing = await Summaries(state.Outgoing, false) });
        }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    [Function("SendFriendRequest")]
    public async Task<HttpResponseData> Send([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "friends/request")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        FriendRequestInput? input = await Read<FriendRequestInput>(request);
        string targetInput = !string.IsNullOrWhiteSpace(input?.NicknameOrId) ? input!.NicknameOrId : input?.TargetPlayerId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetInput)) return await Bad(request, "Informe o ID ou apelido do jogador.");
        try
        {
            string lookup = targetInput.Trim();
            bool isPlayFabId = PlayFabServerClient.LooksLikePlayFabId(lookup);
            string? targetId = isPlayFabId
                ? await playFab.ResolvePlayerIdAsync(lookup)
                : await nicknameIndex.ResolveAsync(lookup);

            if (string.IsNullOrWhiteSpace(targetId))
            {
                string message = isPlayFabId
                    ? "Nenhuma conta foi encontrada com este ID PlayFab. Use o ID exibido no Perfil do jogador; Master ID e Entity ID não servem aqui."
                    : "Apelido não localizado. O jogador precisa abrir esta versão do jogo uma vez para registrar o apelido — não precisa ficar online.";
                return await Bad(request, message);
            }
            if (targetId == session!.Value.PlayFabId) return await Bad(request, "Você não pode adicionar a si mesmo.");

            if (await playFab.AreFriendsAsync(session.Value.PlayFabId, targetId))
                return await Bad(request, "Este jogador já está na sua lista de amigos.");

            FriendRequestState sender = await playFab.GetFriendStateAsync(session.Value.PlayFabId);
            FriendRequestState target = await playFab.GetFriendStateAsync(targetId);
            if (sender.Outgoing.Exists(x => x.TargetPlayerId == targetId)) return await Bad(request, "Já existe uma solicitação pendente para este jogador.");
            if (sender.Incoming.Exists(x => x.SenderPlayerId == targetId)) return await Bad(request, "Este jogador já enviou uma solicitação para você. Aceite ou recuse-a na aba de solicitações.");
            FriendRequestRecord record = new() { SenderPlayerId = session.Value.PlayFabId, TargetPlayerId = targetId };
            sender.Outgoing.Add(record); target.Incoming.Add(record);
            await playFab.SaveFriendStateAsync(session.Value.PlayFabId, sender); await playFab.SaveFriendStateAsync(targetId, target);
            return await Ok(request, new { requestId = record.RequestId });
        }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    [Function("AcceptFriendRequest")]
    public Task<HttpResponseData> Accept([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "friends/accept")] HttpRequestData request) => Process(request, true);
    [Function("RejectFriendRequest")]
    public Task<HttpResponseData> Reject([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "friends/reject")] HttpRequestData request) => Process(request, false);

    [Function("CancelFriendRequest")]
    public async Task<HttpResponseData> Cancel([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "friends/cancel")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        FriendRequestActionInput? input = await Read<FriendRequestActionInput>(request);
        try
        {
            FriendRequestState sender = await playFab.GetFriendStateAsync(session!.Value.PlayFabId);
            FriendRequestRecord? record = sender.Outgoing.Find(x => x.RequestId == input?.RequestId);
            if (record == null) return await Bad(request, "Solicitação enviada não encontrada.");
            FriendRequestState target = await playFab.GetFriendStateAsync(record.TargetPlayerId);
            FriendRequestStore.Remove(sender, record.RequestId); FriendRequestStore.Remove(target, record.RequestId);
            await playFab.SaveFriendStateAsync(session.Value.PlayFabId, sender); await playFab.SaveFriendStateAsync(record.TargetPlayerId, target);
            return await Ok(request, new { });
        }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    [Function("RemoveFriend")]
    public async Task<HttpResponseData> Remove([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "friends/remove")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        FriendRequestInput? input = await Read<FriendRequestInput>(request);
        string friendId = !string.IsNullOrWhiteSpace(input?.PlayerId) ? input!.PlayerId : input?.TargetPlayerId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(friendId)) return await Bad(request, "ID do amigo ausente.");
        try { await playFab.RemoveFriendAsync(session!.Value.PlayFabId, friendId); await playFab.RemoveFriendAsync(friendId, session.Value.PlayFabId); return await Ok(request, new { }); }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    private async Task<HttpResponseData> Process(HttpRequestData request, bool accept)
    {
        var (session, error) = await RequirePlayerAsync(request); if (error != null) return error;
        FriendRequestActionInput? input = await Read<FriendRequestActionInput>(request);
        try
        {
            FriendRequestState target = await playFab.GetFriendStateAsync(session!.Value.PlayFabId);
            FriendRequestRecord? record = FriendRequestStore.FindIncoming(target, input?.RequestId ?? string.Empty);
            if (record == null) return await Bad(request, "Solicitação recebida não encontrada.");
            FriendRequestState sender = await playFab.GetFriendStateAsync(record.SenderPlayerId);
            if (accept) { await playFab.AddFriendAsync(session.Value.PlayFabId, record.SenderPlayerId); await playFab.AddFriendAsync(record.SenderPlayerId, session.Value.PlayFabId); }
            FriendRequestStore.Remove(target, record.RequestId); FriendRequestStore.Remove(sender, record.RequestId);
            await playFab.SaveFriendStateAsync(session.Value.PlayFabId, target); await playFab.SaveFriendStateAsync(record.SenderPlayerId, sender);
            return await Ok(request, new { });
        }
        catch (Exception exception) { return await Fail(request, exception); }
    }

    private async Task<List<FriendRequestSummary>> Summaries(IEnumerable<FriendRequestRecord> records, bool incoming)
    {
        List<FriendRequestSummary> result = new();
        foreach (FriendRequestRecord record in records)
        {
            string playerId = incoming ? record.SenderPlayerId : record.TargetPlayerId;
            FriendSummary player = await playFab.GetSummaryAsync(playerId);
            result.Add(new FriendRequestSummary { RequestId = record.RequestId, PlayerId = playerId, Nickname = player.Nickname, CreatedAtUtc = record.CreatedAtUtc, Direction = incoming ? "incoming" : "outgoing" });
        }
        return result;
    }
    private static Task<T?> Read<T>(HttpRequestData request) => JsonSerializer.DeserializeAsync<T>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).AsTask();
    private static async Task<HttpResponseData> Ok(HttpRequestData request, object value) { HttpResponseData response = request.CreateResponse(HttpStatusCode.OK); response.Headers.Add("Content-Type", "application/json; charset=utf-8"); await response.WriteStringAsync(JsonSerializer.Serialize(value, Json)); return response; }
    private static async Task<HttpResponseData> Bad(HttpRequestData request, string message) { HttpResponseData response = request.CreateResponse(HttpStatusCode.BadRequest); await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, Json)); return response; }
    private static async Task<HttpResponseData> Fail(HttpRequestData request, Exception exception) { Console.Error.WriteLine(exception); HttpResponseData response = request.CreateResponse(HttpStatusCode.InternalServerError); await response.WriteStringAsync(JsonSerializer.Serialize(new { error = "Falha ao processar a solicitação social." }, Json)); return response; }
}
