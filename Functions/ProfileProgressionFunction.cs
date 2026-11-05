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
            int matches = 0, eliminations = 0, wins = 0, level = 1;
            if (data.TryGetProperty("Statistics", out JsonElement values)) foreach (JsonElement value in values.EnumerateArray())
            {
                if (value.GetProperty("Name").GetString() != options.PlayerPerformanceStatistic || !value.TryGetProperty("Scores", out JsonElement scores)) continue;
                matches = Score(scores, 0);
                wins = Score(scores, 1);
                eliminations = Score(scores, 2);
                level = Math.Max(1, Score(scores, 3));
            }
            HttpResponseData response = request.CreateResponse(HttpStatusCode.OK); await response.WriteStringAsync(JsonSerializer.Serialize(new { matches, eliminations, wins, level }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })); return response;
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
}
