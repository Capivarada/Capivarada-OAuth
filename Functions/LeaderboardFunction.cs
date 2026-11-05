using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

/// <summary>V2 leaderboard projection. All four visible player values are read from entry metadata written during match settlement.</summary>
public sealed class LeaderboardFunction : AuthenticatedFunction
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly PlayFabEntityClient leaderboards;
    private readonly CapivaradaOptions options;
    public LeaderboardFunction(PlayerSessionService sessions, PlayFabEntityClient leaderboards, CapivaradaOptions options) : base(sessions) { this.leaderboards = leaderboards; this.options = options; }

    [Function("GetLeaderboardV2")]
    public async Task<HttpResponseData> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "leaderboards/{name}")] HttpRequestData request, string name)
    {
        var (_, error) = await RequirePlayerAsync(request); if (error != null) return error;
        if (!Allowed(name)) return await Bad(request, "Leaderboard não autorizado.");
        int count = QueryCount(request.Url);
        try
        {
            JsonElement data = await leaderboards.CallAsync(request, "Leaderboard", "GetLeaderboard", new { LeaderboardName = name, PageSize = count });
            return await Ok(request, new { entries = Map(data, name) });
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return await Fail(request, "Não foi possível carregar o ranking."); }
    }

    [Function("GetMyLeaderboardV2")]
    public async Task<HttpResponseData> Me([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "leaderboards/{name}/me")] HttpRequestData request, string name)
    {
        var (_, error) = await RequirePlayerAsync(request); if (error != null) return error;
        if (!Allowed(name)) return await Bad(request, "Leaderboard não autorizado.");
        try
        {
            JsonElement data = await leaderboards.CallAsync(request, "Leaderboard", "GetLeaderboardAroundEntity", new { LeaderboardName = name, MaxSurroundingEntries = 0 });
            LeaderboardEntry[] entries = Map(data, name);
            return await Ok(request, new { entry = entries.FirstOrDefault() });
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return await Fail(request, "Não foi possível carregar sua posição no ranking."); }
    }

    private bool Allowed(string name) => name == options.MatchesLeaderboard || name == options.EliminationsLeaderboard || name == options.WinsLeaderboard || name == options.LevelLeaderboard;
    private static int QueryCount(Uri url)
    {
        foreach (string pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length == 2 && string.Equals(parts[0], "count", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[1], out int value)) return Math.Clamp(value, 1, 100);
        }
        return 10;
    }
    private static LeaderboardEntry[] Map(JsonElement data, string leaderboardName)
    {
        if (!data.TryGetProperty("Rankings", out JsonElement rankings)) return Array.Empty<LeaderboardEntry>();
        List<LeaderboardEntry> result = new();
        foreach (JsonElement source in rankings.EnumerateArray())
        {
            LeaderboardMetadata metadata = ReadMetadata(source);
            long score = source.TryGetProperty("Scores", out JsonElement scores) && scores.GetArrayLength() > 0 && long.TryParse(scores[0].GetString(), out long parsed) ? parsed : 0;
            if (leaderboardName == "RankingMatches") metadata.Matches = (int)score;
            else if (leaderboardName == "RankingEliminations") metadata.Eliminations = (int)score;
            else if (leaderboardName == "RankingWins") metadata.Wins = (int)score;
            else if (leaderboardName == "RankingLevel") metadata.Level = (int)score;
            string id = source.TryGetProperty("Entity", out JsonElement entity) && entity.TryGetProperty("Id", out JsonElement entityId) ? entityId.GetString() ?? string.Empty : string.Empty;
            string displayName = source.TryGetProperty("DisplayName", out JsonElement name) ? name.GetString() ?? metadata.Nickname : metadata.Nickname;
            result.Add(new LeaderboardEntry { PlayerId = id, Nickname = displayName, AvatarId = metadata.AvatarId, Position = source.TryGetProperty("Rank", out JsonElement rank) ? rank.GetInt32() + 1 : 0, Matches = metadata.Matches, Eliminations = metadata.Eliminations, Wins = metadata.Wins, Level = metadata.Level });
        }
        return result.ToArray();
    }
    private static LeaderboardMetadata ReadMetadata(JsonElement source)
    {
        if (!source.TryGetProperty("Metadata", out JsonElement raw) || raw.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(raw.GetString())) return new LeaderboardMetadata();
        try { return JsonSerializer.Deserialize<LeaderboardMetadata>(raw.GetString()!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new LeaderboardMetadata(); }
        catch (JsonException) { return new LeaderboardMetadata(); }
    }
    private static async Task<HttpResponseData> Ok(HttpRequestData request, object value) { HttpResponseData response = request.CreateResponse(HttpStatusCode.OK); response.Headers.Add("Content-Type", "application/json; charset=utf-8"); await response.WriteStringAsync(JsonSerializer.Serialize(value, Json)); return response; }
    private static async Task<HttpResponseData> Bad(HttpRequestData request, string message) { HttpResponseData response = request.CreateResponse(HttpStatusCode.BadRequest); await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, Json)); return response; }
    private static async Task<HttpResponseData> Fail(HttpRequestData request, string message) { HttpResponseData response = request.CreateResponse(HttpStatusCode.InternalServerError); await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, Json)); return response; }
    private sealed class LeaderboardMetadata { public string Nickname { get; set; } = "Capivara"; public string AvatarId { get; set; } = "avatar1"; public int Matches { get; set; } public int Eliminations { get; set; } public int Wins { get; set; } public int Level { get; set; } = 1; }
    private sealed class LeaderboardEntry { public string PlayerId { get; set; } = string.Empty; public string Nickname { get; set; } = string.Empty; public string AvatarId { get; set; } = "avatar1"; public int Position { get; set; } public int Matches { get; set; } public int Eliminations { get; set; } public int Wins { get; set; } public int Level { get; set; } }
}
