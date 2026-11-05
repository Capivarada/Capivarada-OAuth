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

    public static CapivaradaOptions From(IConfiguration configuration) => new()
    {
        CurrencyId = configuration["CAPIVARADA_CURRENCY_ID"] ?? "PA",
        CatalogId = configuration["CAPIVARADA_CATALOG_ID"] ?? "Capivarada",
        StoreId = configuration["CAPIVARADA_STORE_ID"] ?? "CapivaradaStore",
        MatchesLeaderboard = configuration["LEADERBOARD_MATCHES"] ?? "RankingMatches",
        EliminationsLeaderboard = configuration["LEADERBOARD_ELIMINATIONS"] ?? "RankingEliminations",
        WinsLeaderboard = configuration["LEADERBOARD_WINS"] ?? "RankingWins",
        LevelLeaderboard = configuration["LEADERBOARD_LEVEL"] ?? "RankingLevel"
        ,PlayerPerformanceStatistic = configuration["STATISTIC_PLAYER_PERFORMANCE"] ?? "PlayerPerformance"
    };
}
