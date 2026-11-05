using System;
using System.Collections.Generic;

public sealed class FriendRequestRecord
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string SenderPlayerId { get; set; } = string.Empty;
    public string TargetPlayerId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class FriendRequestState
{
    public List<FriendRequestRecord> Incoming { get; set; } = new();
    public List<FriendRequestRecord> Outgoing { get; set; } = new();
}

public static class FriendRequestStore
{
    public const string DataKey = "CapivaradaFriendRequests";

    public static FriendRequestRecord? FindIncoming(FriendRequestState state, string requestId) =>
        state.Incoming.Find(item => string.Equals(item.RequestId, requestId, StringComparison.Ordinal));

    public static void Remove(FriendRequestState state, string requestId)
    {
        state.Incoming.RemoveAll(item => string.Equals(item.RequestId, requestId, StringComparison.Ordinal));
        state.Outgoing.RemoveAll(item => string.Equals(item.RequestId, requestId, StringComparison.Ordinal));
    }
}
