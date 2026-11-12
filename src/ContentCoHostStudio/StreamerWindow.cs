using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace ContentCoHostStudio;

public sealed class StreamerWindow : Window
{
    public AvatarCanvas Avatar { get; }
    internal bool UsesTightHitArea => tightFallback;
    private readonly SpriteImages images;
    private readonly StreamerHitRegion hitRegion = new();
    private bool tightFallback;
    private (PixelPoint Window, PixelPoint Pointer)? drag;
    private readonly Button dismiss;
    private bool automaticPresentation;
    public event EventHandler? DismissRequested;
    internal double ChromeHeight => automaticPresentation ? 32 : 0;
    public bool AutomaticPresentation
    {
        get => automaticPresentation;
        set { if (automaticPresentation == value) return; automaticPresentation = value; dismiss.IsVisible = value; hitRegion.ClearPack(); }
    }
    private void DismissOrClose() { if (automaticPresentation) DismissRequested?.Invoke(this, EventArgs.Empty); else Close(); }
    public StreamerWindow(SpriteImages images)
    {
        this.images = images;
        Title = "Content CoHost Studio — Desktop Avatar";
        Width = Height = 420; MinWidth = MinHeight = 120; MaxWidth = MaxHeight = 1800;
        WindowDecorations = WindowDecorations.None; Topmost = true; CanResize = false;
        ShowActivated = false;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent]; Background = Brushes.Transparent;
        Avatar = new() { Images = images };
        var larger = new MenuItem { Header = "Larger" }; larger.Click += (_, _) => ResizeAvatar(1.2);
        var smaller = new MenuItem { Header = "Smaller" }; smaller.Click += (_, _) => ResizeAvatar(1 / 1.2);
        var reset = new MenuItem { Header = "Reset Size" }; reset.Click += (_, _) => { Width = Height = 420; };
        var top = new MenuItem { Header = "Always on Top", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
        top.Click += (_, _) => Topmost = top.IsChecked;
        var exit = new MenuItem { Header = "Close / Dismiss Desktop Avatar" }; exit.Click += (_, _) => DismissOrClose();
        Avatar.ContextMenu = new ContextMenu { ItemsSource = new[] { larger, smaller, reset, top, exit } };
        // Move in screen pixels so a transparent shaped window can drag without a native title bar.
        Avatar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(Avatar).Properties.IsLeftButtonPressed) return;
            drag = (Position, this.PointToScreen(e.GetPosition(this)));
            e.Pointer.Capture(Avatar); e.Handled = true;
        };
        Avatar.PointerMoved += (_, e) =>
        {
            if (drag is not { } start || !e.GetCurrentPoint(Avatar).Properties.IsLeftButtonPressed) return;
            var pointer = this.PointToScreen(e.GetPosition(this));
            Position = new(start.Window.X + pointer.X - start.Pointer.X, start.Window.Y + pointer.Y - start.Pointer.Y);
            e.Handled = true;
        };
        Avatar.PointerReleased += (_, e) => { drag = null; e.Pointer.Capture(null); };
        Avatar.PointerCaptureLost += (_, _) => drag = null;
        ToolTip.SetTip(Avatar, "Drag the character to move · Right-click for size, always-on-top, and Close");
        dismiss = new Button
        {
            Content = "×",
            Width = 32,
            Height = 32,
            Padding = new(0),
            FontSize = 24,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            IsVisible = false,
            Background = Brushes.DarkSlateGray,
            Foreground = Brushes.White
        };
        ToolTip.SetTip(dismiss, "Dismiss this visit · the schedule continues");
        dismiss.Click += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(Avatar, 1); content.Children.Add(dismiss); content.Children.Add(Avatar); Content = content;
        Opened += (_, _) =>
        {
            if (ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
            { Background = Brushes.DarkSlateGray; ToolTip.SetTip(Avatar, "Desktop transparency is unavailable. Right-click to close; Spout2 remains transparent."); }
            if (Avatar.Snapshot is { } snapshot) UpdateSnapshot(snapshot);
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DismissOrClose(); };
        Closing += (_, e) => { if (automaticPresentation) { e.Cancel = true; DismissOrClose(); } };
        Closed += (_, _) => hitRegion.Dispose();
    }
    public void PackChanged() { hitRegion.ClearPack(); tightFallback = false; }
    public void UpdateSnapshot(AvatarOutputSnapshot snapshot)
    {
        Avatar.Snapshot = snapshot; Avatar.InvalidateVisual();
        if (IsVisible && !tightFallback && !hitRegion.Update(this, images, snapshot)) UseTightHitArea(snapshot);
    }
    internal void UseTightHitArea(AvatarOutputSnapshot snapshot)
    {
        if (tightFallback || !IsVisible) return;
        tightFallback = true;
        hitRegion.EnterTightFallback(this);
        var size = hitRegion.TightSize(this, images, snapshot); Width = size.Width;
        ToolTip.SetTip(Avatar, "Precise desktop click-through is unavailable; using a tight rectangular hit area. Drag to move; right-click to close.");
    }
    public void ResizeAvatar(double factor) { Width = Math.Clamp(Width * factor, 120, 1800); Height = Math.Clamp(Height * factor, 120, 1800); }
}
