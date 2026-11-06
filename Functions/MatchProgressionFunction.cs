using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Trusted server-only settlement. The Fusion dedicated match server submits all players;
/// browser and Unity clients never receive the secret required by this endpoint.
/// </summary>
public sealed class MatchProgressionFunction
{
    private readonly PlayFabTitleEntityClient playFab;
    private readonly CapivaradaOptions options;
    private readonly IConfiguration configuration;
    private readonly MatchSettlementReceiptService receipts;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public MatchProgressionFunction(PlayFabTitleEntityClient playFab, CapivaradaOptions options, IConfiguration configuration, MatchSettlementReceiptService receipts)
    { this.playFab = playFab; this.options = options; this.configuration = configuration; this.receipts = receipts; }

    [Function("SettleMatchProgression")]
    public async Task<HttpResponseData> Settle([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "matches/settle")] HttpRequestData request)
    {
        if (!request.Headers.TryGetValues("X-Capivarada-Match-Secret", out IEnumerable<string>? supplied) ||
            string.IsNullOrWhiteSpace(configuration["CAPIVARADA_MATCH_SERVER_SECRET"]) || supplied.FirstOrDefault() != configuration["CAPIVARADA_MATCH_SERVER_SECRET"])
            return await Reply(request, HttpStatusCode.Unauthorized, new { error = "Apenas o servidor de partida pode registrar resultados." });

        MatchSettlementBatch? batch = await JsonSerializer.DeserializeAsync<MatchSettlementBatch>(request.Body, JsonOptions);
        if (batch == null || string.IsNullOrWhiteSpace(batch.MatchId) || batch.Participants == null || batch.Participants.Count is < 1 or > 8)
            return await Reply(request, HttpStatusCode.BadRequest, new { error = "MatchId e entre um e oito participantes são obrigatórios." });
        if (batch.Participants.Select(x => x.EntityId).Distinct(StringComparer.Ordinal).Count() != batch.Participants.Count)
            return await Reply(request, HttpStatusCode.BadRequest, new { error = "A mesma entidade foi enviada mais de uma vez." });

        List<object> outcomes = new();
        foreach (MatchParticipant player in batch.Participants)
        {
            if (!Eligible(player)) { outcomes.Add(new { player.EntityId, settled = false, reason = player.IsBot ? "bot" : "notEligible" }); continue; }
            if (!await receipts.TryStartAsync(batch.MatchId, player.EntityId, request.FunctionContext.CancellationToken)) { outcomes.Add(new { player.EntityId, settled = false, reason = "alreadySettled" }); continue; }
            try
            {
                PlayerTotals previous = await ReadTotals(player.EntityId, player.EntityType);
                PlayerTotals totals = new()
                {
                    Matches = previous.Matches + 1,
                    Eliminations = previous.Eliminations + Math.Max(0, player.Eliminations),
                    Wins = previous.Wins + (player.Placement == 1 ? 1 : 0),
                    Level = Math.Max(previous.Level, Math.Max(1, player.Level)),
                    Nickname = string.IsNullOrWhiteSpace(player.Nickname) ? previous.Nickname : player.Nickname.Trim(),
                    AvatarId = string.IsNullOrWhiteSpace(player.AvatarId) ? previous.AvatarId : player.AvatarId.Trim()
                };
                await WriteTotals(batch.MatchId + ":" + player.EntityId, player.EntityId, player.EntityType, totals, request.FunctionContext.CancellationToken);
                await receipts.CompleteAsync(batch.MatchId, player.EntityId, request.FunctionContext.CancellationToken);
                outcomes.Add(new { player.EntityId, settled = true, totals.Matches, totals.Eliminations, totals.Wins, totals.Level });
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                await receipts.ReleaseAsync(batch.MatchId, player.EntityId, request.FunctionContext.CancellationToken);
                outcomes.Add(new { player.EntityId, settled = false, reason = "failed" });
            }
        }
        return await Reply(request, HttpStatusCode.OK, new { matchId = batch.MatchId, outcomes });
    }

    private static bool Eligible(MatchParticipant player) => !player.IsBot && player.RewardEligible && !string.IsNullOrWhiteSpace(player.EntityId) && player.EntityType == "title_player_account";

    private async Task WriteTotals(string transactionId, string entityId, string entityType, PlayerTotals totals, CancellationToken token)
    {
        await playFab.CallAsync("Statistic", "UpdateStatistics", new
        {
            Entity = new { Id = entityId, Type = entityType }, TransactionId = transactionId,
            Statistics = new object[] { new { Name = options.PlayerPerformanceStatistic, Scores = new[] { totals.Matches.ToString(), totals.Wins.ToString(), totals.Eliminations.ToString(), totals.Level.ToString() }, Metadata = JsonSerializer.Serialize(totals, JsonOptions) } }
        }, token);
    }

    private async Task<PlayerTotals> ReadTotals(string entityId, string entityType)
    {
        JsonElement response = await playFab.CallAsync("Statistic", "GetStatistics", new { Entity = new { Id = entityId, Type = entityType }, StatisticNames = new[] { options.PlayerPerformanceStatistic } });
        PlayerTotals totals = new();
        if (!response.TryGetProperty("Statistics", out JsonElement values)) return totals;
        foreach (JsonElement statistic in values.EnumerateArray())
        {
            if (statistic.GetProperty("Name").GetString() != options.PlayerPerformanceStatistic || !statistic.TryGetProperty("Scores", out JsonElement scores)) continue;
            totals.Matches = Score(scores, 0); totals.Wins = Score(scores, 1); totals.Eliminations = Score(scores, 2); totals.Level = Math.Max(1, Score(scores, 3));
        }
        return totals;
    }
    private static int Score(JsonElement scores, int index) => scores.ValueKind != JsonValueKind.Array || scores.GetArrayLength() <= index ? 0 : scores[index].ValueKind == JsonValueKind.Number && scores[index].TryGetInt32(out int n) ? n : int.TryParse(scores[index].GetString(), out n) ? n : 0;
    private static async Task<HttpResponseData> Reply(HttpRequestData request, HttpStatusCode status, object body) { HttpResponseData response = request.CreateResponse(status); response.Headers.Add("Content-Type", "application/json; charset=utf-8"); await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions)); return response; }

    private sealed class MatchSettlementBatch { public string MatchId { get; set; } = string.Empty; public List<MatchParticipant> Participants { get; set; } = new(); }
    private sealed class MatchParticipant { public string EntityId { get; set; } = string.Empty; public string EntityType { get; set; } = "title_player_account"; public string Nickname { get; set; } = "Capivara"; public string AvatarId { get; set; } = "avatar1"; public int Eliminations { get; set; } public int Placement { get; set; } public int Level { get; set; } = 1; public bool IsBot { get; set; } public bool RewardEligible { get; set; } }
    private sealed class PlayerTotals { public string Nickname { get; set; } = "Capivara"; public string AvatarId { get; set; } = "avatar1"; public int Matches { get; set; } public int Eliminations { get; set; } public int Wins { get; set; } public int Level { get; set; } = 1; }
}
