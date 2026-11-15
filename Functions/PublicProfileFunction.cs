using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

/// <summary>Public projection assembled from separated Player Data records.</summary>
public sealed class PublicProfileFunction
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly PlayFabServerClient playFab;
    public PublicProfileFunction(PlayFabServerClient playFab) => this.playFab = playFab;

    [Function("GetPublicProfile")]
    public async Task<HttpResponseData> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "profiles/{playerId}")] HttpRequestData request, string playerId)
    {
        if (!PlayFabServerClient.LooksLikePlayFabId(playerId)) return await Reply(request, HttpStatusCode.BadRequest, new { error = "ID de jogador inválido." });
        try
        {
            JsonElement data = await playFab.CallAsync("GetUserData", new { PlayFabId = playerId, Keys = new[] { "profile", "cosmetics", "progression", "statistics" } });
            PublicProfile profile = Read(data, playerId);
            profile.PlayerId = playerId;
            profile.Nickname = string.IsNullOrWhiteSpace(profile.Nickname) ? "Capivara" : profile.Nickname;
            profile.AvatarId = string.IsNullOrWhiteSpace(profile.AvatarId) ? "avatar1" : profile.AvatarId;
            profile.Progression.Level = Math.Max(1, profile.Progression.Level);
            return await Reply(request, HttpStatusCode.OK, profile);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return await Reply(request, HttpStatusCode.NotFound, new { error = "Perfil público não encontrado." });
        }
    }

    private static PublicProfile Read(JsonElement data, string playerId)
    {
        PublicProfile profile = new() { PlayerId = playerId };
        if (!data.TryGetProperty("Data", out JsonElement records)) return profile;
        if (TryRead(records, "profile", out Identity identity))
        {
            profile.Nickname = identity.Nickname; profile.Email = identity.Email; profile.AvatarId = identity.AvatarId;
            profile.CreatedAtUtc = identity.CreatedAtUtc;
        }
        if (TryRead(records, "cosmetics", out JsonElement cosmetics)) profile.Cosmetics = cosmetics;
        if (TryRead(records, "progression", out Progression progression)) profile.Progression = progression;
        if (TryRead(records, "statistics", out Statistics statistics)) profile.Statistics = statistics;

        // Read-only migration from the original monolithic profile.
        if (!records.TryGetProperty("cosmetics", out _) || !records.TryGetProperty("progression", out _) || !records.TryGetProperty("statistics", out _))
        {
            if (TryRead(records, "profile", out LegacyProfile legacy))
            {
                if (string.IsNullOrWhiteSpace(profile.Nickname)) { profile.Nickname = legacy.Nickname; profile.Email = legacy.Email; profile.AvatarId = legacy.AvatarId; profile.CreatedAtUtc = legacy.CreatedAtUtc; }
                if (!records.TryGetProperty("cosmetics", out _)) profile.Cosmetics = legacy.Cosmetics;
                if (!records.TryGetProperty("progression", out _)) profile.Progression = new Progression { Level = Math.Max(1, legacy.Level), Experience = Math.Max(0, legacy.Experience % 1500) };
                if (!records.TryGetProperty("statistics", out _)) profile.Statistics = new Statistics { Matches = legacy.Matches, Wins = legacy.Wins, Eliminations = legacy.Eliminations };
            }
        }
        return profile;
    }

    private static bool TryRead<T>(JsonElement records, string key, out T value)
    {
        value = default!;
        if (!records.TryGetProperty(key, out JsonElement record) || !record.TryGetProperty("Value", out JsonElement raw) || string.IsNullOrWhiteSpace(raw.GetString())) return false;
        try { value = JsonSerializer.Deserialize<T>(raw.GetString()!, Json)!; return value != null; }
        catch (JsonException) { return false; }
    }
    private static async Task<HttpResponseData> Reply(HttpRequestData request, HttpStatusCode code, object body) { HttpResponseData response = request.CreateResponse(code); response.Headers.Add("Content-Type", "application/json; charset=utf-8"); await response.WriteStringAsync(JsonSerializer.Serialize(body, Json)); return response; }

    private class Identity { public string Nickname { get; set; } = "Capivara"; public string Email { get; set; } = string.Empty; public string AvatarId { get; set; } = "avatar1"; public string CreatedAtUtc { get; set; } = string.Empty; }
    private sealed class Progression { public int Level { get; set; } = 1; public int Experience { get; set; } }
    private sealed class Statistics { public int Matches { get; set; } public int Wins { get; set; } public int Eliminations { get; set; } }
    private sealed class LegacyProfile : Identity { public JsonElement Cosmetics { get; set; } public int Level { get; set; } = 1; public int Experience { get; set; } public int Matches { get; set; } public int Wins { get; set; } public int Eliminations { get; set; } }
    private sealed class PublicProfile
    {
        public string PlayerId { get; set; } = string.Empty; public string Nickname { get; set; } = "Capivara"; public string Email { get; set; } = string.Empty;
        public string AvatarId { get; set; } = "avatar1"; public string CreatedAtUtc { get; set; } = string.Empty; public JsonElement Cosmetics { get; set; }
        public Progression Progression { get; set; } = new(); public Statistics Statistics { get; set; } = new();
    }
}
