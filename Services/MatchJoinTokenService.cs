using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Issues opaque, short-lived proofs that a player joined one specific match.
/// The proof is safe to forward to a dedicated game server because it cannot
/// authenticate any PlayFab or Azure API by itself.
/// </summary>
public sealed class MatchJoinTokenService
{
    private readonly IConfiguration configuration;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public MatchJoinTokenService(IConfiguration configuration) => this.configuration = configuration;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["CAPIVARADA_MATCH_TOKEN_SECRET"]);

    public string Create(string playerId, string matchId, TimeSpan lifetime)
    {
        if (!IsConfigured) throw new InvalidOperationException("CAPIVARADA_MATCH_TOKEN_SECRET não foi configurado.");
        JoinTokenPayload payload = new() { PlayerId = playerId, MatchId = matchId, ExpiresAtUnix = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds() };
        string encoded = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json)));
        return encoded + "." + Sign(encoded);
    }

    public bool TryValidate(string token, string expectedPlayerId, string expectedMatchId)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(token)) return false;
        string[] parts = token.Split('.', 2);
        if (parts.Length != 2 || !FixedEquals(Sign(parts[0]), parts[1])) return false;
        try
        {
            JoinTokenPayload? payload = JsonSerializer.Deserialize<JoinTokenPayload>(Encoding.UTF8.GetString(FromBase64Url(parts[0])), Json);
            return payload != null && payload.ExpiresAtUnix >= DateTimeOffset.UtcNow.ToUnixTimeSeconds() &&
                   string.Equals(payload.PlayerId, expectedPlayerId, StringComparison.Ordinal) &&
                   string.Equals(payload.MatchId, expectedMatchId, StringComparison.Ordinal);
        }
        catch (Exception) { return false; }
    }

    private string Sign(string value)
    {
        using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(configuration["CAPIVARADA_MATCH_TOKEN_SECRET"]!));
        return Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static bool FixedEquals(string left, string right)
    {
        byte[] leftBytes = Encoding.UTF8.GetBytes(left);
        byte[] rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private sealed class JoinTokenPayload
    {
        public string PlayerId { get; set; } = string.Empty;
        public string MatchId { get; set; } = string.Empty;
        public long ExpiresAtUnix { get; set; }
    }
}
