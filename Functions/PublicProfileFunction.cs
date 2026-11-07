using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

/// <summary>
/// Allow-listed public profile projection for OtherProfile and public
/// leaderboard interactions. Private account data is never returned here.
/// </summary>
public sealed class PublicProfileFunction
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private readonly PlayFabServerClient playFab;
    private readonly PlayFabTitleEntityClient title;
    private readonly CapivaradaOptions options;

    public PublicProfileFunction(PlayFabServerClient playFab, PlayFabTitleEntityClient title, CapivaradaOptions options)
    {
        this.playFab = playFab;
        this.title = title;
        this.options = options;
    }

    [Function("GetPublicProfile")]
    public async Task<HttpResponseData> Get([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "profiles/{playerId}")] HttpRequestData request, string playerId)
    {
        if (!PlayFabServerClient.LooksLikePlayFabId(playerId))
            return await Reply(request, HttpStatusCode.BadRequest, new { error = "ID de jogador inválido." });

        try
        {
            PublicProfile source = await ReadLegacyProfile(playerId);
            // A legacy PlayFabId cannot always be queried as a title_player_account
            // entity. Preserve its public legacy projection instead of turning that
            // compatibility miss into a 404 for OtherProfile.
            try
            {
                await MergeProgression(source, playerId);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[PublicProfile] Progressão V2 indisponível para {playerId}: {exception.Message}");
            }
            source.PlayerId = playerId;
            source.Nickname = string.IsNullOrWhiteSpace(source.Nickname) ? "Capivara" : source.Nickname;
            source.AvatarId = string.IsNullOrWhiteSpace(source.AvatarId) ? "avatar1" : source.AvatarId;
            source.Level = Math.Max(1, source.Level);
            source.ExperienceToNext = source.ExperienceToNext > 0 ? source.ExperienceToNext : 1500;
            return await Reply(request, HttpStatusCode.OK, source);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return await Reply(request, HttpStatusCode.NotFound, new { error = "Perfil público não encontrado." });
        }
    }

    private async Task<PublicProfile> ReadLegacyProfile(string playerId)
    {
        try
        {
            JsonElement data = await playFab.CallAsync("GetUserData", new { PlayFabId = playerId, Keys = new[] { "profile" } });
            if (data.TryGetProperty("Data", out JsonElement records) && records.TryGetProperty("profile", out JsonElement profileRecord) && profileRecord.TryGetProperty("Value", out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString()))
                return JsonSerializer.Deserialize<PublicProfile>(value.GetString()!, Json) ?? new PublicProfile { PlayerId = playerId };
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[PublicProfile] Perfil legado indisponível para {playerId}: {exception.Message}");
        }
        return new PublicProfile { PlayerId = playerId };
    }

    private async Task MergeProgression(PublicProfile profile, string entityId)
    {
        JsonElement data = await title.CallAsync("Statistic", "GetStatistics", new
        {
            Entity = new { Id = entityId, Type = "title_player_account" },
            StatisticNames = new[] { options.PlayerPerformanceStatistic }
        });
        if (!data.TryGetProperty("Statistics", out JsonElement statistics)) return;
        foreach (JsonElement statistic in statistics.EnumerateArray())
        {
            if (statistic.GetProperty("Name").GetString() != options.PlayerPerformanceStatistic) continue;
            if (statistic.TryGetProperty("Scores", out JsonElement scores))
            {
                profile.Matches = Score(scores, 0);
                profile.Wins = Score(scores, 1);
                profile.Eliminations = Score(scores, 2);
                profile.Level = Math.Max(1, Score(scores, 3));
            }
            if (statistic.TryGetProperty("Metadata", out JsonElement metadata) && metadata.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(metadata.GetString()))
            {
                try
                {
                    ProgressionMetadata persisted = JsonSerializer.Deserialize<ProgressionMetadata>(metadata.GetString()!, Json) ?? new ProgressionMetadata();
                    profile.Nickname = string.IsNullOrWhiteSpace(persisted.Nickname) ? profile.Nickname : persisted.Nickname;
                    profile.AvatarId = string.IsNullOrWhiteSpace(persisted.AvatarId) ? profile.AvatarId : persisted.AvatarId;
                    profile.Level = Math.Max(1, persisted.Level);
                    profile.Experience = Math.Max(0, persisted.Experience);
                    profile.ExperienceToNext = persisted.ExperienceToNext > 0 ? persisted.ExperienceToNext : 1500;
                    profile.PlayTimeSeconds = Math.Max(0, persisted.PlayTimeSeconds);
                }
                catch (JsonException) { }
            }
            return;
        }
    }

    private static int Score(JsonElement scores, int index)
    {
        if (scores.ValueKind != JsonValueKind.Array || scores.GetArrayLength() <= index) return 0;
        JsonElement value = scores[index];
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
            ? number
            : int.TryParse(value.GetString(), out number) ? number : 0;
    }

    private static async Task<HttpResponseData> Reply(HttpRequestData request, HttpStatusCode code, object body)
    {
        HttpResponseData response = request.CreateResponse(code);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, Json));
        return response;
    }

    private sealed class PublicProfile
    {
        public string PlayerId { get; set; } = string.Empty;
        public string Nickname { get; set; } = "Capivara";
        public string AvatarId { get; set; } = "avatar1";
        public string EquippedSkinId { get; set; } = string.Empty;
        public int Level { get; set; } = 1;
        public int Experience { get; set; }
        public int ExperienceToNext { get; set; } = 1500;
        public int Matches { get; set; }
        public int Eliminations { get; set; }
        public int Wins { get; set; }
        public float PlayTimeSeconds { get; set; }
    }

    private sealed class ProgressionMetadata
    {
        public string Nickname { get; set; } = "Capivara";
        public string AvatarId { get; set; } = "avatar1";
        public int Level { get; set; } = 1;
        public int Experience { get; set; }
        public int ExperienceToNext { get; set; } = 1500;
        public int PlayTimeSeconds { get; set; }
    }
}
