using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace LargoSda.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => ViewModel?.Tick();
        _timer.Start();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    // ---------- Titlebar ----------

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => OnMaximize(sender, e);

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ---------- Navigation & codes ----------

    private void OnShowCodes(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) vm.Page = Page.Codes;
    }

    private void OnShowConfirmations(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) vm.Page = Page.Confirmations;
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Папка с .maFile",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null)
            ViewModel?.SetFolder(path);
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        Directory.CreateDirectory(vm.Folder);
        _ = Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(vm.Folder));
    }

    private void OnResetFolder(object? sender, RoutedEventArgs e) => ViewModel?.SetFolder(null);

    private async void OnImportFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Папка maFiles, аккаунты из которой нужно добавить",
            AllowMultiple = true,
        });
        ViewModel?.Import(LocalPaths(folders));
    }

    private static List<string> LocalPaths(IEnumerable<IStorageItem>? items) =>
        items?.Select(i => i.TryGetLocalPath()).OfType<string>().ToList() ?? [];

    // ---------- Drag & drop: other maFiles folders / .maFile files anywhere on the window ----------

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(DataFormats.Files)) return;
        e.DragEffects = DragDropEffects.Copy;
        if (ViewModel is { } vm) vm.IsDragging = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } vm) vm.IsDragging = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        vm.IsDragging = false;
        vm.Import(LocalPaths(e.Data.GetFiles()));
    }

    private void OnReload(object? sender, RoutedEventArgs e)
    {
        ViewModel?.Reload();
        if (ViewModel is { IsConfirmationsPage: true } vm) _ = vm.LoadConfirmationsAsync();
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.CopyCodeAsync(SetClipboard);
    }

    private Func<string, Task> SetClipboard => text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    private static AccountViewModel? AccountOf(object? sender) => (sender as Control)?.DataContext as AccountViewModel;

    private async void OnCopyLoginPass(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.CopyLoginPasswordAsync(SetClipboard);
    }

    private async void OnCopyLogin(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.CopyLoginAsync(SetClipboard);
    }

    private async void OnCopyPassword(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.CopyPasswordAsync(SetClipboard);
    }

    private void OnSavePassword(object? sender, RoutedEventArgs e) => ViewModel?.SaveNewPassword();

    private void OnCancelPassword(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) vm.EditingPassword = false;
    }

    private void OnNewPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ViewModel?.SaveNewPassword();
        else if (e.Key == Key.Escape) OnCancelPassword(sender, e);
    }

    // ---------- Row context menu ----------

    private async void OnMenuCopyCode(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.CopyCodeAsync(SetClipboard, AccountOf(sender));
    }

    private async void OnMenuCopyLogin(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.CopyLoginAsync(SetClipboard, AccountOf(sender));
    }

    private async void OnMenuCopyPassword(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.CopyPasswordAsync(SetClipboard, AccountOf(sender));
    }

    private async void OnMenuCopyLoginPass(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.CopyLoginPasswordAsync(SetClipboard, AccountOf(sender));
    }

    private void OnMenuEditPassword(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || AccountOf(sender) is not { } account) return;
        vm.Selected = account;
        vm.Page = Page.Codes;
        vm.EditingPassword = true;
    }

    private void OnMenuConfirmations(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || AccountOf(sender) is not { } account) return;
        vm.Selected = account;
        vm.Page = Page.Confirmations;
    }

    // ---------- Link wizard ----------

    private void OnAddAccount(object? sender, RoutedEventArgs e) => ViewModel?.StartLink();

    private void OnChooseLogin(object? sender, RoutedEventArgs e) => ViewModel?.Link?.ChooseLogin();

    private async void OnChooseMaFile(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите .maFile",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Steam Guard (.maFile)") { Patterns = ["*.maFile"] }],
        });
        var paths = LocalPaths(files);
        if (paths.Count == 0) return; // picker closed: stay on the choice screen
        vm.Link?.Cancel();
        vm.Import(paths);
    }

    private async void OnLinkNext(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Link is { } link) await link.NextAsync();
    }

    private void OnLinkCancel(object? sender, RoutedEventArgs e) => ViewModel?.Link?.Cancel();

    private void OnCodeCompleted(object? sender, EventArgs e) => OnLinkNext(sender, new RoutedEventArgs());

    private void OnLinkKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnLinkNext(sender, e);
    }

    private async void OnCopyRevocation(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.Link?.RevocationCode is { Length: > 0 } code) await SetClipboard(code);
    }

    // ---------- Confirmations ----------

    private async void OnRefreshConfirmations(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.LoadConfirmationsAsync();
    }

    private async void OnLogin(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.LoginAsync();
    }

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnLogin(sender, e);
    }

    private async void OnAccept(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && (sender as Control)?.DataContext is ConfirmationViewModel item)
            await vm.RespondAsync(item, accept: true);
    }

    private async void OnDeny(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && (sender as Control)?.DataContext is ConfirmationViewModel item)
            await vm.RespondAsync(item, accept: false);
    }

    private async void OnAcceptAll(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.RespondAllAsync(accept: true);
    }

    private async void OnDenyAll(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm) await vm.RespondAllAsync(accept: false);
    }
}
