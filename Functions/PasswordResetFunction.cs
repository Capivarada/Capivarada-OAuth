using System.Net;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

public sealed class PasswordResetFunction
{
    private const string HtmlContentType = "text/html; charset=utf-8";

    private readonly ILogger<PasswordResetFunction> logger;
    private readonly PlayFabAdminClient playFab;
    private readonly PageTemplateService pages;

    public PasswordResetFunction(
        ILogger<PasswordResetFunction> logger,
        PlayFabAdminClient playFab,
        PageTemplateService pages)
    {
        this.logger = logger;
        this.playFab = playFab;
        this.pages = pages;
    }

    [Function("PasswordResetCallback")]
    public async Task<HttpResponseData> Callback(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "password/callback")]
        HttpRequestData request)
    {
        string? token = FormData.GetQueryValue(request.Url, "token");

        if (string.IsNullOrWhiteSpace(token))
        {
            return await CreateTextResponse(request, HttpStatusCode.BadRequest, "Token de recuperação não informado.");
        }

        try
        {
            string html = await pages.RenderAsync("reset-password.html", new Dictionary<string, string>
            {
                ["RECOVERY_TOKEN"] = token
            });

            return await CreateHtmlResponse(request, HttpStatusCode.OK, html);
        }
        catch (FileNotFoundException exception)
        {
            logger.LogError(exception, "A página de redefinição não foi publicada.");
            return await CreateTextResponse(request, HttpStatusCode.InternalServerError, "Página de redefinição não encontrada no servidor.");
        }
    }

    [Function("PasswordReset")]
    public async Task<HttpResponseData> Reset(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "password/reset")]
        HttpRequestData request)
    {
        if (!request.Headers.TryGetValues("Content-Type", out IEnumerable<string>? contentTypes) ||
            !contentTypes.Any(value => value.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)))
        {
            return await CreateTextResponse(request, HttpStatusCode.BadRequest, "Formato de requisição inválido.");
        }

        string body;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync();
        }

        IReadOnlyDictionary<string, string> form = FormData.Parse(body);
        form.TryGetValue("token", out string? token);
        form.TryGetValue("password", out string? password);
        form.TryGetValue("confirmPassword", out string? confirmPassword);

        if (string.IsNullOrWhiteSpace(token))
        {
            return await CreateTextResponse(request, HttpStatusCode.BadRequest, "Token de recuperação não informado.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            return await CreateTextResponse(request, HttpStatusCode.BadRequest, "Nova senha não informada.");
        }

        if (password.Length < 6)
        {
            return await CreateTextResponse(request, HttpStatusCode.BadRequest, "A senha deve ter pelo menos 6 caracteres.");
        }

        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            return await CreateTextResponse(request, HttpStatusCode.BadRequest, "As senhas não coincidem.");
        }

        PlayFabResetResult result = await playFab.ResetPasswordAsync(token, password);
        if (result.Succeeded)
        {
            logger.LogInformation("Senha redefinida com sucesso.");
            return await CreateHtmlResponse(request, HttpStatusCode.OK, await pages.RenderAsync("password-reset-success.html"));
        }

        logger.LogWarning("A redefinição de senha foi recusada pelo PlayFab. Status: {StatusCode}", result.StatusCode);
        return await CreateHtmlResponse(request, result.StatusCode, await pages.RenderAsync(
            "password-reset-error.html",
            new Dictionary<string, string>
            {
                ["ERROR_TITLE"] = "Não foi possível redefinir sua senha.",
                ["ERROR_MESSAGE"] = "O link pode ter expirado ou já ter sido utilizado."
            }));
    }

    [Function("Health")]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")]
        HttpRequestData request)
    {
        return await CreateTextResponse(request, HttpStatusCode.OK, "Capivarada Functions online.");
    }

    [Function("PasswordResetStaticFile")]
    public async Task<HttpResponseData> StaticFile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "static/{fileName}")]
        HttpRequestData request,
        string fileName)
    {
        (string relativePath, string contentType)? asset = fileName switch
        {
            "Logo_Capivarada.png" => (Path.Combine("assets", "Logo_Capivarada.png"), "image/png"),
            "reset-password.css" => (Path.Combine("static", "reset-password.css"), "text/css; charset=utf-8"),
            _ => null
        };

        if (asset is null)
        {
            return await CreateTextResponse(request, HttpStatusCode.NotFound, "Arquivo estático não encontrado.");
        }

        string assetPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", asset.Value.relativePath);
        if (!File.Exists(assetPath))
        {
            logger.LogError("O arquivo estático {FileName} não foi publicado.", fileName);
            return await CreateTextResponse(request, HttpStatusCode.InternalServerError, "Arquivo estático não publicado.");
        }

        HttpResponseData response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", asset.Value.contentType);
        response.Headers.Add("Cache-Control", "public, max-age=86400");
        await response.Body.WriteAsync(await File.ReadAllBytesAsync(assetPath));
        return response;
    }

    private static async Task<HttpResponseData> CreateTextResponse(HttpRequestData request, HttpStatusCode statusCode, string content)
    {
        HttpResponseData response = request.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        await response.WriteStringAsync(content);
        return response;
    }

    private static async Task<HttpResponseData> CreateHtmlResponse(HttpRequestData request, HttpStatusCode statusCode, string content)
    {
        HttpResponseData response = request.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", HtmlContentType);
        response.Headers.Add("Referrer-Policy", "no-referrer");
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        await response.WriteStringAsync(content);
        return response;
    }
}
