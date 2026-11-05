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
            JsonElement data = await progression.CallAsync(request, "Statistic", "GetStatistics", new { StatisticNames = new[] { options.MatchesStatistic, options.EliminationsStatistic, options.WinsStatistic, options.LevelStatistic } });
            int matches = 0, eliminations = 0, wins = 0, level = 1;
            if (data.TryGetProperty("Statistics", out JsonElement values)) foreach (JsonElement value in values.EnumerateArray())
            {
                string name = value.GetProperty("Name").GetString() ?? string.Empty;
                int score = value.TryGetProperty("Scores", out JsonElement scores) && scores.GetArrayLength() > 0 && int.TryParse(scores[0].GetString(), out int current) ? current : 0;
                if (name == options.MatchesStatistic) matches = score; else if (name == options.EliminationsStatistic) eliminations = score; else if (name == options.WinsStatistic) wins = score; else if (name == options.LevelStatistic) level = Math.Max(1, score);
            }
            HttpResponseData response = request.CreateResponse(HttpStatusCode.OK); await response.WriteStringAsync(JsonSerializer.Serialize(new { matches, eliminations, wins, level }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })); return response;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); HttpResponseData response = request.CreateResponse(HttpStatusCode.InternalServerError); await response.WriteStringAsync("{\"error\":\"Falha ao carregar progressão.\"}"); return response; }
    }
}
