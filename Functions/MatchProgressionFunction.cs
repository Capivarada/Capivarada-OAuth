using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

/// <summary>Trusted match-server endpoint. Deliberately rejects direct Unity calls unless the server secret is present.</summary>
public sealed class MatchProgressionFunction
{
    private readonly PlayFabTitleEntityClient playFab;
    private readonly CapivaradaOptions options;
    private readonly IConfiguration configuration;
    public MatchProgressionFunction(PlayFabTitleEntityClient playFab, CapivaradaOptions options, IConfiguration configuration) { this.playFab = playFab; this.options = options; this.configuration = configuration; }

    [Function("SettleMatchProgression")]
    public async Task<HttpResponseData> Settle([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "matches/settle")] HttpRequestData request)
    {
        if (!request.Headers.TryGetValues("X-Capivarada-Match-Secret", out IEnumerable<string>? supplied) || supplied.FirstOrDefault() != configuration["CAPIVARADA_MATCH_SERVER_SECRET"])
            return await Reply(request, HttpStatusCode.Unauthorized, new { error = "Apenas o servidor de partida pode registrar resultados." });
        MatchSettlement? result = await JsonSerializer.DeserializeAsync<MatchSettlement>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result == null || string.IsNullOrWhiteSpace(result.MatchId) || string.IsNullOrWhiteSpace(result.EntityId) || string.IsNullOrWhiteSpace(result.EntityType))
            return await Reply(request, HttpStatusCode.BadRequest, new { error = "MatchId e EntityKey são obrigatórios." });
        try
        {
            PlayerTotals previous = await ReadTotals(result.EntityId, result.EntityType);
            PlayerTotals totals = new() { Matches = previous.Matches + 1, Eliminations = previous.Eliminations + Math.Max(0, result.Eliminations), Wins = previous.Wins + (result.Won ? 1 : 0), Level = Math.Max(1, result.Level), Nickname = result.Nickname, AvatarId = string.IsNullOrWhiteSpace(result.AvatarId) ? "avatar1" : result.AvatarId };
            string metadata = JsonSerializer.Serialize(totals, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await playFab.CallAsync("Statistic", "UpdateStatistics", new
            {
                Entity = new { Id = result.EntityId, Type = result.EntityType }, TransactionId = result.MatchId,
                Statistics = new object[]
                {
                    new { Name = options.MatchesStatistic, Scores = new[] { "1" }, Metadata = metadata },
                    new { Name = options.EliminationsStatistic, Scores = new[] { Math.Max(0, result.Eliminations).ToString() }, Metadata = metadata },
                    new { Name = options.WinsStatistic, Scores = new[] { result.Won ? "1" : "0" }, Metadata = metadata },
                    new { Name = options.LevelStatistic, Scores = new[] { totals.Level.ToString() }, Metadata = metadata }
                }
            });
            return await Reply(request, HttpStatusCode.OK, totals);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return await Reply(request, HttpStatusCode.InternalServerError, new { error = "Falha ao registrar a partida." }); }
    }

    private async Task<PlayerTotals> ReadTotals(string entityId, string entityType)
    {
        JsonElement response = await playFab.CallAsync("Statistic", "GetStatistics", new { Entity = new { Id = entityId, Type = entityType }, StatisticNames = new[] { options.MatchesStatistic, options.EliminationsStatistic, options.WinsStatistic, options.LevelStatistic } });
        PlayerTotals totals = new();
        if (!response.TryGetProperty("Statistics", out JsonElement values)) return totals;
        foreach (JsonElement statistic in values.EnumerateArray())
        {
            string name = statistic.GetProperty("Name").GetString() ?? string.Empty;
            int score = statistic.TryGetProperty("Scores", out JsonElement scores) && scores.GetArrayLength() > 0 && int.TryParse(scores[0].GetString(), out int number) ? number : 0;
            if (name == options.MatchesStatistic) totals.Matches = score;
            else if (name == options.EliminationsStatistic) totals.Eliminations = score;
            else if (name == options.WinsStatistic) totals.Wins = score;
            else if (name == options.LevelStatistic) totals.Level = Math.Max(1, score);
        }
        return totals;
    }
    private static async Task<HttpResponseData> Reply(HttpRequestData request, HttpStatusCode status, object body) { HttpResponseData response = request.CreateResponse(status); response.Headers.Add("Content-Type", "application/json; charset=utf-8"); await response.WriteStringAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })); return response; }
    private sealed class MatchSettlement { public string MatchId { get; set; } = string.Empty; public string EntityId { get; set; } = string.Empty; public string EntityType { get; set; } = "title_player_account"; public string Nickname { get; set; } = "Capivara"; public string AvatarId { get; set; } = "avatar1"; public int Eliminations { get; set; } public bool Won { get; set; } public int Level { get; set; } = 1; }
    private sealed class PlayerTotals { public string Nickname { get; set; } = "Capivara"; public string AvatarId { get; set; } = "avatar1"; public int Matches { get; set; } public int Eliminations { get; set; } public int Wins { get; set; } public int Level { get; set; } = 1; }
}
