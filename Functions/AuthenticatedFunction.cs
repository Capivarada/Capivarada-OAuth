using System.Net;
using Microsoft.Azure.Functions.Worker.Http;

public abstract class AuthenticatedFunction
{
    private readonly PlayerSessionService sessions;

    protected AuthenticatedFunction(PlayerSessionService sessions)
    {
        this.sessions = sessions;
    }

    protected async Task<(PlayerSession? Session, HttpResponseData? Error)> RequirePlayerAsync(HttpRequestData request)
    {
        PlayerSession? session = await sessions.ValidateAsync(request);
        if (session.HasValue)
        {
            return (session, null);
        }

        HttpResponseData response = request.CreateResponse(HttpStatusCode.Unauthorized);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync("{\"error\":\"Sessão PlayFab inválida ou expirada.\"}");
        return (null, response);
    }
}
