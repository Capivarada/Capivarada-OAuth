using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Owns party matchmaking tickets. Unity never receives a PlayFab title token,
/// and cancellation only affects the ticket created for this party.
/// </summary>
public sealed class MatchmakingFunction : AuthenticatedFunction
{
    private const int MinPlayers = 1;
    private const int MaxMatchPlayers = 8;
    // PlayFab requires MaxTicketSize to be strictly less than MaxMatchSize.
    private const int MaxTicketPlayers = MaxMatchPlayers - 1;
    private static readonly TimeSpan SearchWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan JoinLifetime = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly PlayFabServerClient playFab;
    private readonly PlayFabTitleEntityClient multiplayer;
    private readonly MatchAllocationStore allocations;
    private readonly MatchJoinTokenService tokens;
    private readonly IConfiguration configuration;
    private readonly MpsCapacityService capacity;

    public MatchmakingFunction(
        PlayerSessionService sessions,
        PlayFabServerClient playFab,
        PlayFabTitleEntityClient multiplayer,
        MatchAllocationStore allocations,
        MatchJoinTokenService tokens,
        IConfiguration configuration,
        MpsCapacityService capacity) : base(sessions)
    {
        this.playFab = playFab;
        this.multiplayer = multiplayer;
        this.allocations = allocations;
        this.tokens = tokens;
        this.configuration = configuration;
        this.capacity = capacity;
    }

    [Function("StartPartyMatchmaking")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "matchmaking/start")]
        HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request);
        if (error != null || !session.HasValue) return error!;

        CapacityStatus availability = await capacity.HeartbeatAsync(session.Value.PlayFabId, request.FunctionContext.CancellationToken);
        if (!availability.Ready)
            return await Reply(request, HttpStatusCode.ServiceUnavailable, new { error = "A arena está sendo preparada.", status = availability.Status, retryAfterSeconds = availability.RetryAfterSeconds });

        try
        {
            PartyRecord party = await RequirePartyAsync(session.Value.PlayFabId);
            await RememberMatchmakingEntityAsync(party, session.Value,
                request.FunctionContext.CancellationToken);
            if (!string.Equals(party.LeaderPlayerId, session.Value.PlayFabId, StringComparison.Ordinal))
                return await Reply(request, HttpStatusCode.Forbidden, new { error = "Somente o líder pode iniciar o pareamento." });

            ValidateParty(party);
            PartyMatchmakingTicket stored = await LoadTicketAsync(party.LeaderPlayerId);
            if (!string.IsNullOrWhiteSpace(stored.TicketId))
                return await StatusReply(request, session.Value.PlayFabId, party, stored);

            // A closed group already fills the arena. PlayFab cannot accept an
            // eight-player ticket for an eight-player queue, so allocate its MPS
            // session directly and keep the same signed-token delivery path.
            if (party.Members.Count == MaxMatchPlayers)
            {
                PartyMatchmakingTicket direct = await AllocateDirectPartyAsync(
                    party, request.FunctionContext.CancellationToken);
                await SaveTicketAsync(party.LeaderPlayerId, direct,
                    request.FunctionContext.CancellationToken);
                return await StatusReply(request, session.Value.PlayFabId,
                    party, direct);
            }

            PartyMatchmakingTicket ticket = await CreateTicketAsync(party, FullQueueName(), "full", SearchWindow, request.FunctionContext.CancellationToken);
            await SaveTicketAsync(party.LeaderPlayerId, ticket, request.FunctionContext.CancellationToken);
            return await Reply(request, HttpStatusCode.OK, Searching(ticket));
        }
        catch (ArgumentException exception)
        {
            return await Reply(request, HttpStatusCode.BadRequest, new { error = exception.Message });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return await Reply(request, HttpStatusCode.BadGateway, new { error = "Não foi possível iniciar o pareamento.", detail = exception.Message });
        }
    }

    [Function("GetPartyMatchmakingStatus")]
    public async Task<HttpResponseData> Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "matchmaking/status")]
        HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request);
        if (error != null || !session.HasValue) return error!;

        try
        {
            PartyRecord party = await RequirePartyAsync(session.Value.PlayFabId);
            await RememberMatchmakingEntityAsync(party, session.Value,
                request.FunctionContext.CancellationToken);
            PartyMatchmakingTicket ticket = await LoadTicketAsync(party.LeaderPlayerId);
            if (string.IsNullOrWhiteSpace(ticket.TicketId))
                return await Reply(request, HttpStatusCode.OK, new { state = "idle" });

            return await StatusReply(request, session.Value.PlayFabId, party, ticket);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return await Reply(request, HttpStatusCode.BadGateway, new { error = "Não foi possível consultar o pareamento.", detail = exception.Message });
        }
    }

    [Function("CancelPartyMatchmaking")]
    public async Task<HttpResponseData> Cancel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "matchmaking/cancel")]
        HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request);
        if (error != null || !session.HasValue) return error!;

        try
        {
            PartyRecord party = await RequirePartyAsync(session.Value.PlayFabId);
            if (!string.Equals(party.LeaderPlayerId, session.Value.PlayFabId, StringComparison.Ordinal))
                return await Reply(request, HttpStatusCode.Forbidden, new { error = "Somente o líder pode cancelar o pareamento." });

            PartyMatchmakingTicket ticket = await LoadTicketAsync(party.LeaderPlayerId);
            if (!string.IsNullOrWhiteSpace(ticket.TicketId))
            {
                if (ticket.Stage == "direct")
                {
                    return await Reply(request, HttpStatusCode.Conflict, new
                    {
                        error = "A partida do grupo completo já foi alocada e não pode mais ser cancelada."
                    });
                }

                try
                {
                    await multiplayer.CallAsync("Match", "CancelMatchmakingTicket", new { QueueName = ticket.QueueName, TicketId = ticket.TicketId }, request.FunctionContext.CancellationToken);
                }
                catch (InvalidOperationException exception) when (IsTerminalTicketError(exception))
                {
                    // An expired or already-matched ticket is already terminal.
                }
            }

            await ClearTicketAsync(party.LeaderPlayerId, request.FunctionContext.CancellationToken);
            // This only cancels this one party ticket. Every unrelated ticket remains queued.
            return await Reply(request, HttpStatusCode.OK, new { state = "cancelled" });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return await Reply(request, HttpStatusCode.BadGateway, new { error = "Não foi possível cancelar o pareamento.", detail = exception.Message });
        }
    }

    private async Task<HttpResponseData> StatusReply(HttpRequestData request, string playerId, PartyRecord party, PartyMatchmakingTicket ticket)
    {
        if (ticket.Stage == "direct")
        {
            MatchAllocationStore.Session? allocation = await allocations.GetSessionAsync(
                ticket.TicketId, request.FunctionContext.CancellationToken);
            if (allocation == null || allocation.ExpiresUtc <= DateTimeOffset.UtcNow)
            {
                await ClearTicketAsync(party.LeaderPlayerId,
                    request.FunctionContext.CancellationToken);
                return await Reply(request, HttpStatusCode.OK, new
                {
                    state = "idle",
                    error = "A alocação da partida completa expirou."
                });
            }

            return await ReplyDirectReadyAsync(request, playerId, allocation);
        }

        JsonElement ticketData = default;
        string status = string.Empty;
        string matchId = string.Empty;
        bool ticketMissing = false;
        try
        {
            ticketData = await multiplayer.CallAsync("Match", "GetMatchmakingTicket", new { QueueName = ticket.QueueName, TicketId = ticket.TicketId, EscapeObject = false }, request.FunctionContext.CancellationToken);
            status = ReadString(ticketData, "Status");
            matchId = ReadString(ticketData, "MatchId");
        }
        catch (InvalidOperationException exception) when (IsTerminalTicketError(exception))
        {
            // PlayFab removes an expired ticket instead of returning a terminal state.
            ticketMissing = true;
        }

        if (!string.IsNullOrWhiteSpace(matchId))
            return await ReplyReadyAsync(request, playerId, party, ticket, matchId);

        bool expired = DateTime.UtcNow >= ticket.ExpiresAtUtc;
        bool terminal = ticketMissing || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("Expired", StringComparison.OrdinalIgnoreCase);

        if ((expired || terminal) && ticket.Stage == "full")
        {
            // PlayFab queues require MinMatchSize >= 2. A solo ticket waits in
            // the full queue for 30 seconds, then receives a direct MPS session.
            // The game server fills the remaining slots with bots.
            await CancelTicketBestEffortAsync(ticket, request.FunctionContext.CancellationToken);
            if (party.Members.Count == 1)
            {
                PartyMatchmakingTicket solo = await AllocateDirectPartyAsync(
                    party, request.FunctionContext.CancellationToken);
                await SaveTicketAsync(party.LeaderPlayerId, solo,
                    request.FunctionContext.CancellationToken);
                return await StatusReply(request, playerId, party, solo);
            }

            // Parties with two through seven players can still be combined by
            // the fallback queue before the MPS server fills missing slots.
            PartyMatchmakingTicket fallback = await CreateTicketAsync(party, FallbackQueueName(), "fallback", SearchWindow, request.FunctionContext.CancellationToken);
            await SaveTicketAsync(party.LeaderPlayerId, fallback, request.FunctionContext.CancellationToken);
            return await Reply(request, HttpStatusCode.OK, Searching(fallback, "fallback"));
        }

        if (terminal)
        {
            await ClearTicketAsync(party.LeaderPlayerId, request.FunctionContext.CancellationToken);
            return await Reply(request, HttpStatusCode.OK, new { state = "idle", error = "O ticket de pareamento expirou." });
        }

        return await Reply(request, HttpStatusCode.OK, Searching(ticket));
    }

    private async Task<HttpResponseData> ReplyReadyAsync(HttpRequestData request, string playerId, PartyRecord party, PartyMatchmakingTicket ticket, string matchId)
    {
        JsonElement match = await multiplayer.CallAsync("Match", "GetMatch", new { QueueName = ticket.QueueName, MatchId = matchId, ReturnMemberAttributes = false }, request.FunctionContext.CancellationToken);
        JsonElement server = match.TryGetProperty("ServerDetails", out JsonElement details) ? details : default;
        string address = ReadString(server, "IPV4Address");
        int port = ReadGamePort(server);
        if (string.IsNullOrWhiteSpace(address) || port <= 0)
            return await Reply(request, HttpStatusCode.OK, new { state = "allocating", remainingSeconds = 0 });

        string region = ReadString(server, "Region");
        MatchAllocationStore.Session allocation = new(matchId, address, port, region, DateTimeOffset.UtcNow.Add(JoinLifetime));
        await allocations.SaveSessionAsync(allocation, request.FunctionContext.CancellationToken);
        foreach (PartyMemberRecord member in party.Members)
            await allocations.AddPlayerAsync(matchId, member.PlayerId, request.FunctionContext.CancellationToken);

        string token = tokens.Create(playerId, matchId, JoinLifetime);
        return await Reply(request, HttpStatusCode.OK, new
        {
            state = "ready",
            matchId,
            address,
            port,
            token,
            region,
            photonRegion = ToPhotonRegion(region)
        });
    }

    private async Task<PartyMatchmakingTicket> AllocateDirectPartyAsync(
        PartyRecord party, CancellationToken cancellationToken)
    {
        if (!tokens.IsConfigured)
            throw new InvalidOperationException("CAPIVARADA_MATCH_TOKEN_SECRET não foi configurado.");

        string buildId = configuration["CAPIVARADA_MPS_BUILD_ID"] ?? string.Empty;
        if (!Guid.TryParse(buildId, out _))
            throw new InvalidOperationException("CAPIVARADA_MPS_BUILD_ID não foi configurado com o Build ID publicado.");

        string matchId = Guid.NewGuid().ToString("D");
        JsonElement allocation = await multiplayer.CallAsync(
            "MultiplayerServer", "RequestMultiplayerServer", new
            {
                BuildId = buildId,
                SessionId = matchId,
                PreferredRegions = new[] { "EastUs" },
                InitialPlayers = party.Members.Select(member => member.PlayerId).ToArray(),
                SessionCookie = JsonSerializer.Serialize(new { matchId }, Json)
            }, cancellationToken);

        string address = ReadString(allocation, "IPV4Address");
        int port = ReadGamePort(allocation);
        if (string.IsNullOrWhiteSpace(address) || port <= 0)
            throw new InvalidOperationException("O PlayFab MPS não retornou endpoint UDP para o grupo completo.");

        string region = ReadString(allocation, "Region");
        MatchAllocationStore.Session session = new(matchId, address, port,
            region, DateTimeOffset.UtcNow.Add(JoinLifetime));
        await allocations.SaveSessionAsync(session, cancellationToken);
        foreach (PartyMemberRecord member in party.Members)
            await allocations.AddPlayerAsync(matchId, member.PlayerId,
                cancellationToken);

        return new PartyMatchmakingTicket
        {
            TicketId = matchId,
            Stage = "direct",
            ExpiresAtUtc = session.ExpiresUtc.UtcDateTime
        };
    }

    private async Task<HttpResponseData> ReplyDirectReadyAsync(
        HttpRequestData request, string playerId, MatchAllocationStore.Session allocation)
    {
        string token = tokens.Create(playerId, allocation.MatchId, JoinLifetime);
        return await Reply(request, HttpStatusCode.OK, new
        {
            state = "ready",
            matchId = allocation.MatchId,
            address = allocation.Address,
            port = allocation.Port,
            token,
            region = allocation.Region,
            photonRegion = ToPhotonRegion(allocation.Region)
        });
    }

    private async Task<PartyMatchmakingTicket> CreateTicketAsync(PartyRecord party, string queueName, string stage, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        JsonElement result = await multiplayer.CallAsync("Match", "CreateServerMatchmakingTicket", new
        {
            QueueName = queueName,
            GiveUpAfterSeconds = (int)lifetime.TotalSeconds,
            Members = party.Members.Select(member => new
            {
                Entity = new { Id = MatchmakingEntityId(member), Type = MatchmakingEntityType(member) },
                // The queue's EastUS region-selection rule reads this key.
                Attributes = new
                {
                    // Attribute keys are case-sensitive. JsonContent applies
                    // camelCase to anonymous-property names, so keep the queue
                    // path "Latencies" in a dictionary key instead.
                    DataObject = new Dictionary<string, object>
                    {
                        ["Latencies"] = new[]
                        {
                            new Dictionary<string, object>
                            {
                                ["region"] = "EastUs",
                                ["latency"] = 100
                            }
                        }
                    }
                }
            }).ToArray()
        }, cancellationToken);

        string ticketId = ReadString(result, "TicketId");
        if (string.IsNullOrWhiteSpace(ticketId))
            throw new InvalidOperationException("PlayFab não retornou o identificador do ticket.");

        return new PartyMatchmakingTicket
        {
            TicketId = ticketId,
            QueueName = queueName,
            Stage = stage,
            ExpiresAtUtc = DateTime.UtcNow.Add(lifetime)
        };
    }

    private void ValidateParty(PartyRecord party)
    {
        int count = party.Members.Count;
        if (count < MinPlayers) throw new ArgumentException("É preciso ao menos 1 jogador para iniciar o pareamento.");
        if (count > MaxMatchPlayers) throw new ArgumentException("O grupo não pode ter mais de 8 jogadores.");
        if (party.Members.Any(member => string.IsNullOrWhiteSpace(member.MatchmakingEntityId)))
            throw new ArgumentException("Um integrante ainda não atualizou a identidade PlayFab. Peça para todos abrirem novamente a tela de grupo.");
        if (party.Members.Any(member => member.PlayerId != party.LeaderPlayerId && !member.Ready))
            throw new ArgumentException("Todos os integrantes do grupo precisam estar prontos.");
    }

    private async Task<PartyRecord> RequirePartyAsync(string playerId)
    {
        PartyMembership membership = await playFab.GetInternalStateAsync(playerId, PartyStore.MembershipKey, () => new PartyMembership());
        if (string.IsNullOrWhiteSpace(membership.LeaderPlayerId)) throw new ArgumentException("Grupo não encontrado.");
        PartyRecord party = await playFab.GetInternalStateAsync(membership.LeaderPlayerId, PartyStore.PartyKey, () => new PartyRecord());
        if (string.IsNullOrWhiteSpace(party.PartyId) || !PartyStore.IsMember(party, playerId)) throw new ArgumentException("Você não pertence a este grupo.");
        return party;
    }

    private async Task RememberMatchmakingEntityAsync(PartyRecord party,
        PlayerSession session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.EntityId)) return;
        PartyMemberRecord? member = PartyStore.FindMember(party,
            session.PlayFabId);
        if (member == null || string.Equals(member.MatchmakingEntityId,
                session.EntityId, StringComparison.Ordinal)) return;
        member.MatchmakingEntityId = session.EntityId;
        member.MatchmakingEntityType = string.IsNullOrWhiteSpace(session.EntityType)
            ? "title_player_account" : session.EntityType;
        await playFab.SaveInternalStateAsync(party.LeaderPlayerId,
            PartyStore.PartyKey, party, cancellationToken);
    }

    private static string MatchmakingEntityId(PartyMemberRecord member) =>
        member.MatchmakingEntityId;
    private static string MatchmakingEntityType(PartyMemberRecord member) =>
        string.IsNullOrWhiteSpace(member.MatchmakingEntityType)
            ? "title_player_account" : member.MatchmakingEntityType;

    private static bool IsTerminalTicketError(InvalidOperationException exception) =>
        exception.Message.Contains("404", StringComparison.Ordinal) ||
        exception.Message.Contains("MatchmakingTicketAlreadyCompleted", StringComparison.Ordinal) ||
        exception.Message.Contains("TicketAlreadyCompleted", StringComparison.Ordinal) ||
        exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private Task<PartyMatchmakingTicket> LoadTicketAsync(string leaderId) => playFab.GetInternalStateAsync(leaderId, PartyStore.MatchmakingTicketKey, () => new PartyMatchmakingTicket());
    private Task SaveTicketAsync(string leaderId, PartyMatchmakingTicket ticket, CancellationToken cancellationToken) => playFab.SaveInternalStateAsync(leaderId, PartyStore.MatchmakingTicketKey, ticket, cancellationToken);
    private Task ClearTicketAsync(string leaderId, CancellationToken cancellationToken) => playFab.RemoveInternalStateAsync(leaderId, PartyStore.MatchmakingTicketKey, cancellationToken);

    private async Task CancelTicketBestEffortAsync(PartyMatchmakingTicket ticket, CancellationToken cancellationToken)
    {
        try
        {
            await multiplayer.CallAsync("Match", "CancelMatchmakingTicket", new { QueueName = ticket.QueueName, TicketId = ticket.TicketId }, cancellationToken);
        }
        catch (InvalidOperationException exception) when (IsTerminalTicketError(exception))
        {
            // Expired tickets are already gone.
        }
    }

    private string FullQueueName() => configuration["CAPIVARADA_MATCHMAKING_FULL_QUEUE"] ?? "CapivaradaArenaFull";
    private string FallbackQueueName() => configuration["CAPIVARADA_MATCHMAKING_FALLBACK_QUEUE"] ?? "CapivaradaArenaFallback";
    private static object Searching(PartyMatchmakingTicket ticket, string? state = null) => new { state = state ?? "searching", remainingSeconds = Math.Max(0, (int)Math.Ceiling((ticket.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds)) };
    private static string ReadString(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement item) ? item.GetString() ?? string.Empty : string.Empty;
    private static string? ToPhotonRegion(string region) => region.Equals("EastUs", StringComparison.OrdinalIgnoreCase) ? "us" : null;
    private static int ReadGamePort(JsonElement server)
    {
        if (server.ValueKind != JsonValueKind.Object || !server.TryGetProperty("Ports", out JsonElement ports) || ports.ValueKind != JsonValueKind.Array) return 0;
        foreach (JsonElement port in ports.EnumerateArray())
        {
            string name = ReadString(port, "Name");
            string protocol = ReadString(port, "Protocol");
            if ((name.Equals("game_port", StringComparison.OrdinalIgnoreCase) || protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase)) && port.TryGetProperty("Num", out JsonElement number) && number.TryGetInt32(out int value)) return value;
        }
        return 0;
    }
    private static async Task<HttpResponseData> Reply(HttpRequestData request, HttpStatusCode status, object body)
    {
        HttpResponseData response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, Json));
        return response;
    }
}
