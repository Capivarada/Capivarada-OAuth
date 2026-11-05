using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public sealed class PlayFabAdminClient
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IConfiguration configuration;
    private readonly ILogger<PlayFabAdminClient> logger;

    public PlayFabAdminClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<PlayFabAdminClient> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.configuration = configuration;
        this.logger = logger;
    }

    public async Task<PlayFabResetResult> ResetPasswordAsync(string token, string password)
    {
        string? titleId = configuration["PLAYFAB_TITLE_ID"];
        string? secretKey = configuration["PLAYFAB_SECRET_KEY"];

        if (string.IsNullOrWhiteSpace(titleId) || string.IsNullOrWhiteSpace(secretKey))
        {
            logger.LogError("As configurações privadas do PlayFab não foram definidas.");
            return new PlayFabResetResult(false, HttpStatusCode.InternalServerError);
        }

        using HttpRequestMessage request = new(HttpMethod.Post, $"https://{titleId}.playfabapi.com/Admin/ResetPassword");
        request.Headers.Add("X-SecretKey", secretKey);
        request.Content = JsonContent.Create(new { Token = token, Password = password });

        using HttpClient client = httpClientFactory.CreateClient();
        using HttpResponseMessage response = await client.SendAsync(request);
        return new PlayFabResetResult(response.IsSuccessStatusCode, response.StatusCode);
    }
}

public readonly record struct PlayFabResetResult(bool Succeeded, HttpStatusCode StatusCode);
