using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LargoSda.Core;

public sealed record SteamTokens(ulong SteamId, string AccountName, string AccessToken, string RefreshToken);

/// <summary>Which code Steam asks for while logging in.</summary>
public enum GuardCodeKind { Email = 2, Device = 3 }

/// <summary>Asks for a Steam Guard code. <c>error</c> is set when the previous code was rejected.</summary>
public delegate Task<string> GuardCodeProvider(GuardCodeKind kind, string? error);

/// <summary>
/// Steam login through IAuthenticationService as a mobile app.
/// Tokens issued this way are accepted by steamcommunity.com/mobileconf and ITwoFactorService.
/// </summary>
public sealed class SteamAuth(HttpClient http)
{
    private const string Iface = "IAuthenticationService";

    // EAuthTokenPlatformType.MobileApp, EAuthSessionGuardType, EOSType.AndroidUnknown
    private const int PlatformMobileApp = 3;
    private const int GuardNone = 1;
    private const int OsAndroidUnknown = -500;
    private const int GamingDeviceTypePhone = 528;
    private const int MaxCodeAttempts = 3;

    /// <summary>Logs in an account that already has this maFile as its authenticator.</summary>
    public Task<SteamTokens> LoginAsync(string accountName, string password, MaFile maFile, SteamTime time,
        CancellationToken ct = default) =>
        LoginAsync(accountName, password, (kind, error) => kind == GuardCodeKind.Device && error is null
            ? Task.FromResult(maFile.GenerateCode(time.Now))
            : throw new SteamApiException(error ?? "Steam просит код с почты — этот maFile не от этого аккаунта."), ct);

    public async Task<SteamTokens> LoginAsync(string accountName, string password, GuardCodeProvider provideCode,
        CancellationToken ct = default)
    {
        // 1. RSA key for the password.
        var rsaKey = await Call(HttpMethod.Get, "GetPasswordRSAPublicKey", new ProtoWriter().String(1, accountName), ct);
        var modulus = rsaKey.String(1, "publickey_mod") ?? throw new SteamApiException("Steam не вернул RSA-ключ.");
        var exponent = rsaKey.String(2, "publickey_exp")!;
        var timestamp = rsaKey.UInt64(3, "timestamp") ?? 0;

        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Convert.FromHexString(modulus),
            Exponent = Convert.FromHexString(exponent.Length % 2 == 1 ? "0" + exponent : exponent),
        });
        var encrypted = Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.Pkcs1));

        // 2. Start the auth session.
        var device = new ProtoWriter()
            .String(1, "Largo SDA")
            .Int32(2, PlatformMobileApp)
            .Int32(3, OsAndroidUnknown)
            .UInt64(4, GamingDeviceTypePhone);
        var begin = await Call(HttpMethod.Post, "BeginAuthSessionViaCredentials", new ProtoWriter()
            .String(1, "Largo SDA")
            .String(2, accountName)
            .String(3, encrypted)
            .UInt64(4, timestamp)
            .Bool(5, true)
            .Int32(6, PlatformMobileApp)
            .Int32(7, 1) // persistent session
            .String(8, "Mobile")
            .Message(9, device), ct);

        var clientId = begin.UInt64(1, "client_id") ?? throw new SteamApiException("Steam не начал сессию входа.");
        var requestId = begin.Bytes(2, "request_id")!;
        var steamId = begin.UInt64(5, "steamid") ?? 0;
        var guardTypes = begin.GuardTypes();

        // 3. Steam Guard.
        GuardCodeKind? kind = guardTypes.Contains((int)GuardCodeKind.Device) ? GuardCodeKind.Device
            : guardTypes.Contains((int)GuardCodeKind.Email) ? GuardCodeKind.Email
            : null;

        if (kind is { } k)
        {
            string? error = null;
            for (var attempt = 1; ; attempt++)
            {
                var code = (await provideCode(k, error)).Trim();
                try
                {
                    await Call(HttpMethod.Post, "UpdateAuthSessionWithSteamGuardCode", new ProtoWriter()
                        .UInt64(1, clientId)
                        .Fixed64(2, steamId)
                        .String(3, code)
                        .Int32(4, (int)k), ct, okResults: [1, 29 /* DuplicateRequest */]);
                    break;
                }
                catch (SteamApiException ex) when (ex.EResult is 65 or 88 && attempt < MaxCodeAttempts)
                {
                    error = "Код не подошёл, проверьте и введите ещё раз.";
                }
            }
        }
        else if (!guardTypes.Contains(GuardNone))
        {
            throw new SteamApiException("Steam просит подтвердить вход способом, который не поддерживается.");
        }

        // 4. Poll until tokens are issued.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var poll = await Call(HttpMethod.Post, "PollAuthSessionStatus", new ProtoWriter()
                .UInt64(1, clientId)
                .Bytes(2, requestId), ct);

            var refresh = poll.String(3, "refresh_token");
            var access = poll.String(4, "access_token");
            if (!string.IsNullOrEmpty(refresh) && !string.IsNullOrEmpty(access))
                return new SteamTokens(steamId, poll.String(6, "account_name") is { Length: > 0 } n ? n : accountName, access, refresh);

            clientId = poll.UInt64(1, "new_client_id") ?? clientId;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new SteamApiException("Steam не выдал токены — попробуйте ещё раз.");
    }

    /// <summary>Gets a fresh access token from a refresh token (no password needed).</summary>
    public async Task<SteamTokens> RefreshAsync(ulong steamId, string accountName, string refreshToken, CancellationToken ct = default)
    {
        var response = await Call(HttpMethod.Post, "GenerateAccessTokenForApp", new ProtoWriter()
            .String(1, refreshToken)
            .Fixed64(2, steamId)
            .Int32(3, 0), ct);

        var access = response.String(1, "access_token");
        if (string.IsNullOrEmpty(access))
            throw new SteamApiException("Сессия истекла — нужно войти заново.");
        var newRefresh = response.String(2, "refresh_token");
        return new SteamTokens(steamId, accountName, access, string.IsNullOrEmpty(newRefresh) ? refreshToken : newRefresh);
    }

    /// <summary>Unix expiry time from a Steam JWT, or 0 if it cannot be read.</summary>
    public static long GetTokenExpiry(string? jwt)
    {
        try
        {
            var parts = jwt?.Split('.');
            if (parts is not { Length: 3 }) return 0;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("exp", out var exp) ? exp.GetInt64() : 0;
        }
        catch
        {
            return 0;
        }
    }

    private Task<ServiceResponse> Call(HttpMethod method, string name, ProtoWriter body, CancellationToken ct,
        int[]? okResults = null) =>
        SteamWebApi.CallAsync(http, Iface, name, method, body, ct, okResults: okResults);
}
