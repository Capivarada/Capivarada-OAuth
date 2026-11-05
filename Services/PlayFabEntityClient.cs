using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Proxies Economy v2 and Leaderboard v2 using the authenticated player's entity token.
/// The ticket is independently validated before this client is reached.
/// </summary>
public sealed class PlayFabEntityClient
{
    private readonly IHttpClientFactory clients;
    private readonly IConfiguration configuration;

    public PlayFabEntityClient(IHttpClientFactory clients, IConfiguration configuration)
    {
        this.clients = clients;
        this.configuration = configuration;
    }

    public async Task<JsonElement> CallAsync(HttpRequestData incomingRequest, string area, string operation, object payload, CancellationToken cancellationToken = default)
    {
        if (!incomingRequest.Headers.TryGetValues("X-PlayFab-EntityToken", out IEnumerable<string>? values) || string.IsNullOrWhiteSpace(values.FirstOrDefault()))
            throw new InvalidOperationException("O token de entidade PlayFab está ausente. Faça login novamente.");
        string titleId = configuration["PLAYFAB_TITLE_ID"] ?? throw new InvalidOperationException("PLAYFAB_TITLE_ID não foi configurado.");
        using HttpRequestMessage request = new(HttpMethod.Post, $"https://{titleId}.playfabapi.com/{area}/{operation}");
        request.Headers.Add("X-EntityToken", values.First());
        request.Content = JsonContent.Create(payload);
        using HttpResponseMessage response = await clients.CreateClient().SendAsync(request, cancellationToken);
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"PlayFab {area}/{operation} ({(int)response.StatusCode}): {content}");
        using JsonDocument document = JsonDocument.Parse(content);
        return document.RootElement.TryGetProperty("data", out JsonElement data) ? data.Clone() : default;
    }
}
