using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;

/// <summary>Idempotency ledger: a player is rewarded at most once for one match.</summary>
public sealed class MatchSettlementReceiptService
{
    private const string TableName = "CapivaradaMatchSettlement";
    private readonly TableClient table;

    public MatchSettlementReceiptService(IConfiguration configuration)
    {
        string connection = configuration["AzureWebJobsStorage"] ?? throw new InvalidOperationException("AzureWebJobsStorage não foi configurado na Function App.");
        table = new TableClient(connection, TableName);
    }

    public async Task<bool> TryStartAsync(string matchId, string entityId, CancellationToken token = default)
    {
        await table.CreateIfNotExistsAsync(token);
        try
        {
            await table.AddEntityAsync(new TableEntity(matchId, entityId) { ["State"] = "processing", ["StartedUtc"] = DateTime.UtcNow }, token);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status == 409) { return false; }
    }

    public Task CompleteAsync(string matchId, string entityId, CancellationToken token = default) =>
        table.UpdateEntityAsync(new TableEntity(matchId, entityId) { ["State"] = "complete", ["CompletedUtc"] = DateTime.UtcNow }, ETag.All, TableUpdateMode.Merge, token);

    public async Task ReleaseAsync(string matchId, string entityId, CancellationToken token = default)
    {
        try { await table.DeleteEntityAsync(matchId, entityId, ETag.All, token); }
        catch (RequestFailedException exception) when (exception.Status == 404) { }
    }
}
