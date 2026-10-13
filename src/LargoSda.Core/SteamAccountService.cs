using System.Security.Cryptography;

namespace LargoSda.Core;

/// <summary>Keeps an account's Steam session alive and wraps confirmation calls with one automatic refresh.</summary>
public sealed class SteamAccountService(HttpClient http, SteamTime time)
{
    private readonly SteamAuth _auth = new(http);
    private readonly ConfirmationClient _confirmations = new(http);

    public static bool HasSession(MaFile account) =>
        !string.IsNullOrEmpty(account.Session?.RefreshToken) || !string.IsNullOrEmpty(account.Session?.AccessToken);

    public async Task LoginAsync(MaFile account, string password, CancellationToken ct = default)
    {
        var tokens = await _auth.LoginAsync(account.AccountName, password, account, time, ct);
        Store(account, tokens);
    }

    public Task<IReadOnlyList<Confirmation>> GetConfirmationsAsync(MaFile account, CancellationToken ct = default) =>
        WithSessionAsync(account, () => _confirmations.GetAsync(account, time.Now, ct), ct);

    public Task<bool> AcceptAsync(MaFile account, IReadOnlyCollection<Confirmation> items, CancellationToken ct = default) =>
        WithSessionAsync(account, () => _confirmations.AcceptAsync(account, items, time.Now, ct), ct);

    public Task<bool> DenyAsync(MaFile account, IReadOnlyCollection<Confirmation> items, CancellationToken ct = default) =>
        WithSessionAsync(account, () => _confirmations.DenyAsync(account, items, time.Now, ct), ct);

    private async Task<T> WithSessionAsync<T>(MaFile account, Func<Task<T>> call, CancellationToken ct)
    {
        if (!HasSession(account)) throw new SessionExpiredException();

        var expiry = SteamAuth.GetTokenExpiry(account.Session!.AccessToken);
        if (expiry < time.Now + 60)
            await RefreshAsync(account, ct);

        try
        {
            return await call();
        }
        catch (SessionExpiredException)
        {
            await RefreshAsync(account, ct);
            return await call();
        }
    }

    private async Task RefreshAsync(MaFile account, CancellationToken ct)
    {
        var session = account.Session;
        if (string.IsNullOrEmpty(session?.RefreshToken)) throw new SessionExpiredException();
        try
        {
            Store(account, await _auth.RefreshAsync(account.SteamId, account.AccountName, session.RefreshToken, ct));
        }
        catch (SteamApiException)
        {
            throw new SessionExpiredException();
        }
    }

    private static void Store(MaFile account, SteamTokens tokens)
    {
        account.Session ??= new MaFileSession();
        if (tokens.SteamId != 0) account.Session.SteamId = tokens.SteamId;
        account.Session.AccessToken = tokens.AccessToken;
        account.Session.RefreshToken = tokens.RefreshToken;
        if (string.IsNullOrEmpty(account.Session.SessionId))
            account.Session.SessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        MaFileStore.Save(account);
    }
}
