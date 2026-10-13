using LargoSda.Core;

namespace LargoSda.App;

public enum LinkStep { Choose, Credentials, EmailCode, Activation, Done }

/// <summary>
/// Add-account wizard: choose (login+password or .maFile) →
/// log in → (email code) → create authenticator, show R-code → activation code → done.
/// </summary>
public sealed class LinkViewModel(HttpClient http, SteamTime time, string folder, Action<LinkViewModel> onClose)
    : ObservableObject
{
    private readonly SteamAuth _auth = new(http);
    private readonly AuthenticatorLinker _linker = new(http, time);

    private LinkStep _step = LinkStep.Choose;
    private string _login = "";
    private string _password = "";
    private bool _rememberPassword = true;
    private string _code = "";
    private string _error = "";
    private bool _busy;
    private string _revocationCode = "";
    private string _activationHint = "";
    private string _guardCode = "";

    private TaskCompletionSource<string>? _emailCode;
    private SteamTokens? _tokens;
    private PendingAuthenticator? _pending;

    public string? CreatedFile => _pending?.MaFile.FilePath;
    public string? LinkedAccountName => _pending?.MaFile.AccountName;

    /// <summary>Shown above the code cells, like Steam's own "Аккаунт: name".</summary>
    public string AccountLabel => LinkedAccountName ?? Login.Trim();

    public bool IsSms => _pending?.Channel == ActivationChannel.Sms;

    public LinkStep Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, value)) return;
            Raise(nameof(IsChoose), nameof(IsCredentials), nameof(IsEmailCode), nameof(IsActivation), nameof(IsDone),
                nameof(CanCancel), nameof(AccountLabel), nameof(IsSms), nameof(Title));
        }
    }

    public bool IsChoose => Step == LinkStep.Choose;
    public bool IsCredentials => Step == LinkStep.Credentials;
    public bool IsEmailCode => Step == LinkStep.EmailCode;
    public bool IsActivation => Step == LinkStep.Activation;
    public bool IsDone => Step == LinkStep.Done;

    public string Title => IsChoose ? "Добавить аккаунт" : "Привязка мобильного Guard";

    /// <summary>"Войти по логину и паролю" on the choice screen.</summary>
    public void ChooseLogin() => Step = LinkStep.Credentials;

    public bool CanCancel => Step != LinkStep.Done;

    public string Login { get => _login; set => Set(ref _login, value); }
    public string Password { get => _password; set => Set(ref _password, value); }
    public bool RememberPassword { get => _rememberPassword; set => Set(ref _rememberPassword, value); }
    public string Code { get => _code; set => Set(ref _code, value); }
    public string RevocationCode { get => _revocationCode; private set => Set(ref _revocationCode, value); }
    public string ActivationHint { get => _activationHint; private set => Set(ref _activationHint, value); }
    public string GuardCode { get => _guardCode; private set => Set(ref _guardCode, value); }

    public string Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => Error.Length > 0;

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) Raise(nameof(NotBusy));
        }
    }

    public bool NotBusy => !Busy;

    public void Tick(long now)
    {
        if (_pending is not null) GuardCode = _pending.MaFile.GenerateCode(now);
    }

#if DEBUG
    /// <summary>Design preview without Steam: LARGO_SDA_PREVIEW_LINK=choose|email|activation|done.</summary>
    internal void Preview(string step)
    {
        Login = "vx3138187";
        RevocationCode = "R76930";
        ActivationHint = "Введите код из письма Steam";
        Step = step switch
        {
            "choose" => LinkStep.Choose,
            "email" => LinkStep.EmailCode,
            "done" => LinkStep.Done,
            _ => LinkStep.Activation,
        };
    }
#endif

    /// <summary>Main button of the current step.</summary>
    public async Task NextAsync()
    {
        if (Busy) return;
        switch (Step)
        {
            case LinkStep.Credentials:
                await StartAsync();
                break;
            case LinkStep.EmailCode:
                if (string.IsNullOrWhiteSpace(Code)) return;
                Busy = true;
                _emailCode?.TrySetResult(Code);
                break;
            case LinkStep.Activation:
                await FinalizeAsync();
                break;
            case LinkStep.Done:
                onClose(this);
                break;
        }
    }

    public void Cancel()
    {
        if (Busy && Step != LinkStep.EmailCode) return;
        _emailCode?.TrySetCanceled();

        // Not activated yet: Guard is not enabled on the account, so the file is useless in the list.
        // Keep it aside (not deleted) in case Steam still finishes the activation.
        if (Step == LinkStep.Activation && _pending?.MaFile.FilePath is { } path && File.Exists(path))
        {
            File.Move(path, path + ".unfinished", overwrite: true);
            _pending = null;
        }
        onClose(this);
    }

    private async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(Login) || string.IsNullOrEmpty(Password)) return;
        Error = "";
        Busy = true;
        try
        {
            _tokens = await _auth.LoginAsync(Login.Trim(), Password, AskCodeAsync);
            _pending = await _linker.AddAsync(_tokens, folder);

            RevocationCode = _pending.MaFile.RevocationCode ?? "";
            ActivationHint = _pending.Channel == ActivationChannel.Email
                ? "Введите код из письма Steam"
                : $"Введите код из SMS на номер {_pending.PhoneHint ?? "аккаунта"}";
            Code = "";
            Tick(time.Now);
            Step = LinkStep.Activation;
        }
        catch (TaskCanceledException) when (_emailCode?.Task.IsCanceled == true)
        {
            // Wizard closed while waiting for the email code.
        }
        catch (Exception ex)
        {
            Step = LinkStep.Credentials;
            Error = MainViewModel.Describe(ex);
        }
        finally
        {
            Busy = false;
        }
    }

    private Task<string> AskCodeAsync(GuardCodeKind kind, string? error)
    {
        if (kind == GuardCodeKind.Device)
            throw new SteamApiException("На аккаунте уже включён мобильный Guard. Сначала удалите его (R-код), затем привяжите заново.");

        _emailCode = new TaskCompletionSource<string>();
        Code = "";
        Error = error ?? "";
        Busy = false;
        Step = LinkStep.EmailCode;
        return _emailCode.Task;
    }

    private async Task FinalizeAsync()
    {
        if (_pending is null || _tokens is null || string.IsNullOrWhiteSpace(Code)) return;
        Error = "";
        Busy = true;
        try
        {
            if (await _linker.FinalizeAsync(_pending, _tokens.AccessToken, Code))
                Step = LinkStep.Done;
            else
            {
                Error = "Код не подошёл. Проверьте и введите ещё раз.";
                Code = "";
            }
        }
        catch (Exception ex)
        {
            Error = MainViewModel.Describe(ex);
        }
        finally
        {
            Busy = false;
        }
    }
}
