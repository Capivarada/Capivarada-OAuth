using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

/// <summary>Small server-only PlayFab REST client. Never expose its secret key to Unity.</summary>
public sealed class PlayFabServerClient
{
    private readonly IHttpClientFactory clients;
    private readonly IConfiguration configuration;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public PlayFabServerClient(IHttpClientFactory clients, IConfiguration configuration)
    {
        this.clients = clients;
        this.configuration = configuration;
    }

    public async Task<JsonElement> CallAsync(string operation, object payload, CancellationToken cancellationToken = default)
    {
        string titleId = Required("PLAYFAB_TITLE_ID");
        string secretKey = Required("PLAYFAB_SECRET_KEY");
        using HttpRequestMessage request = new(HttpMethod.Post, $"https://{titleId}.playfabapi.com/Server/{operation}");
        request.Headers.Add("X-SecretKey", secretKey);
        request.Content = JsonContent.Create(payload);
        using HttpResponseMessage response = await clients.CreateClient().SendAsync(request, cancellationToken);
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"PlayFab Server/{operation} ({(int)response.StatusCode}): {content}");
        using JsonDocument document = JsonDocument.Parse(content);
        return document.RootElement.TryGetProperty("data", out JsonElement data) ? data.Clone() : default;
    }

    public async Task<FriendRequestState> GetFriendStateAsync(string playerId, CancellationToken cancellationToken = default)
    {
        JsonElement data = await CallAsync("GetUserInternalData", new { PlayFabId = playerId, Keys = new[] { FriendRequestStore.DataKey } }, cancellationToken);
        if (!data.TryGetProperty("Data", out JsonElement values) || !values.TryGetProperty(FriendRequestStore.DataKey, out JsonElement value)) return new FriendRequestState();
        if (!value.TryGetProperty("Value", out JsonElement raw) || string.IsNullOrWhiteSpace(raw.GetString())) return new FriendRequestState();
        try { return JsonSerializer.Deserialize<FriendRequestState>(raw.GetString()!, Json) ?? new FriendRequestState(); }
        catch (JsonException) { return new FriendRequestState(); }
    }

    public Task SaveFriendStateAsync(string playerId, FriendRequestState state, CancellationToken cancellationToken = default) =>
        CallAsync("UpdateUserInternalData", new { PlayFabId = playerId, Data = new Dictionary<string, string> { [FriendRequestStore.DataKey] = JsonSerializer.Serialize(state, Json) } }, cancellationToken);

    public Task AddFriendAsync(string playerId, string friendId, CancellationToken cancellationToken = default) =>
        CallAsync("AddFriend", new { PlayFabId = playerId, FriendPlayFabId = friendId }, cancellationToken);
    public Task RemoveFriendAsync(string playerId, string friendId, CancellationToken cancellationToken = default) =>
        CallAsync("RemoveFriend", new { PlayFabId = playerId, FriendPlayFabId = friendId }, cancellationToken);

    public async Task<string?> ResolvePlayerIdAsync(string nicknameOrId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nicknameOrId)) return null;
        // PlayFab's Server API has no endpoint that resolves an arbitrary title display name.
        // Treat this endpoint as an ID lookup until the title's nickname-index service is introduced.
        if (nicknameOrId.Length < 12 || !nicknameOrId.All(char.IsLetterOrDigit)) return null;
        try
        {
            JsonElement account = await CallAsync("GetUserAccountInfo", new { PlayFabId = nicknameOrId }, cancellationToken);
            return account.TryGetProperty("UserInfo", out JsonElement info) && info.TryGetProperty("PlayFabId", out JsonElement id)
                ? id.GetString()
                : null;
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("PlayFab Server/GetUserAccountInfo (400)", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>
    /// A PlayFabId is the hexadecimal identifier exposed by the game's profile UI.
    /// Entity IDs and MasterPlayerIds are different identifiers and cannot be used by
    /// the Server GetUserAccountInfo endpoint.
    /// </summary>
    public static bool LooksLikePlayFabId(string value) =>
        value.Length >= 12 && value.All(character =>
            (character >= '0' && character <= '9') ||
            (character >= 'A' && character <= 'F') ||
            (character >= 'a' && character <= 'f'));

    public async Task<FriendSummary> GetSummaryAsync(string playerId, CancellationToken cancellationToken = default)
    {
        JsonElement account = await CallAsync("GetUserAccountInfo", new { PlayFabId = playerId }, cancellationToken);
        string nickname = account.TryGetProperty("UserInfo", out JsonElement info) && info.TryGetProperty("TitleInfo", out JsonElement title) && title.TryGetProperty("DisplayName", out JsonElement name)
            ? name.GetString() ?? "Capivara" : "Capivara";
        return new FriendSummary { PlayerId = playerId, Nickname = nickname };
    }

    private string Required(string key) => configuration[key] ?? throw new InvalidOperationException($"A configuração {key} não foi definida na Function App.");
}
