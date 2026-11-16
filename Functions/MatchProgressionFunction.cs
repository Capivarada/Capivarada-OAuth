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
    private readonly PlayFabServerClient playerData;
    private readonly CapivaradaOptions options;
    private readonly IConfiguration configuration;
    private readonly MatchSettlementReceiptService receipts;
    private readonly MatchJoinTokenService joinTokens;
    private readonly MatchAllocationStore allocations;
    private readonly ProgressionRulesService progression;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public MatchProgressionFunction(PlayFabTitleEntityClient playFab, PlayFabServerClient playerData, CapivaradaOptions options, IConfiguration configuration, MatchSettlementReceiptService receipts, MatchJoinTokenService joinTokens, MatchAllocationStore allocations, ProgressionRulesService progression)
    { this.playFab = playFab; this.playerData = playerData; this.options = options; this.configuration = configuration; this.receipts = receipts; this.joinTokens = joinTokens; this.allocations = allocations; this.progression = progression; }

    [Function("SettleMatchProgression")]
    public async Task<HttpResponseData> Settle([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "matches/settle")] HttpRequestData request)
    {
        if (!request.Headers.TryGetValues("X-Capivarada-Match-Secret", out IEnumerable<string>? supplied) ||
            string.IsNullOrWhiteSpace(configuration["CAPIVARADA_MATCH_SERVER_SECRET"]) || supplied.FirstOrDefault() != configuration["CAPIVARADA_MATCH_SERVER_SECRET"])
            return await Reply(request, HttpStatusCode.Unauthorized, new { error = "Apenas o servidor de partida pode registrar resultados." });

        MatchSettlementBatch? batch = await JsonSerializer.DeserializeAsync<MatchSettlementBatch>(request.Body, JsonOptions);
        if (batch == null || string.IsNullOrWhiteSpace(batch.MatchId) || batch.Participants == null || batch.Participants.Count is < 1 or > 8 || batch.DurationSeconds is < 0 or > 14400)
            return await Reply(request, HttpStatusCode.BadRequest, new { error = "MatchId e entre um e oito participantes são obrigatórios." });
        // Bots have no PlayFab entity. Validate uniqueness only for the human
        // participants so an arena result can never block every human reward
        // merely because several bots use an empty identifier.
        List<MatchParticipant> humanParticipants = batch.Participants
            .Where(player => !player.IsBot)
            .ToList();
        if (humanParticipants.Select(x => x.EntityId)
                .Distinct(StringComparer.Ordinal).Count() != humanParticipants.Count)
            return await Reply(request, HttpStatusCode.BadRequest, new { error = "A mesma entidade foi enviada mais de uma vez." });

        List<object> outcomes = new();
        ProgressionRules rules = await progression.GetAsync(request.FunctionContext.CancellationToken);
        bool settlementFailed = false;
        foreach (MatchParticipant player in batch.Participants)
        {
            if (!Eligible(player, batch.MatchId))
            {
                outcomes.Add(new { player.EntityId, settled = false, reason = player.IsBot ? "bot" : "notEligible" });
                if (!player.IsBot) settlementFailed = true;
                continue;
            }
            if (!await receipts.TryStartAsync(batch.MatchId, player.EntityId, request.FunctionContext.CancellationToken)) { outcomes.Add(new { player.EntityId, settled = false, reason = "alreadySettled" }); continue; }
            try
            {
                PlayerTotals previous = await ReadTotals(player.EntityId, player.EntityType);
                PlayerTotals totals = new()
                {
                    Matches = previous.Matches + 1,
                    Eliminations = previous.Eliminations + Math.Max(0, player.Eliminations),
                    Wins = previous.Wins + (player.Placement == 1 ? 1 : 0),
                    Nickname = string.IsNullOrWhiteSpace(player.Nickname) ? previous.Nickname : player.Nickname.Trim(),
                    AvatarId = string.IsNullOrWhiteSpace(player.AvatarId) ? previous.AvatarId : player.AvatarId.Trim(),
                    CreatedAtUtc = previous.CreatedAtUtc
                };
                ProgressionAward award = Award(player, rules);
                totals.TotalExperience = previous.TotalExperience + award.Earned;
                totals.Level = Math.Max(1, totals.TotalExperience / rules.ExperiencePerLevel + 1);
                totals.Experience = totals.TotalExperience % rules.ExperiencePerLevel;
                totals.ExperienceToNext = rules.ExperiencePerLevel;
                await WriteTotals(batch.MatchId + ":" + player.EntityId, player.EntityId, player.EntityType, totals, request.FunctionContext.CancellationToken);
                await WritePublicRecords(player.EntityId, totals, request.FunctionContext.CancellationToken);
                int earnedPoints = ArenaPoints(player);
                if (earnedPoints > 0)
                    await GrantArenaPoints(batch.MatchId, player.EntityId, player.EntityType, earnedPoints, request.FunctionContext.CancellationToken);
                await receipts.CompleteAsync(batch.MatchId, player.EntityId, request.FunctionContext.CancellationToken);
                outcomes.Add(new { player.EntityId, settled = true, totals.Matches, totals.Eliminations, totals.Wins, totals.Level, totals.Experience, totals.TotalExperience, totals.ExperienceToNext, xpEarned = award.Earned, placementXp = award.Placement, eliminationXp = award.Eliminations, roundsXp = award.Rounds, matchCompletionXp = award.MatchCompletion, arenaPointsEarned = earnedPoints });
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                await receipts.ReleaseAsync(batch.MatchId, player.EntityId, request.FunctionContext.CancellationToken);
                outcomes.Add(new { player.EntityId, settled = false, reason = "failed", detail = exception.Message });
                settlementFailed = true;
            }
        }
        // A settled match must never be returned by /matches/allocate again.
        await allocations.CloseSessionAsync(
            batch.MatchId, request.FunctionContext.CancellationToken);
        if (settlementFailed)
            return await Reply(request, HttpStatusCode.UnprocessableEntity,
                new { error = "Uma ou mais recompensas não foram registradas.", matchId = batch.MatchId, outcomes });
        return await Reply(request, HttpStatusCode.OK, new { matchId = batch.MatchId, outcomes });
    }

    private bool Eligible(MatchParticipant player, string matchId) =>
        !player.IsBot && player.RewardEligible && !string.IsNullOrWhiteSpace(player.EntityId) &&
        player.EntityType == "title_player_account" && joinTokens.TryValidate(player.JoinToken, player.EntityId, matchId);

    // Mirrors the validated legacy progression formula, but the calculation now
    // exists only in the trusted settlement endpoint.
    private static ProgressionAward Award(MatchParticipant player, ProgressionRules rules)
    {
        int placement = rules.PlacementXp(player.Placement);
        int eliminations = Math.Max(0, player.Eliminations) * rules.XpPerElimination;
        int rounds = Math.Max(0, player.RoundsSurvived) * rules.XpPerRoundSurvived;
        return new ProgressionAward(placement + eliminations + rounds + rules.XpPerMatchCompleted, placement, eliminations, rounds, rules.XpPerMatchCompleted);
    }

    private int ArenaPoints(MatchParticipant player) =>
        options.ArenaPointsParticipation + Math.Max(0, player.Eliminations) * options.ArenaPointsPerElimination +
        (player.Placement == 1 ? options.ArenaPointsVictoryBonus : 0);

    private async Task WriteTotals(string transactionId, string entityId, string entityType, PlayerTotals totals, CancellationToken token)
    {
        await playFab.CallAsync("Statistic", "UpdateStatistics", new
        {
            Entity = new { Id = entityId, Type = entityType }, TransactionId = transactionId,
            Statistics = new object[] { new { Name = options.PlayerPerformanceStatistic, Scores = new[] { totals.Matches.ToString(), totals.Wins.ToString(), totals.Eliminations.ToString(), totals.Level.ToString() }, Metadata = JsonSerializer.Serialize(totals, JsonOptions) } }
        }, token);
    }

    private Task GrantArenaPoints(string matchId, string entityId, string entityType, int amount, CancellationToken token) =>
        playFab.CallAsync("Inventory", "ExecuteInventoryOperations", new
        {
            Entity = new { Id = entityId, Type = entityType },
            CollectionId = "default",
            IdempotencyId = $"{matchId}:{entityId}:pa",
            Operations = new object[]
            {
                new { Add = new { Item = new { Id = options.CurrencyId }, Amount = amount } }
            }
        }, token);

    private Task WritePublicRecords(string playerId, PlayerTotals totals, CancellationToken token) =>
        playerData.CallAsync("UpdateUserData", new
        {
            PlayFabId = playerId,
            Data = new Dictionary<string, string>
            {
                ["progression"] = JsonSerializer.Serialize(new { level = totals.Level, experience = totals.Experience, totalExperience = totals.TotalExperience, experienceToNext = totals.ExperienceToNext }, JsonOptions),
                ["statistics"] = JsonSerializer.Serialize(new { matches = totals.Matches, wins = totals.Wins, eliminations = totals.Eliminations }, JsonOptions)
            },
            Permission = "Public"
        }, token);

    private async Task<PlayerTotals> ReadTotals(string entityId, string entityType)
    {
        JsonElement response = await playFab.CallAsync("Statistic", "GetStatistics", new { Entity = new { Id = entityId, Type = entityType }, StatisticNames = new[] { options.PlayerPerformanceStatistic } });
        PlayerTotals totals = new();
        if (!response.TryGetProperty("Statistics", out JsonElement values)) return totals;
        foreach (JsonElement statistic in values.EnumerateArray())
        {
            if (statistic.GetProperty("Name").GetString() != options.PlayerPerformanceStatistic || !statistic.TryGetProperty("Scores", out JsonElement scores)) continue;
            totals.Matches = Score(scores, 0); totals.Wins = Score(scores, 1); totals.Eliminations = Score(scores, 2); totals.Level = Math.Max(1, Score(scores, 3));
            if (statistic.TryGetProperty("Metadata", out JsonElement metadata) && metadata.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(metadata.GetString()))
            {
                try { totals = JsonSerializer.Deserialize<PlayerTotals>(metadata.GetString()!, JsonOptions) ?? totals; }
                catch (JsonException) { }
            }
            totals.Level = Math.Max(1, totals.Level);
            ProgressionRules rules = await progression.GetAsync();
            totals.ExperienceToNext = totals.ExperienceToNext > 0 ? totals.ExperienceToNext : rules.ExperiencePerLevel;
            totals.TotalExperience = Math.Max(totals.TotalExperience, (totals.Level - 1) * rules.ExperiencePerLevel + Math.Max(0, totals.Experience));
        }
        return totals;
    }
    private static int Score(JsonElement scores, int index) => scores.ValueKind != JsonValueKind.Array || scores.GetArrayLength() <= index ? 0 : scores[index].ValueKind == JsonValueKind.Number && scores[index].TryGetInt32(out int n) ? n : int.TryParse(scores[index].GetString(), out n) ? n : 0;
    private static async Task<HttpResponseData> Reply(HttpRequestData request, HttpStatusCode status, object body) { HttpResponseData response = request.CreateResponse(status); response.Headers.Add("Content-Type", "application/json; charset=utf-8"); await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions)); return response; }

    private sealed class MatchSettlementBatch { public string MatchId { get; set; } = string.Empty; public int DurationSeconds { get; set; } public List<MatchParticipant> Participants { get; set; } = new(); }
    private sealed class MatchParticipant { public string EntityId { get; set; } = string.Empty; public string EntityType { get; set; } = "title_player_account"; public string JoinToken { get; set; } = string.Empty; public string Nickname { get; set; } = "Capivara"; public string AvatarId { get; set; } = "avatar1"; public int Eliminations { get; set; } public int RoundsSurvived { get; set; } public int Placement { get; set; } public bool IsBot { get; set; } public bool RewardEligible { get; set; } }
    private sealed class PlayerTotals { public string Nickname { get; set; } = "Capivara"; public string AvatarId { get; set; } = "avatar1"; public DateTime CreatedAtUtc { get; set; } = DateTime.MaxValue; public int Matches { get; set; } public int Eliminations { get; set; } public int Wins { get; set; } public int Level { get; set; } = 1; public int Experience { get; set; } public int ExperienceToNext { get; set; } = 1500; public int TotalExperience { get; set; } }
    private readonly record struct ProgressionAward(int Earned, int Placement, int Eliminations, int Rounds, int MatchCompletion);
}
