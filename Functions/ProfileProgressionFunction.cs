using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

public sealed class ProfileProgressionFunction : AuthenticatedFunction
{
    private readonly PlayFabEntityClient progression;
    private readonly CapivaradaOptions options;
    public ProfileProgressionFunction(PlayerSessionService sessions, PlayFabEntityClient progression, CapivaradaOptions options) : base(sessions) { this.progression = progression; this.options = options; }
    [Function("GetMyProgressionV2")]
    public async Task<HttpResponseData> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "profiles/me/progression")] HttpRequestData request)
    {
        var (_, error) = await RequirePlayerAsync(request); if (error != null) return error;
        try
        {
            JsonElement data = await progression.CallAsync(request, "Statistic", "GetStatistics", new { StatisticNames = new[] { options.PlayerPerformanceStatistic } });
            PlayerProgression playerProgression = new();
            if (data.TryGetProperty("Statistics", out JsonElement values)) foreach (JsonElement value in values.EnumerateArray())
            {
                if (value.GetProperty("Name").GetString() != options.PlayerPerformanceStatistic || !value.TryGetProperty("Scores", out JsonElement scores)) continue;
                playerProgression.Matches = Score(scores, 0);
                playerProgression.Wins = Score(scores, 1);
                playerProgression.Eliminations = Score(scores, 2);
                playerProgression.Level = Math.Max(1, Score(scores, 3));
                if (value.TryGetProperty("Metadata", out JsonElement metadata) && metadata.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(metadata.GetString()))
                {
                    try { playerProgression = JsonSerializer.Deserialize<PlayerProgression>(metadata.GetString()!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? playerProgression; }
                    catch (JsonException) { }
                }
            }
            playerProgression.Level = Math.Max(1, playerProgression.Level);
            playerProgression.ExperienceToNext = playerProgression.ExperienceToNext > 0 ? playerProgression.ExperienceToNext : 1500;
            HttpResponseData response = request.CreateResponse(HttpStatusCode.OK); await response.WriteStringAsync(JsonSerializer.Serialize(playerProgression, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })); return response;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); HttpResponseData response = request.CreateResponse(HttpStatusCode.InternalServerError); await response.WriteStringAsync("{\"error\":\"Falha ao carregar progressão.\"}"); return response; }
    }
    private static int Score(JsonElement scores, int index)
    {
        if (scores.ValueKind != JsonValueKind.Array || scores.GetArrayLength() <= index) return 0;
        JsonElement value = scores[index];
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : int.TryParse(value.GetString(), out number) ? number : 0;
    }

    private sealed class PlayerProgression
    {
        public int Matches { get; set; }
        public int Eliminations { get; set; }
        public int Wins { get; set; }
        public int Level { get; set; } = 1;
        public int Experience { get; set; }
        public int ExperienceToNext { get; set; } = 1500;
        public int TotalExperience { get; set; }
        public int PlayTimeSeconds { get; set; }
    }
}
