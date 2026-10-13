using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LargoSda.Core;

public sealed class Confirmation
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("type_name")] public string TypeName { get; set; } = "";
    [JsonPropertyName("creator_id")] public string CreatorId { get; set; } = "";
    [JsonPropertyName("creation_time")] public long CreationTime { get; set; }
    [JsonPropertyName("headline")] public string Headline { get; set; } = "";
    [JsonPropertyName("summary")] public List<string> Summary { get; set; } = [];
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("warn")] public string? Warn { get; set; }

    public ConfirmationKind Kind => Type switch
    {
        2 => ConfirmationKind.Trade,
        3 => ConfirmationKind.MarketListing,
        _ => ConfirmationKind.Other,
    };
}

public enum ConfirmationKind { Trade, MarketListing, Other }

public sealed class SessionExpiredException() : Exception("Сессия Steam истекла.");

/// <summary>steamcommunity.com/mobileconf — the same endpoints the Steam mobile app uses.</summary>
public sealed class ConfirmationClient(HttpClient http)
{
    private const string Base = "https://steamcommunity.com/mobileconf/";

    private const string UserAgent =
        "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/120.0 Mobile Safari/537.36";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<IReadOnlyList<Confirmation>> GetAsync(MaFile account, long time, CancellationToken ct = default)
    {
        using var request = Request(HttpMethod.Get, "getlist?" + Query(account, time, "list"), account);
        using var doc = await SendAsync(request, ct);

        var root = doc.RootElement;
        if (root.TryGetProperty("needauth", out var needAuth) && needAuth.ValueKind == JsonValueKind.True)
            throw new SessionExpiredException();
        if (!root.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException(root.TryGetProperty("message", out var m) ? m.GetString() : "Steam не отдал список подтверждений.");

        return root.TryGetProperty("conf", out var list)
            ? list.Deserialize<List<Confirmation>>(JsonOptions) ?? []
            : [];
    }

    public Task<bool> AcceptAsync(MaFile account, IReadOnlyCollection<Confirmation> items, long time, CancellationToken ct = default) =>
        RespondAsync(account, items, "allow", time, ct);

    public Task<bool> DenyAsync(MaFile account, IReadOnlyCollection<Confirmation> items, long time, CancellationToken ct = default) =>
        RespondAsync(account, items, "cancel", time, ct);

    private async Task<bool> RespondAsync(MaFile account, IReadOnlyCollection<Confirmation> items, string op, long time,
        CancellationToken ct)
    {
        if (items.Count == 0) return true;

        HttpRequestMessage request;
        if (items.Count == 1)
        {
            var c = items.First();
            request = Request(HttpMethod.Get,
                $"ajaxop?op={op}&{Query(account, time, op)}&cid={c.Id}&ck={c.Nonce}", account);
        }
        else
        {
            var form = new List<KeyValuePair<string, string>> { new("op", op) };
            form.AddRange(QueryPairs(account, time, op));
            foreach (var c in items)
            {
                form.Add(new("cid[]", c.Id));
                form.Add(new("ck[]", c.Nonce));
            }
            request = Request(HttpMethod.Post, "multiajaxop", account);
            request.Content = new FormUrlEncodedContent(form);
        }

        using (request)
        using (var doc = await SendAsync(request, ct))
        {
            return doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
    }

    /// <summary>base64(HMAC-SHA1(identity_secret, time_be64 || tag)).</summary>
    public static string GenerateConfirmationHash(string identitySecretBase64, long time, string tag)
    {
        var tagBytes = Encoding.UTF8.GetBytes(tag);
        var message = new byte[8 + Math.Min(tagBytes.Length, 32)];
        for (var i = 7; i >= 0; i--)
        {
            message[i] = (byte)(time & 0xFF);
            time >>= 8;
        }
        Array.Copy(tagBytes, 0, message, 8, message.Length - 8);
        return Convert.ToBase64String(HMACSHA1.HashData(Convert.FromBase64String(identitySecretBase64), message));
    }

    private static IEnumerable<KeyValuePair<string, string>> QueryPairs(MaFile account, long time, string tag)
    {
        if (string.IsNullOrEmpty(account.IdentitySecret))
            throw new InvalidOperationException("В maFile нет identity_secret — подтверждения недоступны.");

        yield return new("p", account.DeviceId ?? "");
        yield return new("a", account.SteamId.ToString());
        yield return new("k", GenerateConfirmationHash(account.IdentitySecret, time, tag));
        yield return new("t", time.ToString());
        yield return new("m", "react");
        yield return new("tag", tag);
    }

    private static string Query(MaFile account, long time, string tag) =>
        string.Join("&", QueryPairs(account, time, tag).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    private static HttpRequestMessage Request(HttpMethod method, string path, MaFile account)
    {
        var session = account.Session ?? throw new SessionExpiredException();
        if (string.IsNullOrEmpty(session.AccessToken)) throw new SessionExpiredException();

        var request = new HttpRequestMessage(method, Base + path);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ",
            $"steamLoginSecure={session.SteamId}%7C%7C{session.AccessToken}",
            $"sessionid={session.SessionId}",
            "mobileClient=android",
            "mobileClientVersion=777777 3.6.4",
            "Steam_Language=russian"));
        return request;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
            || response.RequestMessage?.RequestUri?.AbsolutePath.StartsWith("/login") == true)
            throw new SessionExpiredException();
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Steam answers with an HTML login page when the cookie is no longer valid.
            throw new SessionExpiredException();
        }
    }
}
