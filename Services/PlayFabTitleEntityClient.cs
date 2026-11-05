using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

/// <summary>Server-only title entity client, used by trusted match servers. Its token never reaches Unity.</summary>
public sealed class PlayFabTitleEntityClient
{
    private readonly IHttpClientFactory clients;
    private readonly IConfiguration configuration;
    private string? entityToken;
    private DateTime expiresUtc;
    public PlayFabTitleEntityClient(IHttpClientFactory clients, IConfiguration configuration) { this.clients = clients; this.configuration = configuration; }

    public async Task<JsonElement> CallAsync(string area, string operation, object payload, CancellationToken cancellationToken = default)
    {
        string titleId = Required("PLAYFAB_TITLE_ID");
        using HttpRequestMessage request = new(HttpMethod.Post, $"https://{titleId}.playfabapi.com/{area}/{operation}");
        request.Headers.Add("X-EntityToken", await TokenAsync(cancellationToken));
        request.Content = JsonContent.Create(payload);
        using HttpResponseMessage response = await clients.CreateClient().SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"PlayFab {area}/{operation} ({(int)response.StatusCode}): {body}");
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("data", out JsonElement data) ? data.Clone() : default;
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(entityToken) && DateTime.UtcNow < expiresUtc) return entityToken;
        string titleId = Required("PLAYFAB_TITLE_ID");
        using HttpRequestMessage request = new(HttpMethod.Post, $"https://{titleId}.playfabapi.com/Authentication/GetEntityToken");
        request.Headers.Add("X-SecretKey", Required("PLAYFAB_SECRET_KEY")); request.Content = JsonContent.Create(new { });
        using HttpResponseMessage response = await clients.CreateClient().SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Não foi possível obter o token de entidade do título.");
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement data = document.RootElement.GetProperty("data");
        entityToken = data.GetProperty("EntityToken").GetString() ?? throw new InvalidOperationException("Token de entidade vazio.");
        expiresUtc = data.TryGetProperty("TokenExpiration", out JsonElement expiry) && expiry.TryGetDateTime(out DateTime date) ? date.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(50);
        return entityToken;
    }
    private string Required(string key) => configuration[key] ?? throw new InvalidOperationException($"A configuração {key} não foi definida na Function App.");
}
