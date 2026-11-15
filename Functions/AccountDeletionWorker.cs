using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

/// <summary>Executes confirmed deletions after their grace period.</summary>
public sealed class AccountDeletionWorker
{
    private readonly AccountDeletionStore deletions;
    private readonly PlayFabAdminClient admin;
    private readonly ILogger<AccountDeletionWorker> logger;

    public AccountDeletionWorker(AccountDeletionStore deletions, PlayFabAdminClient admin, ILogger<AccountDeletionWorker> logger)
    {
        this.deletions = deletions;
        this.admin = admin;
        this.logger = logger;
    }

    [Function("ProcessScheduledAccountDeletions")]
    public async Task Run([TimerTrigger("0 0 * * * *")] TimerInfo _, CancellationToken cancellationToken)
    {
        foreach (AccountDeletionStore.DeletionRecord record in await deletions.FindDueAsync(DateTimeOffset.UtcNow, cancellationToken))
        {
            try
            {
                await admin.DeletePlayerAsync(record.PlayerId, cancellationToken);
                await deletions.MarkDeletedAsync(record.PlayerId, cancellationToken);
                logger.LogInformation("Conta de título excluída após prazo. PlayerId={PlayerId}", record.PlayerId);
            }
            catch (Exception exception)
            {
                // Leave the record scheduled: the next hourly run retries an
                // idempotent deletion rather than silently losing the request.
                logger.LogError(exception, "Não foi possível excluir a conta agendada. PlayerId={PlayerId}", record.PlayerId);
            }
        }
    }
}
