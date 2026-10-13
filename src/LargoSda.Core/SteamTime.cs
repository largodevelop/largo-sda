using System.Text.Json;

namespace LargoSda.Core;

/// <summary>Keeps a clock offset against Steam servers so codes stay valid on a skewed PC clock.</summary>
public sealed class SteamTime
{
    private const string QueryTimeUrl = "https://api.steampowered.com/ITwoFactorService/QueryTime/v1/";

    private long _offsetSeconds;

    public bool Synced { get; private set; }

    public long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Volatile.Read(ref _offsetSeconds);

    public async Task SyncAsync(HttpClient http, CancellationToken ct = default)
    {
        using var response = await http.PostAsync(QueryTimeUrl, new FormUrlEncodedContent([]), ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var serverTime = doc.RootElement.GetProperty("response").GetProperty("server_time");
        var seconds = serverTime.ValueKind == JsonValueKind.String
            ? long.Parse(serverTime.GetString()!)
            : serverTime.GetInt64();

        Volatile.Write(ref _offsetSeconds, seconds - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Synced = true;
    }
}
