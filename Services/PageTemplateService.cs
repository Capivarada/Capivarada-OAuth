using System.Net;

public sealed class PageTemplateService
{
    private readonly string pagesDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot", "pages");

    public async Task<string> RenderAsync(string pageName, IReadOnlyDictionary<string, string>? replacements = null)
    {
        string path = Path.Combine(pagesDirectory, pageName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Página HTML não publicada.", path);
        }

        string html = await File.ReadAllTextAsync(path);
        if (replacements is null)
        {
            return html;
        }

        foreach ((string key, string value) in replacements)
        {
            html = html.Replace("{{" + key + "}}", WebUtility.HtmlEncode(value), StringComparison.Ordinal);
        }

        return html;
    }
}
