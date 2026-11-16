using Azure;
using Azure.Data.Tables;

/// <summary>Short-lived authenticated-player heartbeats used only for MPS capacity decisions.</summary>
public sealed class OnlinePresenceStore
{
    private const string TableName = "CapivaradaOnlinePresence";
    private const string PlayersPartition = "players";
    private const string StatePartition = "state";
    private const string LastOnlineRow = "last-online";
    private readonly TableClient table;

    public OnlinePresenceStore(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        string connection = configuration["AzureWebJobsStorage"] ?? throw new InvalidOperationException("AzureWebJobsStorage não foi configurado na Function App.");
        table = new TableClient(connection, TableName);
    }

    public async Task TouchAsync(string playerId, CancellationToken token = default)
    {
        await table.CreateIfNotExistsAsync(token);
        DateTime now = DateTime.UtcNow;
        await table.UpsertEntityAsync(new TableEntity(PlayersPartition, playerId) { ["LastSeenUtc"] = now }, TableUpdateMode.Merge, token);
        await table.UpsertEntityAsync(new TableEntity(StatePartition, LastOnlineRow) { ["LastOnlineUtc"] = now }, TableUpdateMode.Merge, token);
    }

    public async Task<int> CountRecentAsync(TimeSpan ttl, CancellationToken token = default)
    {
        await table.CreateIfNotExistsAsync(token);
        int count = 0;
        await foreach (TableEntity entity in table.QueryAsync<TableEntity>(filter: $"PartitionKey eq '{PlayersPartition}'", cancellationToken: token))
            if (entity.GetDateTime("LastSeenUtc") is DateTime seen && seen >= DateTime.UtcNow.Subtract(ttl)) count++;
        return count;
    }

    public async Task<DateTime?> LastOnlineUtcAsync(CancellationToken token = default)
    {
        try
        {
            await table.CreateIfNotExistsAsync(token);
            Response<TableEntity> entity = await table.GetEntityAsync<TableEntity>(StatePartition, LastOnlineRow, cancellationToken: token);
            return entity.Value.GetDateTime("LastOnlineUtc");
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }
}
