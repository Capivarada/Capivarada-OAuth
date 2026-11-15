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

    public async Task<PlayFabRecoveryEmailResult> SendRecoveryEmailAsync(string email)
    {
        string? titleId = configuration["PLAYFAB_TITLE_ID"];
        string? secretKey = configuration["PLAYFAB_SECRET_KEY"];
        string? templateId = configuration["PLAYFAB_RECOVERY_EMAIL_TEMPLATE_ID"];

        if (string.IsNullOrWhiteSpace(titleId) || string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(templateId))
        {
            logger.LogError("As configurações de recuperação de senha do PlayFab não foram definidas.");
            return new PlayFabRecoveryEmailResult(false, HttpStatusCode.InternalServerError);
        }

        using HttpRequestMessage request = new(HttpMethod.Post, $"https://{titleId}.playfabapi.com/Admin/SendAccountRecoveryEmail");
        request.Headers.Add("X-SecretKey", secretKey);
        request.Content = JsonContent.Create(new { Email = email, EmailTemplateId = templateId });

        using HttpClient client = httpClientFactory.CreateClient();
        using HttpResponseMessage response = await client.SendAsync(request);
        return new PlayFabRecoveryEmailResult(response.IsSuccessStatusCode, response.StatusCode);
    }

    /// <summary>Resolves the short-lived token produced by a PlayFab account-recovery e-mail.</summary>
    public async Task<string?> ResolveEmailTokenPlayerIdAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        string? titleId = configuration["PLAYFAB_TITLE_ID"];
        string? secretKey = configuration["PLAYFAB_SECRET_KEY"];
        if (string.IsNullOrWhiteSpace(titleId) || string.IsNullOrWhiteSpace(secretKey)) return null;

        using HttpRequestMessage request = new(HttpMethod.Post, $"https://{titleId}.playfabapi.com/Admin/GetPlayerIdFromAuthToken");
        request.Headers.Add("X-SecretKey", secretKey);
        request.Content = JsonContent.Create(new { Token = token, TokenType = "Email" });
        using HttpResponseMessage response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("PlayFabId", out var id)
            ? id.GetString()
            : null;
    }

    /// <summary>
    /// Deletes title-scoped player data after a validated e-mail confirmation.
    /// The Admin endpoint is intentional: unlike Server/DeletePlayer, it is
    /// not disabled by PlayFab's game-server API feature switch.
    /// </summary>
    public async Task DeletePlayerAsync(string playerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playerId))
            throw new ArgumentException("PlayFabId é obrigatório.", nameof(playerId));

        string? titleId = configuration["PLAYFAB_TITLE_ID"];
        string? secretKey = configuration["PLAYFAB_SECRET_KEY"];
        if (string.IsNullOrWhiteSpace(titleId) || string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Configuração privada do PlayFab indisponível.");

        using HttpRequestMessage request = new(HttpMethod.Post,
            $"https://{titleId}.playfabapi.com/Admin/DeletePlayer");
        request.Headers.Add("X-SecretKey", secretKey);
        request.Content = JsonContent.Create(new { PlayFabId = playerId });
        using HttpResponseMessage response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return;

        string detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"PlayFab Admin/DeletePlayer ({(int)response.StatusCode}): {detail}");
    }

    /// <summary>Validates a password without retaining it or creating local credentials.</summary>
    public async Task<bool> ValidatePasswordAsync(string playerId, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(password)) return false;
        string? titleId = configuration["PLAYFAB_TITLE_ID"];
        string? secretKey = configuration["PLAYFAB_SECRET_KEY"];
        if (string.IsNullOrWhiteSpace(titleId) || string.IsNullOrWhiteSpace(secretKey)) return false;

        using HttpRequestMessage accountRequest = new(HttpMethod.Post,
            $"https://{titleId}.playfabapi.com/Admin/GetUserAccountInfo");
        accountRequest.Headers.Add("X-SecretKey", secretKey);
        accountRequest.Content = JsonContent.Create(new { PlayFabId = playerId });
        using HttpResponseMessage accountResponse = await httpClientFactory.CreateClient().SendAsync(accountRequest, cancellationToken);
        if (!accountResponse.IsSuccessStatusCode) return false;

        using System.Text.Json.JsonDocument account = System.Text.Json.JsonDocument.Parse(
            await accountResponse.Content.ReadAsStringAsync(cancellationToken));
        if (!account.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("UserInfo", out var user) ||
            !user.TryGetProperty("PrivateInfo", out var privateInfo) ||
            !privateInfo.TryGetProperty("Email", out var emailValue)) return false;
        string? email = emailValue.GetString();
        if (string.IsNullOrWhiteSpace(email)) return false;

        using HttpRequestMessage loginRequest = new(HttpMethod.Post,
            $"https://{titleId}.playfabapi.com/Client/LoginWithEmailAddress");
        loginRequest.Content = JsonContent.Create(new { TitleId = titleId, Email = email, Password = password });
        using HttpResponseMessage loginResponse = await httpClientFactory.CreateClient().SendAsync(loginRequest, cancellationToken);
        if (!loginResponse.IsSuccessStatusCode) return false;

        using System.Text.Json.JsonDocument login = System.Text.Json.JsonDocument.Parse(
            await loginResponse.Content.ReadAsStringAsync(cancellationToken));
        return login.RootElement.TryGetProperty("data", out var loginData) &&
            loginData.TryGetProperty("PlayFabId", out var verifiedId) &&
            string.Equals(verifiedId.GetString(), playerId, StringComparison.Ordinal);
    }
}

public readonly record struct PlayFabResetResult(bool Succeeded, HttpStatusCode StatusCode);
public readonly record struct PlayFabRecoveryEmailResult(bool Succeeded, HttpStatusCode StatusCode);
