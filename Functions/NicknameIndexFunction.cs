using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

/// <summary>Registers the current authenticated profile name for nickname-based friend discovery.</summary>
public sealed class NicknameIndexFunction : AuthenticatedFunction
{
    private readonly NicknameIndexService nicknameIndex;
    public NicknameIndexFunction(PlayerSessionService sessions, NicknameIndexService nicknameIndex) : base(sessions) => this.nicknameIndex = nicknameIndex;

    [Function("RegisterNicknameIndex")]
    public async Task<HttpResponseData> Register([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "profiles/me/nickname-index")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request);
        if (error != null) return error;
        NicknameInput? input = await JsonSerializer.DeserializeAsync<NicknameInput>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (string.IsNullOrWhiteSpace(input?.Nickname)) return await Bad(request, "Apelido ausente.");
        try
        {
            bool registered = await nicknameIndex.RegisterAsync(session!.Value.PlayFabId, input.Nickname);
            if (!registered) return await Bad(request, "Este apelido já está associado a outra conta.");
            HttpResponseData response = request.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync("{\"registered\":true}");
            return response;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            HttpResponseData response = request.CreateResponse(HttpStatusCode.InternalServerError);
            await response.WriteStringAsync("{\"error\":\"Falha ao registrar apelido para busca social.\"}");
            return response;
        }
    }

    private static async Task<HttpResponseData> Bad(HttpRequestData request, string message)
    {
        HttpResponseData response = request.CreateResponse(HttpStatusCode.BadRequest);
        await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }));
        return response;
    }

    private sealed class NicknameInput { public string Nickname { get; set; } = string.Empty; }
}
