using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ContentCoHostStudio;

public sealed class SpriteImages(AvatarPack pack) : IDisposable
{
    private readonly Dictionary<string, Bitmap> images = new();
    private readonly Queue<string> order = new();
    private AvatarPack pack = pack;
    public long CachedBytes { get; private set; }
    public byte[] GetData(string name) => pack.Sprites[name];
    public void SetPack(AvatarPack value) { Dispose(); pack = value; }
    public Bitmap Get(string name)
    {
        if (images.TryGetValue(name, out var bitmap)) return bitmap;
        byte[] png = GetData(name);
        long cost = Cost(png);
        while (CachedBytes + cost > 64 * 1024 * 1024 && order.TryDequeue(out string? oldest))
        { CachedBytes -= Cost(GetData(oldest)); images[oldest].Dispose(); images.Remove(oldest); }
        using var stream = new MemoryStream(png);
        images.Add(name, bitmap = new Bitmap(stream)); order.Enqueue(name); CachedBytes += cost;
        return bitmap;
    }
    private static long Cost(byte[] png) => (long)BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) * BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) * 4;
    public void Dispose()
    {
        foreach (var bitmap in images.Values) bitmap.Dispose();
        images.Clear(); order.Clear(); CachedBytes = 0;
    }
}

public sealed class AvatarCanvas : Control
{
    public SpriteImages? Images { get; set; }
    public AvatarOutputSnapshot? Snapshot { get; set; }
    public bool ShowWhenHidden { get; set; }
    public double SpriteScale { get; set; } = 1;
    public AvatarCanvas() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Images == null || Snapshot is not { } snapshot || (!snapshot.Visible && !ShowWhenHidden)) return;
        var bitmap = Images.Get(snapshot.Sprite);
        var p = AvatarComposition.Calculate(Bounds.Width, Bounds.Height, bitmap.PixelSize.Width, bitmap.PixelSize.Height, snapshot.Rotation, snapshot.X, snapshot.Y, SpriteScale);
        if (p.Scale <= 0) return;
        var transform = Matrix.CreateScale(snapshot.Flip ? -p.Scale : p.Scale, p.Scale) * Matrix.CreateRotation(p.Radians) * Matrix.CreateTranslation(p.CenterX, p.CenterY);
        using (context.PushTransform(transform)) context.DrawImage(bitmap, new Rect(-bitmap.PixelSize.Width / 2d, -bitmap.PixelSize.Height / 2d, bitmap.PixelSize.Width, bitmap.PixelSize.Height));
    }
}

public sealed class AvatarAnimator
{
    private readonly AnimationEngine engine = new(8);
    private long playbackId = -1;
    private long sequence;
    private bool wasActive;
    private string? revision;
    public bool Talking => engine.Talking;
    public AvatarOutputSnapshot Next(AvatarPack pack, PlaybackActivity activity, string pose, int sensitivity, int bob, int shake, long now)
    {
        if (activity.Id != playbackId || (wasActive && !activity.Active) || revision != pack.Revision) engine.ResetAudioTracking();
        playbackId = activity.Id;
        wasActive = activity.Active; revision = pack.Revision;
        var frame = engine.Next(pack.Catalog, pose, activity.Active ? activity.Level : 0, sensitivity, bob, shake, now);
        return new(frame.Resource, frame.X, frame.Y, false, 0, ++sequence, pack.Revision);
    }
    public void Reset() { engine.ResetAudioTracking(); playbackId = -1; }
}
