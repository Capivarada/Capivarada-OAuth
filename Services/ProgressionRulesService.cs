using System.Text.Json;

/// <summary>Loads the authoritative progression balance from PlayFab Title Data.
/// A safe local fallback keeps settlement available if Title Data is temporarily unavailable.</summary>
public sealed class ProgressionRulesService
{
    private const string Key = "capivarada.progression";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private readonly PlayFabServerClient playFab;
    private ProgressionRules? cached;
    private DateTime cacheExpiresUtc;

    public ProgressionRulesService(PlayFabServerClient playFab) => this.playFab = playFab;

    public async Task<ProgressionRules> GetAsync(CancellationToken token = default)
    {
        if (cached != null && DateTime.UtcNow < cacheExpiresUtc) return cached;
        try
        {
            JsonElement response = await playFab.CallAsync("GetTitleData", new { Keys = new[] { Key } }, token);
            if (response.TryGetProperty("Data", out JsonElement data) &&
                data.TryGetProperty(Key, out JsonElement raw) && !string.IsNullOrWhiteSpace(raw.GetString()))
            {
                ProgressionRules? parsed = JsonSerializer.Deserialize<ProgressionRules>(raw.GetString()!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null && parsed.IsValid()) cached = parsed.Normalize();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("[Progression] Title Data indisponível; usando a última regra válida. " + exception.Message);
        }

        cached ??= ProgressionRules.Default;
        cacheExpiresUtc = DateTime.UtcNow.Add(CacheLifetime);
        return cached;
    }
}

public sealed class ProgressionRules
{
    public int ExperiencePerLevel { get; set; } = 1500;
    public int XpPerElimination { get; set; } = 50;
    public int XpPerRoundSurvived { get; set; } = 20;
    public int XpPerMatchCompleted { get; set; } = 250;
    public Dictionary<string, int> XpByPlacement { get; set; } = new()
    {
        ["1"] = 400, ["2"] = 300, ["3"] = 200, ["4"] = 100,
        ["5"] = 100, ["6"] = 100, ["7"] = 100, ["8"] = 100
    };

    public static ProgressionRules Default => new();
    public bool IsValid() => ExperiencePerLevel > 0 && XpPerElimination >= 0 && XpPerRoundSurvived >= 0 && XpPerMatchCompleted >= 0;
    public ProgressionRules Normalize()
    {
        ExperiencePerLevel = Math.Max(1, ExperiencePerLevel);
        XpPerElimination = Math.Max(0, XpPerElimination);
        XpPerRoundSurvived = Math.Max(0, XpPerRoundSurvived);
        XpPerMatchCompleted = Math.Max(0, XpPerMatchCompleted);
        XpByPlacement ??= new Dictionary<string, int>();
        return this;
    }
    public int PlacementXp(int placement) => XpByPlacement.TryGetValue(Math.Max(1, placement).ToString(), out int value) ? Math.Max(0, value) : 0;
}
