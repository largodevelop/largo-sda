using System.Text.Json;

namespace LargoSda.Core;

public sealed class SteamApiException(string message, int eResult = 0) : Exception(message)
{
    public int EResult { get; } = eResult;
}

/// <summary>Calls api.steampowered.com service methods with protobuf-encoded input.</summary>
internal static class SteamWebApi
{
    private const string Base = "https://api.steampowered.com/";

    public static async Task<ServiceResponse> CallAsync(HttpClient http, string iface, string method, HttpMethod httpMethod,
        ProtoWriter body, CancellationToken ct, string? accessToken = null, int[]? okResults = null)
    {
        var encoded = Convert.ToBase64String(body.ToArray());
        var url = $"{Base}{iface}/{method}/v1/";
        if (accessToken is not null) url += "?access_token=" + Uri.EscapeDataString(accessToken);

        using var request = httpMethod == HttpMethod.Get
            ? new HttpRequestMessage(httpMethod, url + (url.Contains('?') ? "&" : "?") + "input_protobuf_encoded=" + Uri.EscapeDataString(encoded))
            : new HttpRequestMessage(httpMethod, url)
            {
                Content = new FormUrlEncodedContent([new("input_protobuf_encoded", encoded)]),
            };

        using var response = await http.SendAsync(request, ct);
        var eResult = response.Headers.TryGetValues("x-eresult", out var values) && int.TryParse(values.First(), out var r)
            ? r
            : response.IsSuccessStatusCode ? 1 : 2;

        if (!(okResults ?? [1]).Contains(eResult))
            throw new SteamApiException(DescribeEResult(eResult), eResult);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var isJson = response.Content.Headers.ContentType?.MediaType?.Contains("json") == true;
        return isJson ? ServiceResponse.FromJson(bytes) : ServiceResponse.FromProtobuf(bytes);
    }

    public static string DescribeEResult(int eResult) => eResult switch
    {
        2 => "Steam отклонил запрос.",
        5 => "Неверный логин или пароль.",
        20 => "Steam недоступен, попробуйте позже.",
        29 => "К аккаунту уже привязан мобильный аутентификатор.",
        65 => "Неверный код подтверждения.",
        84 => "Слишком много попыток. Подождите и попробуйте позже.",
        88 => "Steam не принял код Guard — проверьте, что maFile от этого аккаунта.",
        _ => $"Steam вернул ошибку (EResult {eResult}).",
    };
}

/// <summary>A service response that may come back either as protobuf or as JSON.</summary>
internal sealed class ServiceResponse
{
    private ProtoMessage? _proto;
    private JsonElement _json;

    public static ServiceResponse FromProtobuf(byte[] bytes) => new() { _proto = ProtoMessage.Parse(bytes) };

    public static ServiceResponse FromJson(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement.TryGetProperty("response", out var inner) ? inner : doc.RootElement;
        return new ServiceResponse { _json = root.Clone() };
    }

    public string? String(int field, string name) =>
        _proto is not null ? _proto.String(field)
        : _json.TryGetProperty(name, out var v) ? v.ToString() : null;

    public ulong? UInt64(int field, string name) =>
        _proto is not null ? _proto.UInt64(field)
        : _json.TryGetProperty(name, out var v) && ulong.TryParse(v.ToString(), out var n) ? n : null;

    public int? Int32(int field, string name) => UInt64(field, name) is { } n ? unchecked((int)(long)n) : null;

    public bool Bool(int field, string name) =>
        _proto is not null ? _proto.UInt64(field) is > 0
        : _json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public byte[]? Bytes(int field, string name) =>
        _proto is not null ? _proto.Bytes(field)
        : _json.TryGetProperty(name, out var v) ? Convert.FromBase64String(v.GetString()!) : null;

    public HashSet<int> GuardTypes()
    {
        if (_proto is not null)
            return _proto.Messages(4).Select(m => (int)(m.UInt64(1) ?? 0)).ToHashSet();
        return _json.TryGetProperty("allowed_confirmations", out var list)
            ? list.EnumerateArray().Select(e => e.GetProperty("confirmation_type").GetInt32()).ToHashSet()
            : [];
    }
}
