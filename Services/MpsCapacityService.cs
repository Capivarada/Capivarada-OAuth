using System.Text.Json;
using Microsoft.Extensions.Configuration;

/// <summary>Maintains one warm EastUS MPS only while authenticated players are online.
/// It deliberately never allocates a match; it only manages build standby capacity.</summary>
public sealed class MpsCapacityService
{
    private readonly PlayFabTitleEntityClient multiplayer;
    private readonly OnlinePresenceStore presence;
    private readonly MatchAllocationStore allocations;
    private readonly IConfiguration configuration;
    private readonly SemaphoreSlim gate = new(1, 1);

    public MpsCapacityService(PlayFabTitleEntityClient multiplayer, OnlinePresenceStore presence, MatchAllocationStore allocations, IConfiguration configuration)
    { this.multiplayer = multiplayer; this.presence = presence; this.allocations = allocations; this.configuration = configuration; }

    public async Task<CapacityStatus> HeartbeatAsync(string playerId, CancellationToken token = default)
    {
        await presence.TouchAsync(playerId, token);
        return await ReconcileAsync(token);
    }

    public Task<CapacityStatus> StatusAsync(CancellationToken token = default) => ReconcileAsync(token);

    public async Task<CapacityStatus> ReconcileAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            string buildId = configuration["CAPIVARADA_MPS_BUILD_ID"] ?? string.Empty;
            if (!Guid.TryParse(buildId, out _)) return new CapacityStatus(false, "unconfigured", 10, 0, 0, 0, 0);

            TimeSpan ttl = TimeSpan.FromSeconds(ReadPositive("MPS_PRESENCE_TTL_SECONDS", 90));
            TimeSpan grace = TimeSpan.FromMinutes(ReadPositive("MPS_PRESENCE_GRACE_MINUTES", 20));
            int online = await presence.CountRecentAsync(ttl, token);
            bool activeMatch = await allocations.HasStartedSessionAsync(token);
            DateTime? lastOnline = await presence.LastOnlineUtcAsync(token);
            bool retainWarm = online > 0 || activeMatch || (lastOnline.HasValue && DateTime.UtcNow - lastOnline.Value < grace);
            int targetStandby = retainWarm ? Math.Max(1, ReadPositive("MPS_STANDBY_TARGET", 1)) : 0;
            int targetMaximum = targetStandby + (activeMatch ? 1 : 0);

            JsonElement build = await multiplayer.CallAsync("MultiplayerServer", "GetBuild", new { BuildId = buildId }, token);
            JsonElement region = FindRegion(build, Region());
            int configuredStandby = Number(region, "StandbyServers");
            int configuredMaximum = Number(region, "MaxServers");
            JsonElement stats = Property(region, "CurrentServerStats");
            int standingBy = Number(stats, "StandingBy");
            int propping = Number(stats, "Propping");
            int active = Number(stats, "Active");
            if (configuredStandby != targetStandby || configuredMaximum != targetMaximum)
            {
                await multiplayer.CallAsync("MultiplayerServer", "UpdateBuildRegion", new
                {
                    BuildId = buildId,
                    BuildRegion = new
                    {
                        Region = Region(),
                        StandbyServers = targetStandby,
                        MaxServers = targetMaximum
                    }
                }, token);
                // Configuration propagation is asynchronous. A VM that is
                // already StandingBy is safe to use immediately, even when
                // GetBuild still reports the previous target values.
                bool alreadyReady = targetStandby > 0 && standingBy > 0;
                return new CapacityStatus(alreadyReady, alreadyReady ? "ready" : targetStandby == 0 ? "offline" : "warming", alreadyReady ? 0 : 5, online, standingBy, propping, active);
            }

            bool ready = targetStandby == 0 || standingBy > 0;
            return new CapacityStatus(ready, ready ? "ready" : "warming", ready ? 0 : 5, online, standingBy, propping, active);
        }
        finally { gate.Release(); }
    }

    private string Region() => configuration["CAPIVARADA_MPS_REGION"] ?? "EastUs";
    private int ReadPositive(string key, int fallback) => int.TryParse(configuration[key], out int value) ? Math.Max(1, value) : fallback;
    private static JsonElement FindRegion(JsonElement build, string wanted)
    {
        if (build.TryGetProperty("BuildRegions", out JsonElement regions) && regions.ValueKind == JsonValueKind.Array)
            foreach (JsonElement region in regions.EnumerateArray())
                if (string.Equals(Property(region, "Region").GetString(), wanted, StringComparison.OrdinalIgnoreCase)) return region;
        return default;
    }
    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) ? property : default;
    private static int Number(JsonElement value, string name)
    {
        JsonElement property = Property(value, name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int number) ? number :
            property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out number) ? number : 0;
    }
}

public readonly record struct CapacityStatus(bool Ready, string Status, int RetryAfterSeconds, int OnlinePlayers, int StandingByServers, int ProppingServers, int ActiveServers);
