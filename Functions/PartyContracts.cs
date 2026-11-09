using System;

public sealed class PartyInviteInput { public string PlayerId { get; set; } = string.Empty; }
public sealed class PartyInviteActionInput { public string InviteId { get; set; } = string.Empty; }
public sealed class PartyReadyInput { public bool Ready { get; set; } }
public sealed class PartyKickInput { public string PlayerId { get; set; } = string.Empty; }

public sealed class PartyMemberSummary
{
    public string PlayerId { get; set; } = string.Empty;
    public string Nickname { get; set; } = "Capivara";
    public string AvatarId { get; set; } = "avatar1";
    public int Level { get; set; } = 1;
    public bool Ready { get; set; }
    public bool IsLeader { get; set; }
}

public sealed class PartySummary
{
    public string PartyId { get; set; } = string.Empty;
    public string LeaderPlayerId { get; set; } = string.Empty;
    public int MaxMembers { get; set; } = PartyStore.MaxMembers;
    public PartyMemberSummary[] Members { get; set; } = Array.Empty<PartyMemberSummary>();
}

public sealed class PartyInviteSummary
{
    public string InviteId { get; set; } = string.Empty;
    public string PartyId { get; set; } = string.Empty;
    public string SenderPlayerId { get; set; } = string.Empty;
    public string TargetPlayerId { get; set; } = string.Empty;
    public string Nickname { get; set; } = "Capivara";
    public string AvatarId { get; set; } = "avatar1";
    public int Level { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; }
}
