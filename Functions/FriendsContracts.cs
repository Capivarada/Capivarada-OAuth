public sealed class FriendRequestInput
{
    public string TargetPlayerId { get; set; } = string.Empty;
    public string NicknameOrId { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
}

public sealed class FriendRequestActionInput
{
    public string RequestId { get; set; } = string.Empty;
}

public class FriendSummary
{
    public string PlayerId { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string AvatarId { get; set; } = "avatar1";
    public int Level { get; set; } = 1;
    public string Status { get; set; } = "Offline";
}

public sealed class FriendRequestSummary : FriendSummary
{
    public string RequestId { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}
