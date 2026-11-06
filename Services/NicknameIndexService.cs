using System.Globalization;
using System.Text;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Server-side nickname index used solely to resolve social requests. It lives in the
/// Function App storage account, never in the Unity client, and is case/accent insensitive.
/// </summary>
public sealed class NicknameIndexService
{
    private const string TableName = "CapivaradaNicknameIndex";
    private const string ByNickname = "nickname";
    private const string ByPlayer = "player";
    private readonly TableClient table;

    public NicknameIndexService(IConfiguration configuration)
    {
        string connection = configuration["AzureWebJobsStorage"]
            ?? throw new InvalidOperationException("AzureWebJobsStorage não foi configurado na Function App.");
        table = new TableClient(connection, TableName);
    }

    public async Task<bool> RegisterAsync(string playerId, string nickname, CancellationToken cancellationToken = default)
    {
        string key = Normalize(nickname);
        if (string.IsNullOrWhiteSpace(playerId) || string.IsNullOrWhiteSpace(key)) return false;
        await table.CreateIfNotExistsAsync(cancellationToken);

        TableEntity? previous = await GetAsync(ByPlayer, playerId, cancellationToken);
        if (previous != null && previous.TryGetValue("NicknameKey", out object? previousKeyValue) && previousKeyValue is string previousKey && previousKey != key)
        {
            TableEntity? previousNickname = await GetAsync(ByNickname, previousKey, cancellationToken);
            if (previousNickname != null && previousNickname.TryGetValue("PlayerId", out object? previousOwner) && string.Equals(previousOwner?.ToString(), playerId, StringComparison.Ordinal))
                await table.DeleteEntityAsync(ByNickname, previousKey, ETag.All, cancellationToken);
        }

        TableEntity? claimed = await GetAsync(ByNickname, key, cancellationToken);
        if (claimed != null && claimed.TryGetValue("PlayerId", out object? owner) && !string.Equals(owner?.ToString(), playerId, StringComparison.Ordinal)) return false;

        await table.UpsertEntityAsync(new TableEntity(ByNickname, key) { ["PlayerId"] = playerId, ["Nickname"] = nickname.Trim() }, TableUpdateMode.Replace, cancellationToken);
        await table.UpsertEntityAsync(new TableEntity(ByPlayer, playerId) { ["NicknameKey"] = key, ["Nickname"] = nickname.Trim() }, TableUpdateMode.Replace, cancellationToken);
        return true;
    }

    public async Task<string?> ResolveAsync(string nickname, CancellationToken cancellationToken = default)
    {
        string key = Normalize(nickname);
        if (string.IsNullOrWhiteSpace(key)) return null;
        await table.CreateIfNotExistsAsync(cancellationToken);
        TableEntity? entity = await GetAsync(ByNickname, key, cancellationToken);
        return entity != null && entity.TryGetValue("PlayerId", out object? playerId) ? playerId?.ToString() : null;
    }

    private async Task<TableEntity?> GetAsync(string partition, string row, CancellationToken cancellationToken)
    {
        try { return (await table.GetEntityAsync<TableEntity>(partition, row, cancellationToken: cancellationToken)).Value; }
        catch (RequestFailedException exception) when (exception.Status == 404) { return null; }
    }

    private static string Normalize(string value)
    {
        StringBuilder result = new();
        foreach (char character in value.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }
}
