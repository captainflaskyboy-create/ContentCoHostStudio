using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ContentCoHostStudio;

public partial class MainWindow
{
    private readonly AvatarPackLibrary avatarLibrary;
    private readonly PlaybackActivityHub playbackActivity;
    private readonly ISpoutOutput spout;
    private AvatarPack avatarPack;
    private SpriteImages spriteImages = null!;
    private StreamerWindow? desktopAvatar;
    internal StreamerWindow? DesktopAvatar => desktopAvatar;
    private Window? avatarDialog;
    private readonly AvatarAnimator animator = new();
    private readonly DispatcherTimer avatarTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private Task avatarTask = Task.CompletedTask;
    private Task spoutTask = Task.CompletedTask;
    private bool updatingAvatars, avatarBusy, updatingSpout, spoutBusy;

    private void InitializeAvatarControls()
    {
        spriteImages = new(avatarPack);
        AvatarPreview.Images = spriteImages;
        RefreshAvatarList();
        avatarTimer.Tick += (_, _) => UpdateAvatarFrame();
        avatarTimer.Start();
        spout.Changed += () => Dispatcher.UIThread.Post(() => { if (!closing) UpdateAvatarControls(); });
        UpdateAvatarFrame();
        UpdateAvatarControls();
    }
    private void UpdateAvatarFrame()
    {
        if (closing) return;
        var snapshot = animator.Next(avatarPack, playbackActivity.Current, AvatarPoseBox.SelectedItem as string ?? "Normal",
            (int)AvatarSensitivity.Value, AvatarBobBox.IsChecked == true ? 50 : 0, AvatarShakeBox.IsChecked == true ? 25 : 0, Environment.TickCount64);
        AvatarPreview.Snapshot = snapshot; AvatarPreview.InvalidateVisual();
        desktopAvatar?.UpdateSnapshot(snapshot);
        spout.Publish(snapshot);
    }
    private void RefreshAvatarList()
    {
        updatingAvatars = true;
        AvatarPackBox.ItemsSource = avatarLibrary.Packs;
        AvatarPackBox.SelectedItem = avatarLibrary.Packs.First(pack => pack.Id == avatarPack.Id);
        AvatarPoseBox.ItemsSource = avatarPack.Catalog.Sets.Keys.ToArray();
        AvatarPoseBox.SelectedItem = "Normal";
        updatingAvatars = false;
        UpdateAvatarControls();
    }
    private void UseAvatar(AvatarPack pack)
    {
        string pose = AvatarPoseBox.SelectedItem as string ?? "Normal";
        avatarPack = pack; spriteImages.SetPack(pack); animator.Reset();
        desktopAvatar?.PackChanged();
        if (spout is SpoutOutputService output) output.SetPack(pack);
        RefreshAvatarList();
        AvatarPoseBox.SelectedItem = pack.Catalog.Available(pose) ? pose : "Normal";
        UpdateAvatarFrame();
    }
    private void UpdateAvatarControls()
    {
        if (AvatarPackBox is null) return;
        AvatarPackBox.IsEnabled = ImportAvatarButton.IsEnabled = !closing && !avatarBusy;
        RenameAvatarButton.IsEnabled = RemoveAvatarButton.IsEnabled = !closing && !avatarBusy && !avatarPack.Bundled;
        DesktopAvatarButton.IsEnabled = !closing;
        SpoutBox.IsEnabled = !closing && !spoutBusy && spout.Available;
        updatingSpout = true;
        SpoutBox.IsChecked = spout.Running;
        updatingSpout = false;
        SpoutStatus.Text = $"Sender: {spout.SenderName} · {spout.Status}";
    }
    private void AvatarPackChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (updatingAvatars || avatarBusy || spriteImages == null) return;
        if (AvatarPackBox.SelectedItem is AvatarPackInfo info && avatarLibrary.Find(info.Id) is { } pack) UseAvatar(pack);
    }
    private void BeginAvatarOperation(Func<Task> operation)
    {
        if (closing || avatarBusy) return;
        avatarTask = RunAvatarOperationAsync(operation);
    }
    private async Task RunAvatarOperationAsync(Func<Task> operation)
    {
        avatarBusy = true; UpdateAvatarControls();
        try { await operation(); }
        catch (Exception exception)
        {
            if (!closing) AvatarStatus.Text = exception is InvalidDataException or InvalidOperationException ? exception.Message : "Could not update the avatar library. Check the selected folder and write permissions; the previous avatar remains available.";
        }
        finally { avatarBusy = false; if (!closing) UpdateAvatarControls(); }
    }
    private async Task LoadAvatarLibraryAsync()
    {
        await Task.Run(avatarLibrary.LoadInstalled);
        if (!closing) RefreshAvatarList();
    }
    private void ImportAvatarClicked(object? sender, RoutedEventArgs args) => BeginAvatarOperation(async () =>
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Import PNG avatar pack", AllowMultiple = false });
        string? path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path == null || closing) return;
        var inspected = await Task.Run(() => AvatarPackLibrary.InspectFolder(path));
        if (closing) return;
        bool replace = avatarLibrary.Find(inspected.Id) != null;
        if (replace && !await AvatarConfirmAsync("Replace Avatar Pack", $"Replace installed artwork for {inspected.DisplayName}? The source folder is left untouched.")) return;
        if (closing) return;
        var installed = await Task.Run(() => avatarLibrary.Install(inspected, replace));
        if (!closing) { UseAvatar(installed); AvatarStatus.Text = "Avatar imported. PNG frames and an optional ICO were saved; GIF events are ignored."; }
    });
    private void RenameAvatarClicked(object? sender, RoutedEventArgs args) => BeginAvatarOperation(async () =>
    {
        if (avatarPack.Bundled) return;
        string? name = await AvatarNameAsync(avatarPack.DisplayName);
        if (name == null || closing) return;
        var renamed = await Task.Run(() => avatarLibrary.Rename(avatarPack.Id, name));
        if (!closing) UseAvatar(renamed);
    });
    private void RemoveAvatarClicked(object? sender, RoutedEventArgs args) => BeginAvatarOperation(async () =>
    {
        if (avatarPack.Bundled || !await AvatarConfirmAsync("Remove Avatar Pack", $"Remove {avatarPack.DisplayName} from this application's library? The original folder is left untouched.")) return;
        if (closing) return;
        string id = avatarPack.Id;
        await Task.Run(() => avatarLibrary.Remove(id));
        if (!closing) { UseAvatar(avatarLibrary.Starter); AvatarStatus.Text = "Avatar removed from this library."; }
    });
    private async Task<bool> AvatarConfirmAsync(string title, string message)
    {
        var dialog = new Window { Title = title, Width = 430, Height = 190, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var confirm = new Button { Content = "Confirm" }; var cancel = new Button { Content = "Cancel" };
        confirm.Click += (_, _) => dialog.Close(true); cancel.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new(20),
            Spacing = 16,
            Children =
        { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { confirm, cancel } } }
        };
        avatarDialog = dialog;
        try { return await dialog.ShowDialog<bool>(this); }
        finally { if (ReferenceEquals(avatarDialog, dialog)) avatarDialog = null; }
    }
    private async Task<string?> AvatarNameAsync(string name)
    {
        var dialog = new Window { Title = "Rename Avatar", Width = 400, Height = 160, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var text = new TextBox { Text = name, MaxLength = 80 };
        var save = new Button { Content = "Save Name" }; var cancel = new Button { Content = "Cancel" };
        save.Click += (_, _) => dialog.Close(text.Text); cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new(20), Spacing = 12, Children = { text, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { save, cancel } } } };
        avatarDialog = dialog;
        try { return await dialog.ShowDialog<string?>(this); }
        finally { if (ReferenceEquals(avatarDialog, dialog)) avatarDialog = null; }
    }
    private void DesktopAvatarClicked(object? sender, RoutedEventArgs args)
    {
        if (closing) return;
        if (desktopAvatar != null) { desktopAvatar.Activate(); return; }
        var window = new StreamerWindow(spriteImages);
        desktopAvatar = window;
        window.Avatar.Snapshot = AvatarPreview.Snapshot;
        window.Closed += (_, _) => { if (ReferenceEquals(desktopAvatar, window)) desktopAvatar = null; };
        window.Show();
    }
    private void SpoutChanged(object? sender, RoutedEventArgs args)
    {
        if (updatingSpout || spoutBusy || closing) return;
        bool enable = SpoutBox.IsChecked == true;
        spoutTask = ChangeSpoutAsync(enable);
    }
    private async Task ChangeSpoutAsync(bool enable)
    {
        spoutBusy = true;
        UpdateAvatarControls();
        try { if (enable) await spout.StartAsync(closingSource.Token); else await spout.StopAsync(); }
        catch (Exception) { if (!closing) AvatarStatus.Text = "Spout2 could not start or continue. Check the sender status and README setup instructions. Speech and the desktop avatar remain available."; }
        finally { spoutBusy = false; if (!closing) UpdateAvatarControls(); }
    }
}
