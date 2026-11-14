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
    private const string SessionRowKey = "__session";
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

    public async Task SaveSessionAsync(
        Session session, CancellationToken token = default)
    {
        await table.CreateIfNotExistsAsync(token);
        await table.UpsertEntityAsync(new TableEntity(session.MatchId, SessionRowKey)
        {
            ["Address"] = session.Address,
            ["Port"] = session.Port,
            ["Region"] = session.Region,
            ["ExpiresUtc"] = session.ExpiresUtc,
            ["Joinable"] = true
        }, TableUpdateMode.Replace, token);
    }

    public async Task<Session?> FindForPlayerAsync(
        string playerId, CancellationToken token = default)
    {
        await table.CreateIfNotExistsAsync(token);
        await foreach (TableEntity membership in table.QueryAsync<TableEntity>(
            filter: $"RowKey eq '{EscapeOData(playerId)}'", cancellationToken: token))
        {
            Session? session = await GetSessionAsync(membership.PartitionKey, token);
            if (session != null && session.IsJoinable && session.ExpiresUtc > DateTimeOffset.UtcNow)
                return session;
        }

        return null;
    }

    public async Task<Session?> FindAvailableAsync(
        int maximumPlayers, CancellationToken token = default)
    {
        await table.CreateIfNotExistsAsync(token);
        Session? selected = null;
        int selectedPlayers = maximumPlayers;

        await foreach (TableEntity entity in table.QueryAsync<TableEntity>(
            filter: $"RowKey eq '{SessionRowKey}'", cancellationToken: token))
        {
            Session? session = ToSession(entity);
            if (session == null || !session.IsJoinable || session.ExpiresUtc <= DateTimeOffset.UtcNow)
                continue;

            int players = await CountPlayersAsync(session.MatchId, token);
            if (players < selectedPlayers && players < maximumPlayers)
            {
                selected = session;
                selectedPlayers = players;
            }
        }

        return selected;
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

    public async Task CloseSessionAsync(
        string matchId, CancellationToken token = default)
    {
        try
        {
            await table.DeleteEntityAsync(matchId, SessionRowKey, ETag.All, token);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // The cleanup is intentionally idempotent.
        }
    }

    /// <summary>
    /// Prevents new allocations from joining a match that has entered gameplay.
    /// The dedicated server is the only caller allowed to trigger this state.
    /// </summary>
    public async Task MarkStartedAsync(string matchId, CancellationToken token = default)
    {
        await table.UpdateEntityAsync(new TableEntity(matchId, SessionRowKey)
        {
            ["Joinable"] = false,
            ["StartedUtc"] = DateTime.UtcNow
        }, ETag.All, TableUpdateMode.Merge, token);
    }

    public async Task<Session?> GetSessionAsync(
        string matchId, CancellationToken token)
    {
        try
        {
            Response<TableEntity> entity = await table.GetEntityAsync<TableEntity>(
                matchId, SessionRowKey, cancellationToken: token);
            return ToSession(entity.Value);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return null;
        }
    }

    private async Task<int> CountPlayersAsync(
        string matchId, CancellationToken token)
    {
        int count = 0;
        await foreach (TableEntity entity in table.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{EscapeOData(matchId)}'", cancellationToken: token))
        {
            if (entity.RowKey != SessionRowKey)
                count++;
        }

        return count;
    }

    private static Session? ToSession(TableEntity entity)
    {
        string address = entity.GetString("Address") ?? string.Empty;
        int? port = entity.GetInt32("Port");
        DateTimeOffset? expires = entity.GetDateTimeOffset("ExpiresUtc");
        if (string.IsNullOrWhiteSpace(address) || !port.HasValue ||
            port <= 0 || !expires.HasValue)
            return null;

        return new Session(
            entity.PartitionKey,
            address,
            port.Value,
            entity.GetString("Region") ?? string.Empty,
            expires.Value,
            entity.GetBoolean("Joinable") ?? true);
    }

    private static string EscapeOData(string value) => value.Replace("'", "''");

    public sealed record Session(
        string MatchId, string Address, int Port, string Region,
        DateTimeOffset ExpiresUtc, bool IsJoinable = true);
}
