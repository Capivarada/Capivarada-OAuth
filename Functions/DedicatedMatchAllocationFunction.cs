using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

/// <summary>
/// Allocates a PlayFab Multiplayer Servers session on behalf of an authenticated
/// player.  This is the only place that knows the title entity token/build id.
/// Unity receives only its direct Fusion endpoint and a signed join proof.
/// </summary>
public sealed class DedicatedMatchAllocationFunction : AuthenticatedFunction
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly PlayFabTitleEntityClient multiplayer;
    private readonly MatchAllocationStore allocations;
    private readonly MatchJoinTokenService tokens;
    private readonly Microsoft.Extensions.Configuration.IConfiguration configuration;

    public DedicatedMatchAllocationFunction(PlayerSessionService sessions, PlayFabTitleEntityClient multiplayer, MatchAllocationStore allocations, MatchJoinTokenService tokens, Microsoft.Extensions.Configuration.IConfiguration configuration) : base(sessions)
    {
        this.multiplayer = multiplayer;
        this.allocations = allocations;
        this.tokens = tokens;
        this.configuration = configuration;
    }

    [Function("AllocateDedicatedMatch")]
    public async Task<HttpResponseData> Allocate([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "matches/allocate")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request);
        if (error != null || !session.HasValue) return error!;
        if (!tokens.IsConfigured) return await Reply(request, HttpStatusCode.ServiceUnavailable, new { error = "CAPIVARADA_MATCH_TOKEN_SECRET não foi configurado." });

        string buildId = configuration["CAPIVARADA_MPS_BUILD_ID"] ?? string.Empty;
        if (!Guid.TryParse(buildId, out _)) return await Reply(request, HttpStatusCode.ServiceUnavailable, new { error = "CAPIVARADA_MPS_BUILD_ID não foi configurado com o Build ID publicado." });

        AllocateRequest? body = await JsonSerializer.DeserializeAsync<AllocateRequest>(request.Body, Json);
        string[] regions = NormalizeRegions(body?.PreferredRegions);
        string matchId = Guid.NewGuid().ToString("D");

        try
        {
            // InitialPlayers is the first enforcement layer in PlayFab MPS. The
            // Azure allow-list and signed token are checked again at settlement.
            JsonElement allocation = await multiplayer.CallAsync("MultiplayerServer", "RequestMultiplayerServer", new
            {
                BuildId = buildId,
                SessionId = matchId,
                PreferredRegions = regions,
                InitialPlayers = new[] { session.Value.PlayFabId },
                SessionCookie = JsonSerializer.Serialize(new { matchId }, Json)
            }, request.FunctionContext.CancellationToken);

            string address = allocation.TryGetProperty("IPV4Address", out JsonElement ip) ? ip.GetString() ?? string.Empty : string.Empty;
            int port = ReadGamePort(allocation);
            if (string.IsNullOrWhiteSpace(address) || port <= 0)
                throw new InvalidOperationException("O PlayFab MPS não retornou IPV4Address/porta UDP para a sessão.");

            await allocations.AddPlayerAsync(matchId, session.Value.PlayFabId, request.FunctionContext.CancellationToken);
            string token = tokens.Create(session.Value.PlayFabId, matchId, TimeSpan.FromMinutes(15));
            return await Reply(request, HttpStatusCode.OK, new { matchId, address, port, token, expiresInSeconds = 900, region = allocation.TryGetProperty("Region", out JsonElement region) ? region.GetString() : string.Empty });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return await Reply(request, HttpStatusCode.BadGateway, new { error = "Não foi possível alocar o servidor dedicado.", detail = exception.Message });
        }
    }

    private static string[] NormalizeRegions(string[]? requested)
    {
        HashSet<string> accepted = new(StringComparer.OrdinalIgnoreCase) { "BrazilSouth", "EastUs" };
        List<string> normalized = new();
        if (requested != null)
            foreach (string? value in requested)
                if (!string.IsNullOrWhiteSpace(value) && accepted.Contains(value.Trim()) && !normalized.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase)) normalized.Add(value.Trim());
        if (normalized.Count == 0) normalized.Add("BrazilSouth");
        if (!normalized.Contains("EastUs", StringComparer.OrdinalIgnoreCase)) normalized.Add("EastUs");
        return normalized.ToArray();
    }

    private static int ReadGamePort(JsonElement allocation)
    {
        if (!allocation.TryGetProperty("Ports", out JsonElement ports) || ports.ValueKind != JsonValueKind.Array) return 0;
        foreach (JsonElement port in ports.EnumerateArray())
        {
            string name = port.TryGetProperty("Name", out JsonElement label) ? label.GetString() ?? string.Empty : string.Empty;
            string protocol = port.TryGetProperty("Protocol", out JsonElement protocolValue) ? protocolValue.GetString() ?? string.Empty : string.Empty;
            if ((string.Equals(name, "game_port", StringComparison.OrdinalIgnoreCase) || string.Equals(protocol, "UDP", StringComparison.OrdinalIgnoreCase)) && port.TryGetProperty("Num", out JsonElement number) && number.TryGetInt32(out int value)) return value;
        }
        return 0;
    }

    private static async Task<HttpResponseData> Reply(HttpRequestData request, HttpStatusCode status, object value)
    {
        HttpResponseData response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(value, Json));
        return response;
    }

    private sealed class AllocateRequest { public string[]? PreferredRegions { get; set; } }
}
