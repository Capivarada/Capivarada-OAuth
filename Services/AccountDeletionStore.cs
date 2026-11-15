using Azure;
using Azure.Data.Tables;

/// <summary>Durable deletion schedule and login block for a title player.</summary>
public sealed class AccountDeletionStore
{
    private const string TableName = "CapivaradaAccountDeletion";
    private const string Partition = "deletion";
    private readonly TableClient table;

    public AccountDeletionStore(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        string connection = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage não foi configurado na Function App.");
        table = new TableClient(connection, TableName);
    }

    public async Task<DeletionRecord?> GetAsync(string playerId, CancellationToken token = default)
    {
        try
        {
            await table.CreateIfNotExistsAsync(token);
            TableEntity value = (await table.GetEntityAsync<TableEntity>(Partition, playerId, cancellationToken: token)).Value;
            return ToRecord(value);
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }

    public async Task<DeletionRecord> ScheduleAsync(string playerId, DateTimeOffset executeAfterUtc, CancellationToken token = default)
    {
        await table.CreateIfNotExistsAsync(token);
        DeletionRecord record = new(playerId, executeAfterUtc, "scheduled");
        await table.UpsertEntityAsync(new TableEntity(Partition, playerId)
        {
            ["ExecuteAfterUtc"] = executeAfterUtc,
            ["State"] = record.State,
            ["RequestedUtc"] = DateTimeOffset.UtcNow
        }, TableUpdateMode.Replace, token);
        return record;
    }

    public async Task<IReadOnlyList<DeletionRecord>> FindDueAsync(DateTimeOffset nowUtc, CancellationToken token = default)
    {
        await table.CreateIfNotExistsAsync(token);
        List<DeletionRecord> result = new();
        await foreach (TableEntity entity in table.QueryAsync<TableEntity>(filter: "PartitionKey eq 'deletion'", cancellationToken: token))
        {
            DeletionRecord? record = ToRecord(entity);
            if (record is { State: "scheduled" } && record.ExecuteAfterUtc <= nowUtc)
                result.Add(record);
        }
        return result;
    }

    public async Task MarkDeletedAsync(string playerId, CancellationToken token = default)
    {
        await table.UpdateEntityAsync(new TableEntity(Partition, playerId)
        {
            ["State"] = "deleted",
            ["DeletedUtc"] = DateTimeOffset.UtcNow
        }, ETag.All, TableUpdateMode.Merge, token);
    }

    private static DeletionRecord? ToRecord(TableEntity entity)
    {
        DateTimeOffset? executeAfter = entity.GetDateTimeOffset("ExecuteAfterUtc");
        if (!executeAfter.HasValue) return null;
        return new DeletionRecord(entity.RowKey, executeAfter.Value, entity.GetString("State") ?? "scheduled");
    }

    public sealed record DeletionRecord(string PlayerId, DateTimeOffset ExecuteAfterUtc, string State)
    {
        public bool BlocksLogin => State is "scheduled" or "deleting" or "deleted";
    }
}
