using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

/// <summary>Trusted lifecycle updates emitted by the dedicated MPS process.</summary>
public sealed class MatchLifecycleFunction
{
    private readonly IConfiguration configuration;
    private readonly MatchAllocationStore allocations;
    private readonly PlayerSessionService sessions;

    public MatchLifecycleFunction(IConfiguration configuration, MatchAllocationStore allocations, PlayerSessionService sessions)
    {
        this.configuration = configuration;
        this.allocations = allocations;
        this.sessions = sessions;
    }

    [Function("MarkDedicatedMatchStarted")]
    public async Task<HttpResponseData> Started(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "matches/{matchId}/started")] HttpRequestData request,
        string matchId)
    {
        if (!request.Headers.TryGetValues("X-Capivarada-Match-Secret", out IEnumerable<string>? supplied) ||
            string.IsNullOrWhiteSpace(configuration["CAPIVARADA_MATCH_SERVER_SECRET"]) ||
            supplied.FirstOrDefault() != configuration["CAPIVARADA_MATCH_SERVER_SECRET"])
            return request.CreateResponse(HttpStatusCode.Unauthorized);

        if (string.IsNullOrWhiteSpace(matchId) || matchId.Length > 128)
            return request.CreateResponse(HttpStatusCode.BadRequest);

        try
        {
            await allocations.MarkStartedAsync(matchId.Trim(), request.FunctionContext.CancellationToken);
            return request.CreateResponse(HttpStatusCode.NoContent);
        }
        catch (Azure.RequestFailedException exception) when (exception.Status == 404)
        {
            return request.CreateResponse(HttpStatusCode.NotFound);
        }
    }

    /// <summary>
    /// A client may receive an MPS allocation before its Fusion room becomes
    /// visible. If it never does, remove the backend reuse record so the next
    /// Play attempt requests a fresh allocation instead of returning a dead
    /// MatchId for fifteen minutes.
    /// </summary>
    [Function("AbandonDedicatedMatch")]
    public async Task<HttpResponseData> Abandon(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "matches/{matchId}/abandon")] HttpRequestData request,
        string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId) || matchId.Length > 128)
            return request.CreateResponse(HttpStatusCode.BadRequest);

        PlayerSession? session = await sessions.ValidateAsync(request);
        if (!session.HasValue)
            return request.CreateResponse(HttpStatusCode.Unauthorized);

        if (!await allocations.ContainsAsync(matchId.Trim(), session.Value.PlayFabId, request.FunctionContext.CancellationToken))
            return request.CreateResponse(HttpStatusCode.NotFound);

        await allocations.CloseSessionAsync(matchId.Trim(), request.FunctionContext.CancellationToken);
        return request.CreateResponse(HttpStatusCode.NoContent);
    }
}
