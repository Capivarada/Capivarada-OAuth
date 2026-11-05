using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

/// <summary>Server-side box resolver for Economy v2. Every value comes from the published catalog item's DisplayProperties.</summary>
public sealed class EconomyFunction : AuthenticatedFunction
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly PlayFabEntityClient economy;
    public EconomyFunction(PlayerSessionService sessions, PlayFabEntityClient economy) : base(sessions) => this.economy = economy;

    [Function("OpenEconomyV2Box")]
    public async Task<HttpResponseData> OpenBox([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "economy/boxes/open")] HttpRequestData request)
    {
        var (_, error) = await RequirePlayerAsync(request); if (error != null) return error;
        BoxOpenInput? input = await JsonSerializer.DeserializeAsync<BoxOpenInput>(request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (string.IsNullOrWhiteSpace(input?.ItemId)) return await Bad(request, "Item ID da caixa ausente.");
        if (string.IsNullOrWhiteSpace(input.IdempotencyId)) return await Bad(request, "Idempotency ID ausente.");
        try
        {
            JsonElement catalog = await economy.CallAsync(request, "Catalog", "SearchItems", new { Filter = "id eq '" + input.ItemId.Replace("'", "") + "'", Count = 1 });
            if (!catalog.TryGetProperty("Items", out JsonElement items) || items.GetArrayLength() != 1) return await Bad(request, "A caixa não existe no catálogo V2 publicado.");
            JsonElement item = items[0];
            if (!item.TryGetProperty("DisplayProperties", out JsonElement properties)) return await Bad(request, "A caixa não possui DisplayProperties.");
            if (!properties.TryGetProperty("kind", out JsonElement kind) || kind.GetString() != "skin_box") return await Bad(request, "O item selecionado não é uma caixa de skins.");
            if (!properties.TryGetProperty("pricePA", out JsonElement priceValue) || !priceValue.TryGetInt32(out int price) || price < 0) return await Bad(request, "A caixa não possui pricePA válido.");
            if (!properties.TryGetProperty("drops", out JsonElement drops) || drops.ValueKind != JsonValueKind.Array || drops.GetArrayLength() == 0) return await Bad(request, "A caixa não possui drops válidos.");

            JsonElement selected = PickDrop(drops);
            string skinId = selected.GetProperty("skinId").GetString() ?? throw new InvalidOperationException("Drop sem skinId.");
            string rarity = selected.TryGetProperty("rarity", out JsonElement rarityValue) ? rarityValue.GetString() ?? string.Empty : string.Empty;
            int duplicatePoints = properties.TryGetProperty("duplicatePoints", out JsonElement duplicateValue) && duplicateValue.TryGetInt32(out int points) ? points : 0;
            bool duplicate = await Owns(request, skinId);
            int balance = await PointsBalance(request);
            if (balance < price) return await Bad(request, "Saldo insuficiente.");

            List<object> operations = new()
            {
                new { Subtract = new { Item = new { Id = "PA" }, Amount = price, DeleteEmptyStacks = false } }
            };
            if (!duplicate)
                operations.Add(new { Add = new { Item = new { Id = skinId }, Amount = 1 } });
            else if (duplicatePoints > 0)
                operations.Add(new { Add = new { Item = new { Id = "PA" }, Amount = duplicatePoints } });
            await economy.CallAsync(request, "Inventory", "ExecuteInventoryOperations", new { CollectionId = "default", IdempotencyId = input.IdempotencyId, Operations = operations });
            return await Ok(request, new { itemId = input.ItemId, skinId, rarity, duplicate, duplicatePoints = duplicate ? duplicatePoints : 0, remainingPoints = balance - price + (duplicate ? duplicatePoints : 0) });
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return await Fail(request, "Não foi possível abrir a caixa."); }
    }

    private async Task<bool> Owns(HttpRequestData request, string itemId)
    {
        JsonElement inventory = await economy.CallAsync(request, "Inventory", "GetInventoryItems", new { CollectionId = "default", Filter = "id eq '" + itemId.Replace("'", "") + "'", Count = 1 });
        return inventory.TryGetProperty("Items", out JsonElement items) && items.GetArrayLength() > 0;
    }
    private async Task<int> PointsBalance(HttpRequestData request)
    {
        JsonElement inventory = await economy.CallAsync(request, "Inventory", "GetInventoryItems", new { CollectionId = "default", Filter = "id eq 'PA'", Count = 1 });
        if (!inventory.TryGetProperty("Items", out JsonElement items) || items.GetArrayLength() == 0) return 0;
        return items[0].TryGetProperty("Amount", out JsonElement amount) && amount.TryGetInt32(out int value) ? value : 0;
    }
    private static JsonElement PickDrop(JsonElement drops)
    {
        int total = 0; foreach (JsonElement drop in drops.EnumerateArray()) total += drop.TryGetProperty("weight", out JsonElement weight) && weight.TryGetInt32(out int value) ? Math.Max(0, value) : 0;
        if (total <= 0) throw new InvalidOperationException("Todos os pesos da caixa são inválidos.");
        int roll = RandomNumberGenerator.GetInt32(total); int cursor = 0;
        foreach (JsonElement drop in drops.EnumerateArray()) { cursor += drop.GetProperty("weight").GetInt32(); if (roll < cursor) return drop; }
        throw new InvalidOperationException("Não foi possível sortear a caixa.");
    }
    private static async Task<HttpResponseData> Ok(HttpRequestData request, object value) { HttpResponseData response = request.CreateResponse(HttpStatusCode.OK); response.Headers.Add("Content-Type", "application/json; charset=utf-8"); await response.WriteStringAsync(JsonSerializer.Serialize(value, Json)); return response; }
    private static async Task<HttpResponseData> Bad(HttpRequestData request, string message) { HttpResponseData response = request.CreateResponse(HttpStatusCode.BadRequest); await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, Json)); return response; }
    private static async Task<HttpResponseData> Fail(HttpRequestData request, string message) { HttpResponseData response = request.CreateResponse(HttpStatusCode.InternalServerError); await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, Json)); return response; }
    private sealed class BoxOpenInput { public string ItemId { get; set; } = string.Empty; public string IdempotencyId { get; set; } = string.Empty; }
}
