using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

/// <summary>Authenticated clients obtain a scoped proof for the dedicated server.</summary>
public sealed class MatchJoinTokenFunction : AuthenticatedFunction
{
    private readonly MatchJoinTokenService tokens;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public MatchJoinTokenFunction(PlayerSessionService sessions, MatchJoinTokenService tokens) : base(sessions) => this.tokens = tokens;

    [Function("CreateMatchJoinToken")]
    public async Task<HttpResponseData> Create([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "matches/join-token")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request);
        if (error != null || !session.HasValue) return error!;
        if (!tokens.IsConfigured) return await Reply(request, HttpStatusCode.ServiceUnavailable, new { error = "Token de partida ainda não foi configurado." });

        MatchJoinRequest? body = await JsonSerializer.DeserializeAsync<MatchJoinRequest>(request.Body, Json);
        if (body == null || string.IsNullOrWhiteSpace(body.MatchId) || body.MatchId.Length > 128)
            return await Reply(request, HttpStatusCode.BadRequest, new { error = "Identificador de partida inválido." });

        string token = tokens.Create(session.Value.PlayFabId, body.MatchId.Trim(), TimeSpan.FromMinutes(10));
        return await Reply(request, HttpStatusCode.OK, new { token, expiresInSeconds = 600 });
    }

    private static async Task<HttpResponseData> Reply(HttpRequestData request, HttpStatusCode status, object body)
    {
        HttpResponseData response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, Json));
        return response;
    }

    private sealed class MatchJoinRequest { public string MatchId { get; set; } = string.Empty; }
}
