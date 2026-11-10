using Microsoft.Extensions.Configuration;

public sealed class CapivaradaOptions
{
    public string CurrencyId { get; init; } = "PA";
    public string CatalogId { get; init; } = "Capivarada";
    public string StoreId { get; init; } = "CapivaradaStore";
    public string MatchesLeaderboard { get; init; } = "RankingMatches";
    public string EliminationsLeaderboard { get; init; } = "RankingEliminations";
    public string WinsLeaderboard { get; init; } = "RankingWins";
    public string LevelLeaderboard { get; init; } = "RankingLevel";
    /// <summary>
    /// One multicolumn statistic. Its score order is Matches, Wins,
    /// Eliminations, Level, matching the PlayFab definition.
    /// </summary>
    public string PlayerPerformanceStatistic { get; init; } = "PlayerPerformance";
    /// <summary>Server-side PA reward tuning. These never come from Unity.</summary>
    public int ArenaPointsParticipation { get; init; } = 20;
    public int ArenaPointsPerElimination { get; init; } = 10;
    public int ArenaPointsVictoryBonus { get; init; } = 80;

    public static CapivaradaOptions From(IConfiguration configuration) => new()
    {
        CurrencyId = configuration["CAPIVARADA_CURRENCY_ID"] ?? "PA",
        CatalogId = configuration["CAPIVARADA_CATALOG_ID"] ?? "Capivarada",
        StoreId = configuration["CAPIVARADA_STORE_ID"] ?? "CapivaradaStore",
        MatchesLeaderboard = configuration["LEADERBOARD_MATCHES"] ?? "RankingMatches",
        EliminationsLeaderboard = configuration["LEADERBOARD_ELIMINATIONS"] ?? "RankingEliminations",
        WinsLeaderboard = configuration["LEADERBOARD_WINS"] ?? "RankingWins",
        LevelLeaderboard = configuration["LEADERBOARD_LEVEL"] ?? "RankingLevel"
        ,PlayerPerformanceStatistic = configuration["STATISTIC_PLAYER_PERFORMANCE"] ?? "PlayerPerformance",
        ArenaPointsParticipation = ReadNonNegative(configuration, "CAPIVARADA_PA_PARTICIPATION", 20),
        ArenaPointsPerElimination = ReadNonNegative(configuration, "CAPIVARADA_PA_PER_ELIMINATION", 10),
        ArenaPointsVictoryBonus = ReadNonNegative(configuration, "CAPIVARADA_PA_VICTORY_BONUS", 80)
    };

    private static int ReadNonNegative(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], out int value) ? Math.Max(0, value) : fallback;
}
