using Microsoft.Extensions.Configuration;

/// <summary>
/// Server-only registry for PlayFab e-mail template IDs. IDs stay in Function
/// App settings, never in Unity or serialized scenes.
/// </summary>
public sealed class EmailTemplateService
{
    public const string LoginAlertTemplateKey = "CAPIVARADA_EMAIL_TEMPLATE_LOGIN_ALERT";
    public const string DeletionTemplateKey = "CAPIVARADA_EMAIL_TEMPLATE_ACCOUNT_DELETION";

    private readonly PlayFabServerClient playFab;
    private readonly IConfiguration configuration;

    public EmailTemplateService(PlayFabServerClient playFab, IConfiguration configuration)
    {
        this.playFab = playFab;
        this.configuration = configuration;
    }

    public bool LoginAlertConfigured => !string.IsNullOrWhiteSpace(configuration[LoginAlertTemplateKey]);
    public bool DeletionConfigured => !string.IsNullOrWhiteSpace(configuration[DeletionTemplateKey]);

    public Task SendLoginAlertAsync(string playerId, CancellationToken cancellationToken = default) =>
        SendAsync(playerId, LoginAlertTemplateKey, "login-alert", cancellationToken);

    public Task SendDeletionConfirmationAsync(string playerId, CancellationToken cancellationToken = default) =>
        SendAsync(playerId, DeletionTemplateKey, "account-deletion", cancellationToken);

    private Task SendAsync(string playerId, string templateKey, string reason, CancellationToken cancellationToken)
    {
        string templateId = configuration[templateKey] ?? throw new InvalidOperationException($"A configuração {templateKey} não foi definida na Function App.");
        return playFab.CallAsync("SendEmailFromTemplate", new
        {
            PlayFabId = playerId,
            EmailTemplateId = templateId,
            // Tags are audit metadata only; no sensitive value is sent to a template.
            CustomTags = new Dictionary<string, string> { ["capivarada_reason"] = reason }
        }, cancellationToken);
    }
}
