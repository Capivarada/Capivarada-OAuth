using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

/// <summary>Authenticated account security actions and the e-mail-only deletion confirmation.</summary>
public sealed class AccountSecurityFunction : AuthenticatedFunction
{
    private readonly EmailTemplateService email;
    private readonly PlayFabAdminClient admin;
    private readonly AccountDeletionStore deletions;
    private readonly PageTemplateService pages;
    private readonly int deletionGraceDays;

    public AccountSecurityFunction(PlayerSessionService sessions, EmailTemplateService email, PlayFabAdminClient admin, AccountDeletionStore deletions, PageTemplateService pages, Microsoft.Extensions.Configuration.IConfiguration configuration) : base(sessions)
    {
        this.email = email;
        this.admin = admin;
        this.deletions = deletions;
        this.pages = pages;
        deletionGraceDays = Math.Clamp(configuration.GetValue("ACCOUNT_DELETION_GRACE_DAYS", 7), 1, 30);
    }

    [Function("AccountLoginNotification")]
    public async Task<HttpResponseData> LoginNotification(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "account/login-notification")] HttpRequestData request)
    {
        (PlayerSession? session, HttpResponseData? error) = await RequirePlayerAsync(request);
        if (error != null) return error;

        // During staged deployment the alert is opt-in until its template and
        // SMTP add-on have been configured in PlayFab.
        if (email.LoginAlertConfigured)
            await email.SendLoginAlertAsync(session!.Value.PlayFabId, request.FunctionContext.CancellationToken);
        return await Json(request, HttpStatusCode.Accepted, "{\"accepted\":true}");
    }

    [Function("AccountDeletionRequest")]
    public async Task<HttpResponseData> RequestDeletion(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "account/deletion/request")] HttpRequestData request)
    {
        (PlayerSession? session, HttpResponseData? error) = await RequirePlayerAsync(request);
        if (error != null) return error;
        if (!email.DeletionConfigured)
            return await Json(request, HttpStatusCode.ServiceUnavailable, "{\"error\":\"O e-mail de exclusão ainda não foi configurado.\"}");

        await email.SendDeletionConfirmationAsync(session!.Value.PlayFabId, request.FunctionContext.CancellationToken);
        return await Json(request, HttpStatusCode.Accepted, "{\"accepted\":true}");
    }

    [Function("AccountDeletionConfirmation")]
    public async Task<HttpResponseData> ConfirmDeletion(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "account/deletion/confirm")] HttpRequestData request)
    {
        bool isPost = request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string>? form = isPost
            ? await FormData.ReadAsync(request, request.FunctionContext.CancellationToken)
            : null;
        string? token = isPost
            ? form!.GetValueOrDefault("token")
            : FormData.GetQueryValue(request.Url, "token");
        string? playerId = await admin.ResolveEmailTokenPlayerIdAsync(token ?? string.Empty, request.FunctionContext.CancellationToken);
        if (string.IsNullOrWhiteSpace(playerId))
            return await Html(request, HttpStatusCode.BadRequest, "<h1>Link inválido ou expirado</h1><p>Solicite uma nova exclusão pelo jogo.</p>");

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddDays(deletionGraceDays);
        if (!isPost)
        {
            string page = await pages.RenderAsync("account-deletion-confirm.html", new Dictionary<string, string>
            {
                ["TOKEN"] = token ?? string.Empty,
                ["FORM_ACTION"] = request.Url.AbsolutePath,
                ["DELETION_DAYS"] = deletionGraceDays.ToString(),
                ["DELETION_DATE"] = FormatBrazilDeadline(deadline)
            });
            return await Html(request, HttpStatusCode.OK, page, alreadyDocument: true);
        }

        string password = form!.GetValueOrDefault("password") ?? string.Empty;
        if (!await admin.ValidatePasswordAsync(playerId, password, request.FunctionContext.CancellationToken))
            return await Html(request, HttpStatusCode.Unauthorized, "<h1>Senha inválida</h1><p>Digite a senha correta e tente novamente pelo link recebido.</p>");

        try
        {
            await deletions.ScheduleAsync(playerId, deadline, request.FunctionContext.CancellationToken);
        }
        catch (InvalidOperationException)
        {
            return await Html(request, HttpStatusCode.BadGateway,
                "<h1>Não foi possível excluir a conta</h1><p>O link pode ter expirado ou a exclusão não está disponível agora. Solicite um novo e-mail pelo jogo.</p>");
        }
        return await Html(request, HttpStatusCode.OK, $"<h1>Exclusão agendada</h1><p>O acesso ao Capivarada foi bloqueado. Os dados deste título serão excluídos em {FormatBrazilDeadline(deadline)}.</p>");
    }

    [Function("AccountStatus")]
    public async Task<HttpResponseData> Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "account/status")] HttpRequestData request)
    {
        (PlayerSession? session, HttpResponseData? error) = await RequirePlayerAsync(request);
        if (error != null) return error;
        AccountDeletionStore.DeletionRecord? record = await deletions.GetAsync(session!.Value.PlayFabId, request.FunctionContext.CancellationToken);
        bool blocked = record?.BlocksLogin ?? false;
        return await Json(request, HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new
        {
            blocked,
            executeAfterUtc = record?.ExecuteAfterUtc
        }));
    }

    private static async Task<HttpResponseData> Json(HttpRequestData request, HttpStatusCode status, string content)
    {
        HttpResponseData response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(content);
        return response;
    }

    private static string FormatBrazilDeadline(DateTimeOffset deadline)
    {
        try
        {
            TimeZoneInfo brazil = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
            return TimeZoneInfo.ConvertTime(deadline, brazil).ToString("dd/MM/yyyy 'às' HH:mm 'BRT'");
        }
        catch (TimeZoneNotFoundException)
        {
            return deadline.ToUniversalTime().ToString("dd/MM/yyyy 'às' HH:mm 'UTC'");
        }
    }

    private static async Task<HttpResponseData> Html(HttpRequestData request, HttpStatusCode status, string content, bool alreadyDocument = false)
    {
        HttpResponseData response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");
        response.Headers.Add("Referrer-Policy", "no-referrer");
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        await response.WriteStringAsync(alreadyDocument ? content : $"<!doctype html><html lang=\"pt-BR\"><body>{content}</body></html>");
        return response;
    }
}
