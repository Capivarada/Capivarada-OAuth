using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

/// <summary>
/// Updates only the public metadata projected by leaderboards. Scores never
/// come from a player request: they are read first and written back unchanged
/// through the title entity.
/// </summary>
public sealed class ProfilePresentationFunction : AuthenticatedFunction
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly PlayFabTitleEntityClient title;
    private readonly CapivaradaOptions options;

    public ProfilePresentationFunction(PlayerSessionService sessions, PlayFabTitleEntityClient title, CapivaradaOptions options)
        : base(sessions)
    {
        this.title = title;
        this.options = options;
    }

    [Function("SyncLeaderboardPresentation")]
    public async Task<HttpResponseData> Sync([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "profiles/me/presentation")] HttpRequestData request)
    {
        var (session, error) = await RequirePlayerAsync(request);
        if (error != null || !session.HasValue) return error!;

        PresentationInput? input = await JsonSerializer.DeserializeAsync<PresentationInput>(request.Body, Json);
        if (input == null || (string.IsNullOrWhiteSpace(input.Nickname) && string.IsNullOrWhiteSpace(input.AvatarId)))
            return await Reply(request, HttpStatusCode.BadRequest, new { error = "Apelido ou avatar são obrigatórios." });

        try
        {
            string playerId = session.Value.PlayFabId;
            JsonElement data = await title.CallAsync("Statistic", "GetStatistics", new
            {
                Entity = new { Id = playerId, Type = "title_player_account" },
                StatisticNames = new[] { options.PlayerPerformanceStatistic }
            });

            if (!TryRead(data, out string[] scores, out PlayerPerformanceMetadata metadata))
                return await Reply(request, HttpStatusCode.OK, new { synchronized = false, reason = "noProgressionYet" });

            if (!string.IsNullOrWhiteSpace(input.Nickname)) metadata.Nickname = input.Nickname.Trim();
            if (!string.IsNullOrWhiteSpace(input.AvatarId)) metadata.AvatarId = input.AvatarId.Trim();

            await title.CallAsync("Statistic", "UpdateStatistics", new
            {
                Entity = new { Id = playerId, Type = "title_player_account" },
                TransactionId = $"presentation:{playerId}:{Guid.NewGuid():N}",
                Statistics = new[]
                {
                    new
                    {
                        Name = options.PlayerPerformanceStatistic,
                        Scores = scores,
                        Metadata = JsonSerializer.Serialize(metadata, Json)
                    }
                }
            });

            return await Reply(request, HttpStatusCode.OK, new { synchronized = true });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return await Reply(request, HttpStatusCode.InternalServerError, new { error = "Falha ao sincronizar a apresentação pública." });
        }
    }

    private bool TryRead(JsonElement data, out string[] scores, out PlayerPerformanceMetadata metadata)
    {
        scores = Array.Empty<string>();
        metadata = new PlayerPerformanceMetadata();
        if (!data.TryGetProperty("Statistics", out JsonElement statistics)) return false;

        foreach (JsonElement statistic in statistics.EnumerateArray())
        {
            if (statistic.GetProperty("Name").GetString() != options.PlayerPerformanceStatistic ||
                !statistic.TryGetProperty("Scores", out JsonElement rawScores)) continue;

            scores = rawScores.EnumerateArray().Select(value => value.ToString()).ToArray();
            if (statistic.TryGetProperty("Metadata", out JsonElement rawMetadata) &&
                rawMetadata.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(rawMetadata.GetString()))
            {
                try { metadata = JsonSerializer.Deserialize<PlayerPerformanceMetadata>(rawMetadata.GetString()!, Json) ?? metadata; }
                catch (JsonException) { }
            }
            return scores.Length == 4;
        }

        return false;
    }

    private static async Task<HttpResponseData> Reply(HttpRequestData request, HttpStatusCode status, object body)
    {
        HttpResponseData response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, Json));
        return response;
    }

    private sealed class PresentationInput
    {
        public string Nickname { get; set; } = string.Empty;
        public string AvatarId { get; set; } = string.Empty;
    }

    private sealed class PlayerPerformanceMetadata
    {
        public string Nickname { get; set; } = "Capivara";
        public string AvatarId { get; set; } = "avatar1";
        public int Matches { get; set; }
        public int Eliminations { get; set; }
        public int Wins { get; set; }
        public int Level { get; set; } = 1;
        public int Experience { get; set; }
        public int ExperienceToNext { get; set; } = 1500;
        public int TotalExperience { get; set; }
        public int PlayTimeSeconds { get; set; }
    }
}
