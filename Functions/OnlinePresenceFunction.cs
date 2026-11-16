using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public sealed class OnlinePresenceFunction : AuthenticatedFunction
{
    private readonly MpsCapacityService capacity;
    public OnlinePresenceFunction(PlayerSessionService sessions, MpsCapacityService capacity) : base(sessions) => this.capacity = capacity;

    [Function("PlayerOnlineHeartbeat")]
    public async Task<HttpResponseData> Heartbeat([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "presence/heartbeat")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request);
        if (error != null || !session.HasValue) return error!;
        return await Reply(request, await capacity.HeartbeatAsync(session.Value.PlayFabId, request.FunctionContext.CancellationToken));
    }

    [Function("MpsCapacityStatus")]
    public async Task<HttpResponseData> Status([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "presence/capacity")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request);
        if (error != null || !session.HasValue) return error!;
        return await Reply(request, await capacity.HeartbeatAsync(session.Value.PlayFabId, request.FunctionContext.CancellationToken));
    }

    [Function("MpsCapacityReconciler")]
    public async Task Reconcile([TimerTrigger("0 */1 * * * *")] TimerInfo timerInfo)
    {
        try { await capacity.StatusAsync(); }
        catch (Exception exception) { Console.Error.WriteLine("[MPS Capacity] " + exception); }
    }

    private static async Task<HttpResponseData> Reply(HttpRequestData request, CapacityStatus status)
    {
        HttpResponseData response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { ready = status.Ready, status = status.Status, retryAfterSeconds = status.RetryAfterSeconds, onlinePlayers = status.OnlinePlayers, standingByServers = status.StandingByServers, proppingServers = status.ProppingServers, activeServers = status.ActiveServers }));
        return response;
    }
}
