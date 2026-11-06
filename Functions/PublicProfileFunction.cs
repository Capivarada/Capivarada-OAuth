using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

/// <summary>
/// Allow-listed public profile projection for OtherProfile and public
/// leaderboard interactions. Private account data is never returned here.
/// </summary>
public sealed class PublicProfileFunction
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly PlayFabServerClient playFab;

    public PublicProfileFunction(PlayFabServerClient playFab) => this.playFab = playFab;

    [Function("GetPublicProfile")]
    public async Task<HttpResponseData> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "profiles/{playerId}")] HttpRequestData request, string playerId)
    {
        if (!PlayFabServerClient.LooksLikePlayFabId(playerId))
            return await Reply(request, HttpStatusCode.BadRequest, new { error = "ID de jogador inválido." });

        try
        {
            JsonElement data = await playFab.CallAsync("GetUserData", new { PlayFabId = playerId, Keys = new[] { "profile" } });
            string? profileJson = null;
            if (data.TryGetProperty("Data", out JsonElement records) && records.TryGetProperty("profile", out JsonElement profileRecord) && profileRecord.TryGetProperty("Value", out JsonElement value))
                profileJson = value.GetString();

            PublicProfile source = string.IsNullOrWhiteSpace(profileJson)
                ? new PublicProfile { PlayerId = playerId }
                : JsonSerializer.Deserialize<PublicProfile>(profileJson, Json) ?? new PublicProfile { PlayerId = playerId };
            source.PlayerId = playerId;
            source.Nickname = string.IsNullOrWhiteSpace(source.Nickname) ? "Capivara" : source.Nickname;
            source.AvatarId = string.IsNullOrWhiteSpace(source.AvatarId) ? "avatar1" : source.AvatarId;
            source.Level = Math.Max(1, source.Level);
            source.ExperienceToNext = source.ExperienceToNext > 0 ? source.ExperienceToNext : 1500;
            return await Reply(request, HttpStatusCode.OK, source);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return await Reply(request, HttpStatusCode.NotFound, new { error = "Perfil público não encontrado." });
        }
    }

    private static async Task<HttpResponseData> Reply(HttpRequestData request, HttpStatusCode code, object body)
    {
        HttpResponseData response = request.CreateResponse(code);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, Json));
        return response;
    }

    private sealed class PublicProfile
    {
        public string PlayerId { get; set; } = string.Empty;
        public string Nickname { get; set; } = "Capivara";
        public string AvatarId { get; set; } = "avatar1";
        public string EquippedSkinId { get; set; } = string.Empty;
        public int Level { get; set; } = 1;
        public int Experience { get; set; }
        public int ExperienceToNext { get; set; } = 1500;
        public int Matches { get; set; }
        public int Eliminations { get; set; }
        public int Wins { get; set; }
        public float PlayTimeSeconds { get; set; }
    }
}
