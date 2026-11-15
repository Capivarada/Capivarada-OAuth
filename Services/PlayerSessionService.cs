using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

public sealed class PlayerSessionService
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IConfiguration configuration;

    public PlayerSessionService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        this.httpClientFactory = httpClientFactory;
        this.configuration = configuration;
    }

    public async Task<PlayerSession?> ValidateAsync(HttpRequestData request)
    {
        if (!request.Headers.TryGetValues("X-PlayFab-SessionTicket", out IEnumerable<string>? values))
        {
            return null;
        }

        string? ticket = values.FirstOrDefault();
        string? titleId = configuration["PLAYFAB_TITLE_ID"];
        string? secretKey = configuration["PLAYFAB_SECRET_KEY"];
        if (string.IsNullOrWhiteSpace(ticket) || string.IsNullOrWhiteSpace(titleId) || string.IsNullOrWhiteSpace(secretKey))
        {
            return null;
        }

        using HttpRequestMessage message = new(HttpMethod.Post, $"https://{titleId}.playfabapi.com/Server/AuthenticateSessionTicket");
        message.Headers.Add("X-SecretKey", secretKey);
        message.Content = JsonContent.Create(new { SessionTicket = ticket });

        using HttpResponseMessage response = await httpClientFactory.CreateClient().SendAsync(message);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("data", out JsonElement data) ||
            !data.TryGetProperty("UserInfo", out JsonElement userInfo) ||
            !userInfo.TryGetProperty("PlayFabId", out JsonElement id))
        {
            return null;
        }

        string? playFabId = id.GetString();
        if (string.IsNullOrWhiteSpace(playFabId))
        {
            return null;
        }

        string entityId = request.Headers.TryGetValues("X-PlayFab-EntityId", out IEnumerable<string>? entityIds)
            ? entityIds.FirstOrDefault() ?? string.Empty
            : string.Empty;
        string entityType = request.Headers.TryGetValues("X-PlayFab-EntityType", out IEnumerable<string>? entityTypes)
            ? entityTypes.FirstOrDefault() ?? "title_player_account"
            : "title_player_account";
        return new PlayerSession(playFabId, entityId, entityType);
    }
}

public readonly record struct PlayerSession(
    string PlayFabId,
    string EntityId = "",
    string EntityType = "title_player_account");
