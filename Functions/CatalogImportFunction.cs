using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

/// <summary>One-time/administrative importer for the JSON exported by Unity. Never callable by a game client.</summary>
public sealed class CatalogImportFunction
{
    private readonly PlayFabTitleEntityClient catalog;
    private readonly IConfiguration configuration;
    public CatalogImportFunction(PlayFabTitleEntityClient catalog, IConfiguration configuration) { this.catalog = catalog; this.configuration = configuration; }
    [Function("ImportEconomyV2Catalog")]
    public async Task<HttpResponseData> Import([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tools/catalog/import")] HttpRequestData request)
    {
        if (!request.Headers.TryGetValues("X-Capivarada-Import-Secret", out IEnumerable<string>? header) || header.FirstOrDefault() != configuration["CAPIVARADA_CATALOG_IMPORT_SECRET"])
            return await Reply(request, HttpStatusCode.Unauthorized, new { error = "Importação não autorizada." });
        try
        {
            ImportContract? data = await JsonSerializer.DeserializeAsync<ImportContract>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (data == null) return await Reply(request, HttpStatusCode.BadRequest, new { error = "JSON de contrato inválido." });

            int created = 0;
            // Content Types are disabled for this title. Tags are native to Economy V2 and give
            // us the same stable classification without any manual dashboard configuration.
            foreach (Skin skin in data.Skins) { await Create(new { Id = skin.Id, Type = "catalogItem", Title = new Dictionary<string,string> { ["NEUTRAL"] = skin.DisplayName }, Tags = new[] { "capivarada", "capivarada-skin" }, DisplayProperties = new { kind = "skin", rarity = skin.Rarity } }); created++; }
            string currencyId = configuration["CAPIVARADA_CURRENCY_ID"] ?? "PA";
            foreach (Box box in data.Boxes) { await Create(new { Id = box.Id, Type = "catalogItem", Title = new Dictionary<string,string> { ["NEUTRAL"] = box.DisplayName }, Tags = new[] { "capivarada", "capivarada-box" }, PriceOptions = new { Prices = new[] { new { Amounts = new[] { new { ItemId = currencyId, Amount = box.PricePA } } } } }, DisplayProperties = new { kind = "skin_box", pricePA = box.PricePA, duplicatePoints = box.DuplicatePoints, drops = box.Drops } }); created++; }
            return await Reply(request, HttpStatusCode.OK, new { created });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CatalogImport] {ex}");
            return await Reply(request, HttpStatusCode.InternalServerError, new
            {
                error = "Falha na importação.",
                detail = ex.ToString()
            });
        }
    }
    private Task<JsonElement> Create(object item) => catalog.CallAsync("Catalog", "CreateDraftItem", new { Item = item, Publish = true });
    private static async Task<HttpResponseData> Reply(HttpRequestData request, HttpStatusCode status, object body)
    {
        HttpResponseData response = request.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return response;
    }
    private sealed class ImportContract { public List<Skin> Skins { get; set; } = new(); public List<Box> Boxes { get; set; } = new(); }
    private sealed class Skin { public string Id { get; set; } = string.Empty; public string DisplayName { get; set; } = string.Empty; public string Rarity { get; set; } = string.Empty; }
    private sealed class Box { public string Id { get; set; } = string.Empty; public string DisplayName { get; set; } = string.Empty; public int PricePA { get; set; } public int DuplicatePoints { get; set; } public List<Drop> Drops { get; set; } = new(); }
    private sealed class Drop { public string SkinId { get; set; } = string.Empty; public string Rarity { get; set; } = string.Empty; public int Weight { get; set; } }
}
