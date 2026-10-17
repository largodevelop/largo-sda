using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using LargoSda.Core;

namespace LargoSda.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(params string?[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class AccountViewModel(MaFile file, PasswordStore passwords) : ObservableObject
{
    private string _code = "";

    public MaFile File { get; } = file;
    public string Name => File.AccountName;
    public string SteamId => File.SteamId == 0 ? "—" : File.SteamId.ToString();
    public bool HasSession => SteamAccountService.HasSession(File);
    public bool HasPassword => passwords.Has(Name);
    public string? Password => passwords.Get(Name);

    public string Code
    {
        get => _code;
        private set => Set(ref _code, value);
    }

    public void Refresh(long now) => Code = File.GenerateCode(now);

    public void SessionChanged() => Raise(nameof(HasSession));

    public void SetPassword(string? password)
    {
        passwords.Set(Name, password);
        Raise(nameof(HasPassword), nameof(Password));
    }
}

public sealed class ConfirmationViewModel(Confirmation confirmation) : ObservableObject
{
    private bool _isBusy;

    public Confirmation Model { get; } = confirmation;

    public string Kind => Model.Kind switch
    {
        ConfirmationKind.Trade => "Обмен",
        ConfirmationKind.MarketListing => "Маркет",
        _ => string.IsNullOrEmpty(Model.TypeName) ? "Другое" : Model.TypeName,
    };

    public string Headline => string.IsNullOrWhiteSpace(Model.Headline) ? Kind : Model.Headline;
    public string Summary => string.Join("\n", Model.Summary.Where(s => !string.IsNullOrWhiteSpace(s)).Select(WebUtility.HtmlDecode));
    public bool HasSummary => Summary.Length > 0;
    public string Time => DateTimeOffset.FromUnixTimeSeconds(Model.CreationTime).ToLocalTime().ToString("dd.MM HH:mm");

    public bool IsBusy
    {
        get => _isBusy;
        set => Set(ref _isBusy, value);
    }
}

public enum Page { Codes, Confirmations }

/// <summary>What to do right after the password dialog saves.</summary>
public enum AfterPassword { None, CopyPassword, CopyLoginPassword }

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SteamTime _time = new();
    private readonly HttpClient _http;
    private readonly SteamAccountService _steam;
    private readonly List<AccountViewModel> _all = [];
    private PasswordStore _passwords = null!;

    private Page _page = Page.Codes;
    private AccountViewModel? _selected;
    private string _search = "";
    private string _status = "";
    private string _copyHint = "";
    private int _secondsLeft = SteamGuard.PeriodSeconds;
    private bool _timeSynced;
    private LinkViewModel? _link;

    // Confirmations state for the selected account.
    private bool _confBusy;
    private bool _needsLogin;
    private string _password = "";
    private bool _rememberPassword = true;
    private string _confError = "";
    private int _loadVersion;

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];
    public ObservableCollection<ConfirmationViewModel> Confirmations { get; } = [];

    // ---------- Auto-update ----------

    private UpdateInfo? _update;
    private bool _updating;

    public bool UpdateAvailable => _update is not null;

    public string UpdateHint => _update is null ? "" : $"Доступно обновление {_update.Tag}: нажмите, чтобы установить";

    /// <summary>Check GitHub releases every 5 minutes; the arrow in the titlebar appears when a newer version is out.</summary>
    public async Task WatchUpdatesAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                var info = await Updater.CheckAsync();
                if (!_updating)
                {
                    _update = info;
                    Raise(nameof(UpdateAvailable), nameof(UpdateHint));
                }
            }
            catch { /* offline: try again later */ }
            await Task.Delay(TimeSpan.FromMinutes(5));
        }
    }

    /// <summary>Download the new exe and hand over to it; returns true when the app should close now.</summary>
    public async Task<bool> InstallUpdateAsync()
    {
        if (_update is null || _updating) return false;
        _updating = true;
        var info = _update;
        try
        {
            Status = $"Скачиваю {info.Tag}…";
            var progress = new Progress<double>(p => Status = $"Скачиваю {info.Tag}: {(int)(p * 100)}%");
            var path = await Updater.DownloadAsync(info, progress);
            Updater.StageRestart(path);
            return true;
        }
        catch (Exception exception)
        {
            Status = $"Обновление не удалось: {exception.Message}";
            _updating = false;
            return false;
        }
    }

    public string Version { get; } =
        "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0");

    public MainViewModel()
    {
        _http = new HttpClient(new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _steam = new SteamAccountService(_http, _time);

        Directory.CreateDirectory(Folder);
        WatchFolder();
        Reload();
        _ = SyncTimeAsync();
        _ = WatchUpdatesAsync();
    }

    // ---------- Navigation ----------

    public Page Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            Raise(nameof(IsCodesPage), nameof(IsConfirmationsPage));
            if (value == Page.Confirmations) _ = LoadConfirmationsAsync();
        }
    }

    public bool IsCodesPage => Page == Page.Codes;
    public bool IsConfirmationsPage => Page == Page.Confirmations;

    // ---------- Accounts & codes ----------

    public AccountViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(HasSelection));
            Password = "";
            ConfError = "";
            if (IsConfirmationsPage) _ = LoadConfirmationsAsync();
        }
    }

    public bool HasSelection => Selected is not null;

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value)) ApplyFilter();
        }
    }

    public int AccountCount => _all.Count;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Short notice under the code (replaces the countdown text while shown).</summary>
    public string CopyHint
    {
        get => _copyHint;
        private set
        {
            if (Set(ref _copyHint, value)) Raise(nameof(HasCopyHint));
        }
    }

    public bool HasCopyHint => CopyHint.Length > 0;

    private int _hintVersion;

    /// <summary>Shows a notice and always clears it after a moment.</summary>
    private async void ShowHint(string text, int milliseconds = 2000)
    {
        var version = ++_hintVersion;
        CopyHint = text;
        await Task.Delay(milliseconds);
        if (version == _hintVersion) CopyHint = "";
    }

    public int SecondsLeft
    {
        get => _secondsLeft;
        private set
        {
            if (Set(ref _secondsLeft, value)) Raise(nameof(IsExpiring));
        }
    }

    public bool IsExpiring => SecondsLeft <= 5;

    public bool TimeSynced
    {
        get => _timeSynced;
        private set
        {
            if (Set(ref _timeSynced, value)) Raise(nameof(TimeStatus));
        }
    }

    public string TimeStatus => TimeSynced ? "Время Steam" : "Часы ПК";

    public string Folder => _settings.ResolvedFolder;

    public bool IsDefaultFolder => _settings.IsDefaultFolder;

    /// <summary>Short label for the app's own folder, otherwise the full path.</summary>
    public string FolderLabel => IsDefaultFolder ? "своя папка" : Folder;

    /// <summary>Null switches back to the app's own folder.</summary>
    public void SetFolder(string? folder)
    {
        _settings.SetFolder(folder);
        Directory.CreateDirectory(Folder);
        Raise(nameof(Folder), nameof(IsDefaultFolder), nameof(FolderLabel));
        WatchFolder();
        Reload();
    }

    // ---------- Merging other maFiles folders ----------

    private string _notice = "";
    private int _noticeVersion;

    /// <summary>Result of the last import, shown under the folder drop zone.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value)) Raise(nameof(HasNotice));
        }
    }

    public bool HasNotice => Notice.Length > 0;

    private bool _isDragging;

    /// <summary>Something is being dragged over the window — the drop zone lights up.</summary>
    public bool IsDragging
    {
        get => _isDragging;
        set => Set(ref _isDragging, value);
    }

    /// <summary>Copies accounts (and saved passwords) from other folders / .maFile files into ours.</summary>
    public void Import(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;

        MaFileImportResult result;
        var passwords = 0;
        try
        {
            result = MaFileImporter.Import(paths, Folder);
            var store = PasswordStore.Load(Folder);
            foreach (var source in result.SourceFolders)
                passwords += store.MergeFrom(source);
        }
        catch (Exception ex)
        {
            ShowNotice("Не удалось добавить: " + ex.Message);
            return;
        }

        Search = "";
        Page = Page.Codes;
        Reload();

        if (result.Total == 0)
        {
            ShowNotice("Здесь нет .maFile");
            return;
        }
        var parts = new List<string> { $"Добавлено: {result.Added}" };
        if (result.AlreadyPresent > 0) parts.Add($"уже были: {result.AlreadyPresent}");
        if (passwords > 0) parts.Add($"паролей: {passwords}");
        if (result.Errors.Count > 0) parts.Add($"не прочитано: {result.Errors.Count}");
        ShowNotice(string.Join(" · ", parts));
    }

    private async void ShowNotice(string text)
    {
        var version = ++_noticeVersion;
        Notice = text;
        await Task.Delay(6000);
        if (version == _noticeVersion) Notice = "";
    }

    // Picks up .maFile files dropped into the folder (and removed ones) without pressing "Обновить".
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    private void WatchFolder()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _watcher = new FileSystemWatcher(Folder, "*.maFile") { NotifyFilter = NotifyFilters.FileName };
            _watcher.Created += (_, _) => ScheduleReload();
            _watcher.Deleted += (_, _) => ScheduleReload();
            _watcher.Renamed += (_, _) => ScheduleReload();
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Network drive or no access: the "Обновить" button still works.
        }
    }

    private void ScheduleReload()
    {
        _reloadDebounce?.Cancel();
        var cts = _reloadDebounce = new CancellationTokenSource();
        _ = Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Avalonia.Threading.Dispatcher.UIThread.Post(() => Reload());
        }, TaskScheduler.Default);
    }

    public void Reload(string? selectFile = null)
    {
        _passwords = PasswordStore.Load(Folder);
        var result = MaFileStore.LoadDirectory(Folder);
        _all.Clear();
        _all.AddRange(result.Accounts.Select(a => new AccountViewModel(a, _passwords)));
        Raise(nameof(AccountCount));
        ApplyFilter();
        if (selectFile is not null)
            Selected = Accounts.FirstOrDefault(a => string.Equals(a.File.FilePath, selectFile, StringComparison.OrdinalIgnoreCase)) ?? Selected;
        Tick();

        Status = result.Errors.Count == 0
            ? _all.Count == 0 ? "Аккаунтов пока нет — нажмите «+ Аккаунт» или положите .maFile в папку maFiles (внизу слева → Открыть папку)" : ""
            : $"Не прочитано файлов: {result.Errors.Count} — {result.Errors[0]}";
    }

    /// <summary>Called every second by the window timer.</summary>
    public void Tick()
    {
        var now = _time.Now;
        SecondsLeft = SteamGuard.SecondsRemaining(now);
        foreach (var account in _all)
            account.Refresh(now);
        Link?.Tick(now);
    }

    public Task CopyCodeAsync(Func<string, Task> setClipboard, AccountViewModel? account = null) =>
        CopyAsync(setClipboard, (account ?? Selected)?.Code, "Код скопирован");

    public Task CopyLoginAsync(Func<string, Task> setClipboard, AccountViewModel? account = null) =>
        CopyAsync(setClipboard, (account ?? Selected)?.Name, "Логин скопирован");

    /// <summary>Copies login:password; without a saved password asks for it once, then copies.</summary>
    public async Task CopyLoginPasswordAsync(Func<string, Task> setClipboard, AccountViewModel? account = null)
    {
        account ??= Selected;
        if (account is null) return;

        if (account.Password is { } password)
            await CopyAsync(setClipboard, $"{account.Name}:{password}", "log:pass скопирован");
        else
            AskPassword(account, AfterPassword.CopyLoginPassword, setClipboard);
    }

    /// <summary>Copies only the password; without a saved password asks for it once, then copies.</summary>
    public async Task CopyPasswordAsync(Func<string, Task> setClipboard, AccountViewModel? account = null)
    {
        account ??= Selected;
        if (account is null) return;

        if (account.Password is { } password)
            await CopyAsync(setClipboard, password, "Пароль скопирован");
        else
            AskPassword(account, AfterPassword.CopyPassword, setClipboard);
    }

    private async Task CopyAsync(Func<string, Task> setClipboard, string? text, string hint)
    {
        if (string.IsNullOrEmpty(text)) return;
        await setClipboard(text);
        ShowHint(hint);
    }

    // ---------- Password prompt (shown once per account) ----------

    private AccountViewModel? _promptAccount;
    private string _promptPassword = "";
    private AfterPassword _afterPrompt;
    private Func<string, Task>? _promptClipboard;

    /// <summary>Account whose password the dialog asks for; null when the dialog is closed.</summary>
    public AccountViewModel? PromptAccount
    {
        get => _promptAccount;
        private set
        {
            if (Set(ref _promptAccount, value)) Raise(nameof(IsPrompting), nameof(PromptText));
        }
    }

    public bool IsPrompting => PromptAccount is not null;

    public string PromptPassword
    {
        get => _promptPassword;
        set => Set(ref _promptPassword, value);
    }

    public string PromptText => PromptAccount switch
    {
        null => "",
        { HasPassword: true, Password: null } =>
            "Папка maFiles перенесена с другого компьютера: сохранённые там пароли здесь не расшифровать. " +
            "Введите пароль один раз — дальше копирование будет сразу.",
        { Password: null } =>
            "Пароль этого аккаунта ещё не сохранён — например, maFile добавлен из другой папки. " +
            "Введите его один раз — дальше копирование будет сразу.",
        _ => "Новый пароль заменит сохранённый. Оставьте поле пустым, чтобы удалить пароль.",
    };

    /// <summary>Opens the password dialog; <paramref name="after"/> runs once the password is saved.</summary>
    public void AskPassword(AccountViewModel account, AfterPassword after = AfterPassword.None,
        Func<string, Task>? setClipboard = null)
    {
        _afterPrompt = after;
        _promptClipboard = setClipboard;
        PromptPassword = account.Password ?? "";
        PromptAccount = account;
    }

    public async Task SavePromptAsync()
    {
        if (PromptAccount is not { } account) return;
        var password = PromptPassword;
        if (string.IsNullOrEmpty(password) && _afterPrompt != AfterPassword.None) return;

        account.SetPassword(password);
        var after = _afterPrompt;
        var clipboard = _promptClipboard;
        CancelPrompt();

        switch (after)
        {
            case AfterPassword.CopyPassword when clipboard is not null:
                await CopyAsync(clipboard, password, "Пароль сохранён и скопирован");
                break;
            case AfterPassword.CopyLoginPassword when clipboard is not null:
                await CopyAsync(clipboard, $"{account.Name}:{password}", "Пароль сохранён, log:pass скопирован");
                break;
            default:
                ShowHint(string.IsNullOrEmpty(password) ? "Пароль удалён" : "Пароль сохранён");
                break;
        }
    }

    public void CancelPrompt()
    {
        PromptAccount = null;
        PromptPassword = "";
        _afterPrompt = AfterPassword.None;
        _promptClipboard = null;
    }

    private void ApplyFilter()
    {
        var keep = Selected;
        Accounts.Clear();
        foreach (var account in _all.Where(a =>
                     a.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || a.SteamId.Contains(Search)))
            Accounts.Add(account);
        Selected = keep is not null ? Accounts.FirstOrDefault(a => a.File.FilePath == keep.File.FilePath) ?? Accounts.FirstOrDefault()
            : Accounts.FirstOrDefault();
    }

    private async Task SyncTimeAsync()
    {
        try
        {
            await _time.SyncAsync(_http);
            TimeSynced = true;
            Tick();
        }
        catch
        {
            TimeSynced = false;
        }
    }

    // ---------- Linking a new authenticator ----------

    public LinkViewModel? Link
    {
        get => _link;
        private set
        {
            if (Set(ref _link, value)) Raise(nameof(IsLinking));
        }
    }

    public bool IsLinking => Link is not null;

    public void StartLink()
    {
        Link = new LinkViewModel(_http, _time, Folder, OnLinkClosed);
#if DEBUG
        if (Environment.GetEnvironmentVariable("LARGO_SDA_PREVIEW_LINK") is { Length: > 0 } step)
            Link.Preview(step);
#endif
    }

    private void OnLinkClosed(LinkViewModel link)
    {
        Link = null;
        if (link.CreatedFile is not { } path) return;

        if (link.RememberPassword && !string.IsNullOrEmpty(link.Password))
            PasswordStore.Load(Folder).Set(link.LinkedAccountName ?? link.Login, link.Password);
        Search = "";
        Page = Page.Codes;
        Reload(path);
    }

    // ---------- Confirmations ----------

    public bool ConfBusy
    {
        get => _confBusy;
        private set
        {
            if (Set(ref _confBusy, value)) Raise(nameof(ShowEmpty), nameof(CanAct));
        }
    }

    public bool NeedsLogin
    {
        get => _needsLogin;
        private set
        {
            if (Set(ref _needsLogin, value)) Raise(nameof(ShowEmpty), nameof(ShowList), nameof(CanAct));
        }
    }

    public string Password
    {
        get => _password;
        set => Set(ref _password, value);
    }

    public bool RememberPassword
    {
        get => _rememberPassword;
        set => Set(ref _rememberPassword, value);
    }

    public string ConfError
    {
        get => _confError;
        private set
        {
            if (Set(ref _confError, value)) Raise(nameof(HasConfError), nameof(ShowEmpty));
        }
    }

    public bool HasConfError => ConfError.Length > 0;
    public bool ShowList => !NeedsLogin && Confirmations.Count > 0;
    public bool ShowEmpty => !NeedsLogin && !ConfBusy && Confirmations.Count == 0 && !HasConfError;
    public bool CanAct => !NeedsLogin && !ConfBusy && Confirmations.Count > 0;

    public async Task LoadConfirmationsAsync(bool allowAutoLogin = true)
    {
        var account = Selected;
        var version = ++_loadVersion;
        SetConfirmations([]);
        ConfError = "";
        if (account is null) return;

        if (string.IsNullOrEmpty(account.File.IdentitySecret))
        {
            NeedsLogin = false;
            ConfError = "В этом maFile нет identity_secret — подтверждения недоступны.";
            return;
        }

        NeedsLogin = !account.HasSession;
        if (NeedsLogin)
        {
            // A saved password lets us log in without asking.
            if (allowAutoLogin && account.Password is { } saved)
            {
                Password = saved;
                await LoginAsync();
            }
            return;
        }

        ConfBusy = true;
        try
        {
            var items = await _steam.GetConfirmationsAsync(account.File);
            if (version != _loadVersion) return;
            SetConfirmations(items.Select(c => new ConfirmationViewModel(c)));
        }
        catch (SessionExpiredException)
        {
            if (version != _loadVersion) return;
            NeedsLogin = true;
            if (allowAutoLogin && account.Password is { } saved)
            {
                ConfBusy = false;
                Password = saved;
                await LoginAsync();
            }
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) ConfError = Describe(ex);
        }
        finally
        {
            if (version == _loadVersion) ConfBusy = false;
            account.SessionChanged();
        }
    }

    public async Task LoginAsync()
    {
        var account = Selected;
        if (account is null || string.IsNullOrEmpty(Password)) return;

        ConfBusy = true;
        ConfError = "";
        try
        {
            await _steam.LoginAsync(account.File, Password);
            if (RememberPassword) account.SetPassword(Password);
            Password = "";
            account.SessionChanged();
        }
        catch (Exception ex)
        {
            ConfError = Describe(ex);
            return;
        }
        finally
        {
            ConfBusy = false;
        }

        await LoadConfirmationsAsync(allowAutoLogin: false);
    }

    public Task RespondAsync(ConfirmationViewModel item, bool accept) => RespondAsync([item], accept);

    public Task RespondAllAsync(bool accept) => RespondAsync(Confirmations.ToList(), accept);

    private async Task RespondAsync(IReadOnlyList<ConfirmationViewModel> items, bool accept)
    {
        var account = Selected;
        if (account is null || items.Count == 0) return;

        foreach (var item in items) item.IsBusy = true;
        ConfError = "";
        try
        {
            var models = items.Select(i => i.Model).ToList();
            var ok = accept
                ? await _steam.AcceptAsync(account.File, models)
                : await _steam.DenyAsync(account.File, models);
            if (!ok) ConfError = "Steam не принял ответ — обновите список.";
        }
        catch (SessionExpiredException)
        {
            NeedsLogin = true;
        }
        catch (Exception ex)
        {
            ConfError = Describe(ex);
        }

        await LoadConfirmationsAsync();
    }

    private void SetConfirmations(IEnumerable<ConfirmationViewModel> items)
    {
        Confirmations.Clear();
        foreach (var item in items) Confirmations.Add(item);
        Raise(nameof(ShowList), nameof(ShowEmpty), nameof(CanAct));
    }

    internal static string Describe(Exception ex) => ex switch
    {
        HttpRequestException or TaskCanceledException => "Нет связи со Steam. Проверьте интернет.",
        _ => ex.Message,
    };
}
