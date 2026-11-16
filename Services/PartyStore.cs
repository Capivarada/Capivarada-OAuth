using System;
using System.Collections.Generic;
using System.Linq;

public sealed class PartyMembership
{
    public string PartyId { get; set; } = string.Empty;
    public string LeaderPlayerId { get; set; } = string.Empty;
}

public sealed class PartyMemberRecord
{
    public string PlayerId { get; set; } = string.Empty;
    /// <summary>Title-player entity ID required by PlayFab Match APIs.</summary>
    public string MatchmakingEntityId { get; set; } = string.Empty;
    public string MatchmakingEntityType { get; set; } = "title_player_account";
    public bool Ready { get; set; }
    public DateTime JoinedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PartyRecord
{
    public string PartyId { get; set; } = Guid.NewGuid().ToString("N");
    public string LeaderPlayerId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<PartyMemberRecord> Members { get; set; } = new();
}

public sealed class PartyInviteRecord
{
    public string InviteId { get; set; } = Guid.NewGuid().ToString("N");
    public string PartyId { get; set; } = string.Empty;
    public string LeaderPlayerId { get; set; } = string.Empty;
    public string SenderPlayerId { get; set; } = string.Empty;
    public string TargetPlayerId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PartyInviteState
{
    public List<PartyInviteRecord> Incoming { get; set; } = new();
    public List<PartyInviteRecord> Outgoing { get; set; } = new();
}

/// <summary>Server-owned state for the single PlayFab ticket created by a party leader.</summary>
public sealed class PartyMatchmakingTicket
{
    public string TicketId { get; set; } = string.Empty;
    public string QueueName { get; set; } = string.Empty;
    public string Stage { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    /// <summary>Server-directed polling backoff. Persisted with the party ticket so every member honors it.</summary>
    public DateTime NextStatusCheckUtc { get; set; }
}

public static class PartyStore
{
    public const int MaxMembers = 8;
    public const string MembershipKey = "CapivaradaPartyMembership";
    public const string PartyKey = "CapivaradaParty";
    public const string InviteKey = "CapivaradaPartyInvites";
    public const string MatchmakingTicketKey = "CapivaradaPartyMatchmakingTicket";

    public static bool IsMember(PartyRecord party, string playerId) => party.Members.Any(member => string.Equals(member.PlayerId, playerId, StringComparison.Ordinal));
    public static PartyMemberRecord? FindMember(PartyRecord party, string playerId) => party.Members.FirstOrDefault(member => string.Equals(member.PlayerId, playerId, StringComparison.Ordinal));
    public static void RemoveInvite(PartyInviteState state, string inviteId)
    {
        state.Incoming.RemoveAll(invite => string.Equals(invite.InviteId, inviteId, StringComparison.Ordinal));
        state.Outgoing.RemoveAll(invite => string.Equals(invite.InviteId, inviteId, StringComparison.Ordinal));
    }
}
