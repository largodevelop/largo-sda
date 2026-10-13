using System.Text.Json;
using System.Text.Json.Serialization;

namespace LargoSda.Core;

/// <summary>
/// An account file in the de-facto "maFile" format used by Steam Desktop Authenticator.
/// Unknown fields are preserved so the file round-trips without data loss.
/// </summary>
public sealed class MaFile
{
    [JsonPropertyName("account_name")] public string AccountName { get; set; } = "";
    [JsonPropertyName("shared_secret")] public string SharedSecret { get; set; } = "";
    [JsonPropertyName("identity_secret")] public string? IdentitySecret { get; set; }
    [JsonPropertyName("revocation_code")] public string? RevocationCode { get; set; }
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("Session")] public MaFileSession? Session { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Path the file was loaded from (not serialised).</summary>
    [JsonIgnore] public string? FilePath { get; set; }

    [JsonIgnore] public ulong SteamId => Session?.SteamId ?? 0;

    public string GenerateCode(long unixTime) => SteamGuard.GenerateCode(SharedSecret, unixTime);

    private static readonly JsonSerializerOptions Options = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static MaFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<MaFile>(json, Options)
                   ?? throw new JsonException("Empty maFile.");
        if (string.IsNullOrWhiteSpace(file.SharedSecret))
            throw new JsonException("maFile has no shared_secret (is it encrypted?).");
        return file;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

public sealed class MaFileSession
{
    [JsonPropertyName("SteamID")] public ulong SteamId { get; set; }

    // Same field names as SDA 1.0.13+, so sessions are interchangeable.
    [JsonPropertyName("AccessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("RefreshToken")] public string? RefreshToken { get; set; }
    [JsonPropertyName("SessionID")] public string? SessionId { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
