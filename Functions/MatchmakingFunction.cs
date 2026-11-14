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
    private const int MinPlayers = 2;
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

    public MatchmakingFunction(
        PlayerSessionService sessions,
        PlayFabServerClient playFab,
        PlayFabTitleEntityClient multiplayer,
        MatchAllocationStore allocations,
        MatchJoinTokenService tokens,
        IConfiguration configuration) : base(sessions)
    {
        this.playFab = playFab;
        this.multiplayer = multiplayer;
        this.allocations = allocations;
        this.tokens = tokens;
        this.configuration = configuration;
    }

    [Function("StartPartyMatchmaking")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "matchmaking/start")]
        HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request);
        if (error != null || !session.HasValue) return error!;

        try
        {
            PartyRecord party = await RequirePartyAsync(session.Value.PlayFabId);
            if (!string.Equals(party.LeaderPlayerId, session.Value.PlayFabId, StringComparison.Ordinal))
                return await Reply(request, HttpStatusCode.Forbidden, new { error = "Somente o líder pode iniciar o pareamento." });

            ValidateParty(party);
            PartyMatchmakingTicket stored = await LoadTicketAsync(party.LeaderPlayerId);
            if (!string.IsNullOrWhiteSpace(stored.TicketId))
                return await StatusReply(request, session.Value.PlayFabId, party, stored);

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
                try
                {
                    await multiplayer.CallAsync("Match", "CancelMatchmakingTicket", new { QueueName = ticket.QueueName, TicketId = ticket.TicketId }, request.FunctionContext.CancellationToken);
                }
                catch (InvalidOperationException exception) when (exception.Message.Contains("404", StringComparison.Ordinal) || exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
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
        catch (InvalidOperationException exception) when (exception.Message.Contains("404", StringComparison.Ordinal))
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
            // The fallback queue is MinMatchSize=2 and is the only queue that may
            // complete after the 30-second full-party search. The game server then
            // fills remaining slots with bots.
            await CancelTicketBestEffortAsync(ticket, request.FunctionContext.CancellationToken);
            PartyMatchmakingTicket fallback = await CreateTicketAsync(party, FallbackQueueName(), "fallback", TimeSpan.FromMinutes(2), request.FunctionContext.CancellationToken);
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

    private async Task<PartyMatchmakingTicket> CreateTicketAsync(PartyRecord party, string queueName, string stage, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        JsonElement result = await multiplayer.CallAsync("Match", "CreateServerMatchmakingTicket", new
        {
            QueueName = queueName,
            GiveUpAfterSeconds = (int)lifetime.TotalSeconds,
            Members = party.Members.Select(member => new
            {
                Entity = new { Id = member.PlayerId, Type = "title_player_account" },
                // The queue's EastUS region-selection rule reads this key.
                Attributes = new
                {
                    DataObject = new
                    {
                        Latencies = new[] { new { region = "EastUs", latency = 100 } }
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
        if (count < MinPlayers) throw new ArgumentException("É preciso ao menos 2 jogadores para iniciar o pareamento.");
        if (count > MaxTicketPlayers) throw new ArgumentException("O PlayFab permite tickets de no máximo 7 jogadores em partidas de 8. Divida este grupo antes de procurar partida.");
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

    private Task<PartyMatchmakingTicket> LoadTicketAsync(string leaderId) => playFab.GetInternalStateAsync(leaderId, PartyStore.MatchmakingTicketKey, () => new PartyMatchmakingTicket());
    private Task SaveTicketAsync(string leaderId, PartyMatchmakingTicket ticket, CancellationToken cancellationToken) => playFab.SaveInternalStateAsync(leaderId, PartyStore.MatchmakingTicketKey, ticket, cancellationToken);
    private Task ClearTicketAsync(string leaderId, CancellationToken cancellationToken) => playFab.RemoveInternalStateAsync(leaderId, PartyStore.MatchmakingTicketKey, cancellationToken);

    private async Task CancelTicketBestEffortAsync(PartyMatchmakingTicket ticket, CancellationToken cancellationToken)
    {
        try
        {
            await multiplayer.CallAsync("Match", "CancelMatchmakingTicket", new { QueueName = ticket.QueueName, TicketId = ticket.TicketId }, cancellationToken);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("404", StringComparison.Ordinal))
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
