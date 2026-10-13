using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LargoSda.Core;

/// <summary>How Steam delivers the activation code for a new authenticator.</summary>
public enum ActivationChannel { Sms = 1, Email = 3 }

public sealed record PendingAuthenticator(MaFile MaFile, ActivationChannel Channel, string? PhoneHint);

/// <summary>Links a new mobile authenticator (ITwoFactorService/AddAuthenticator + FinalizeAddAuthenticator).</summary>
public sealed class AuthenticatorLinker(HttpClient http, SteamTime time)
{
    private const string Iface = "ITwoFactorService";
    private const int StatusBadActivationCode = 89;
    private const int StatusBadGuardCode = 88;
    private const int StatusPhoneRequired = 2;

    /// <summary>
    /// Creates the authenticator and immediately saves the maFile into <paramref name="folder"/>:
    /// from this moment the secrets are live, losing them would lock the account.
    /// </summary>
    public async Task<PendingAuthenticator> AddAsync(SteamTokens tokens, string folder, CancellationToken ct = default)
    {
        var deviceId = "android:" + Guid.NewGuid().ToString();
        var response = await SteamWebApi.CallAsync(http, Iface, "AddAuthenticator", HttpMethod.Post, new ProtoWriter()
            .Fixed64(1, tokens.SteamId)
            .UInt64(2, (ulong)time.Now)
            .Int32(4, 1) // authenticator_type: mobile
            .String(5, deviceId)
            .String(6, "1")
            .Int32(8, 2), ct, tokens.AccessToken);

        var status = response.Int32(10, "status") ?? 0;
        var sharedSecret = response.Bytes(1, "shared_secret");
        if (status == StatusPhoneRequired)
            throw new SteamApiException("Steam требует привязать номер телефона к аккаунту, затем повторите.");
        if (sharedSecret is null || status != 1)
            throw new SteamApiException($"Steam не создал аутентификатор (status {status}).");

        var json = new JsonObject
        {
            ["shared_secret"] = Convert.ToBase64String(sharedSecret),
            ["serial_number"] = (response.UInt64(2, "serial_number") ?? 0).ToString(),
            ["revocation_code"] = response.String(3, "revocation_code"),
            ["uri"] = response.String(4, "uri"),
            ["server_time"] = (long)(response.UInt64(5, "server_time") ?? 0),
            ["account_name"] = response.String(6, "account_name") is { Length: > 0 } name ? name : tokens.AccountName,
            ["token_gid"] = response.String(7, "token_gid"),
            ["identity_secret"] = Convert.ToBase64String(response.Bytes(8, "identity_secret") ?? []),
            ["secret_1"] = Convert.ToBase64String(response.Bytes(9, "secret_1") ?? []),
            ["status"] = status,
            ["device_id"] = deviceId,
            ["fully_enrolled"] = false,
            ["phone_number_hint"] = response.String(11, "phone_number_hint"),
            ["confirm_type"] = response.Int32(12, "confirm_type") ?? 1,
            ["Session"] = new JsonObject
            {
                ["SteamID"] = tokens.SteamId,
                ["AccessToken"] = tokens.AccessToken,
                ["RefreshToken"] = tokens.RefreshToken,
                ["SessionID"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
            },
        };

        var maFile = MaFile.Parse(json.ToJsonString());
        Directory.CreateDirectory(folder);
        maFile.FilePath = UniquePath(folder, tokens.SteamId.ToString());
        MaFileStore.Save(maFile);

        var channel = (json["confirm_type"]!.GetValue<int>()) == (int)ActivationChannel.Email
            ? ActivationChannel.Email
            : ActivationChannel.Sms;
        return new PendingAuthenticator(maFile, channel, json["phone_number_hint"]?.GetValue<string>());
    }

    /// <summary>Confirms the authenticator with the code from SMS / email. Returns false if that code was wrong.</summary>
    public async Task<bool> FinalizeAsync(PendingAuthenticator pending, string accessToken, string activationCode,
        CancellationToken ct = default)
    {
        var maFile = pending.MaFile;
        var t = time.Now;

        for (var tries = 0; tries <= 30; tries++)
        {
            var response = await SteamWebApi.CallAsync(http, Iface, "FinalizeAddAuthenticator", HttpMethod.Post, new ProtoWriter()
                .Fixed64(1, maFile.SteamId)
                .String(2, maFile.GenerateCode(t))
                .UInt64(3, (ulong)t)
                .String(4, activationCode.Trim())
                .Bool(6, true), ct, accessToken);

            var status = response.Int32(4, "status") ?? 0;
            if (status == StatusBadActivationCode) return false;
            if (status == StatusBadGuardCode && tries < 30) { t += SteamGuard.PeriodSeconds; continue; }
            if (!response.Bool(1, "success"))
                throw new SteamApiException($"Steam не подтвердил аутентификатор (status {status}).");

            if (response.Bool(2, "want_more"))
            {
                t += SteamGuard.PeriodSeconds;
                continue;
            }

            maFile.Extra ??= [];
            maFile.Extra["fully_enrolled"] = JsonSerializer.SerializeToElement(true);
            maFile.Extra["status"] = JsonSerializer.SerializeToElement(1);
            MaFileStore.Save(maFile);
            return true;
        }

        throw new SteamApiException("Не удалось подобрать код Guard — проверьте время на компьютере.");
    }

    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name + ".maFile");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{name} ({i}).maFile");
        return path;
    }
}
