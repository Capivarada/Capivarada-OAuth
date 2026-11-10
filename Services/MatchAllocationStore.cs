using Azure;
using Azure.Data.Tables;

/// <summary>
/// Server-owned allow-list for one allocated MPS session.  A join token is
/// issued only to a player recorded here; clients can never nominate arbitrary
/// players for an allocated match.
/// </summary>
public sealed class MatchAllocationStore
{
    private const string TableName = "CapivaradaMatchAllocation";
    private readonly TableClient table;

    public MatchAllocationStore(Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        string connection = configuration["AzureWebJobsStorage"] ?? throw new InvalidOperationException("AzureWebJobsStorage não foi configurado na Function App.");
        table = new TableClient(connection, TableName);
    }

    public async Task AddPlayerAsync(string matchId, string playerId, CancellationToken token = default)
    {
        await table.CreateIfNotExistsAsync(token);
        try
        {
            await table.AddEntityAsync(new TableEntity(matchId, playerId)
            {
                ["CreatedUtc"] = DateTime.UtcNow
            }, token);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            // A retry of the same allocation is safe and must not fail.
        }
    }

    public async Task<bool> ContainsAsync(string matchId, string playerId, CancellationToken token = default)
    {
        try
        {
            await table.CreateIfNotExistsAsync(token);
            Response<TableEntity> entity = await table.GetEntityAsync<TableEntity>(matchId, playerId, cancellationToken: token);
            return entity.Value != null;
        }
        catch (RequestFailedException exception) when (exception.Status == 404) { return false; }
    }
}
