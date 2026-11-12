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
    public StreamerWindow(SpriteImages images)
    {
        this.images = images;
        Title = "Content CoHost Studio — Desktop Avatar";
        Width = Height = 420; MinWidth = MinHeight = 120; MaxWidth = MaxHeight = 1800;
        WindowDecorations = WindowDecorations.None; Topmost = true; CanResize = false;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent]; Background = Brushes.Transparent;
        Avatar = new() { Images = images };
        var larger = new MenuItem { Header = "Larger" }; larger.Click += (_, _) => ResizeAvatar(1.2);
        var smaller = new MenuItem { Header = "Smaller" }; smaller.Click += (_, _) => ResizeAvatar(1 / 1.2);
        var reset = new MenuItem { Header = "Reset Size" }; reset.Click += (_, _) => { Width = Height = 420; };
        var top = new MenuItem { Header = "Always on Top", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
        top.Click += (_, _) => Topmost = top.IsChecked;
        var exit = new MenuItem { Header = "Close Desktop Avatar" }; exit.Click += (_, _) => Close();
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
        Content = Avatar;
        Opened += (_, _) =>
        {
            if (ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
            { Background = Brushes.DarkSlateGray; ToolTip.SetTip(Avatar, "Desktop transparency is unavailable. Right-click to close; Spout2 remains transparent."); }
            if (Avatar.Snapshot is { } snapshot) UpdateSnapshot(snapshot);
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
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
