using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using SkiaSharp;


namespace ContentCoHostStudio;

/// <summary>Shapes the Win32 input window to the opaque pixels shown by AvatarCanvas.</summary>
internal sealed class StreamerHitRegion : IDisposable
{
    private const int UpdateIntervalMs = 80;
    private const int MaxCachedAlphaBytes = 32 * 1024 * 1024;
    private const int MaxRegionRectangles = 50_000;
    private readonly object gate = new();
    private readonly Dictionary<string, AlphaMask> masks = new(StringComparer.Ordinal);
    private readonly Queue<string> maskOrder = new();
    private int cachedAlphaBytes;
    private int generation;
    private bool disposed, workerRunning, hasRegion;
    private long lastBuildStart;
    private RegionRequest? pending;
    private readonly RegionRequestState requestState = new();
    private CoarseKey? appliedCoarse;

    internal readonly record struct AlphaMask(int Width, int Height, byte[] Alpha);
    internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);
    internal readonly record struct RegionKey(string Revision, string Sprite, double X, double Y, bool Flip,
        double Rotation, double Width, double Height, double RenderScale, double SpriteScale, double ChromeHeight = 0);
    internal sealed class RegionRequestState
    {
        private RegionKey? desired;
        private RegionKey? applied;
        public bool SetDesired(RegionKey key) { desired = key; return applied == key; }
        public bool IsCurrent(RegionKey key) => desired == key;
        public void MarkApplied(RegionKey key) => applied = key;
        public void InvalidateApplied() => applied = null;
        public void Clear() { desired = null; applied = null; }
    }
    private readonly record struct CoarseKey(string Revision, int SpriteWidth, int SpriteHeight,
        double Rotation, double Width, double Height, double RenderScale, double SpriteScale, double ChromeHeight = 0);
    private sealed record RegionRequest(StreamerWindow Window, nint Handle, byte[] Png, AvatarOutputSnapshot Snapshot,
        double Width, double Height, double RenderScale, double SpriteScale, int Generation,
        RegionKey Key);

    public void ClearPack()
    {
        Interlocked.Increment(ref generation);
        lock (gate)
        {
            masks.Clear(); maskOrder.Clear(); cachedAlphaBytes = 0; pending = null;
        }
        requestState.Clear(); appliedCoarse = null; hasRegion = false;
    }

    public bool Update(StreamerWindow window, SpriteImages images, AvatarOutputSnapshot snapshot)
    {
        if (!OperatingSystem.IsWindows() || window.ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
            return false;
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0 || disposed) return false;
        var renderScale = window.RenderScaling;
        var pixelWidth = (int)Math.Ceiling(window.Bounds.Width * renderScale);
        var pixelHeight = (int)Math.Ceiling(window.Bounds.Height * renderScale);
        if (pixelWidth < 1 || pixelHeight < 1) return false;

        var png = images.GetData(snapshot.Sprite);
        var spriteWidth = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var spriteHeight = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        var key = new RegionKey(snapshot.PackRevision, snapshot.Sprite, snapshot.X, snapshot.Y,
            snapshot.Flip, snapshot.Rotation, window.Bounds.Width, window.Bounds.Height,
            renderScale, window.Avatar.SpriteScale, window.ChromeHeight);
        var coarse = new CoarseKey(snapshot.PackRevision, spriteWidth, spriteHeight,
            snapshot.Rotation, window.Bounds.Width, window.Bounds.Height, renderScale, window.Avatar.SpriteScale, window.ChromeHeight);
        var alreadyApplied = requestState.SetDesired(key);

        // A single cheap rectangle prevents the full window intercepting clicks while the
        // precise region for a new pack, size, or rotation is prepared off the UI thread.
        var coarseChanged = !hasRegion || appliedCoarse != coarse;
        if (coarseChanged)
        {
            var placement = Placement(window.Bounds.Width, window.Bounds.Height, spriteWidth, spriteHeight, snapshot, window.Avatar.SpriteScale, window.ChromeHeight);
            if (placement.Scale <= 0 || !ApplyCoarseRegion(handle, spriteWidth, spriteHeight, placement, renderScale, window.Bounds.Width, window.ChromeHeight))
                return false;
            appliedCoarse = coarse; requestState.InvalidateApplied(); hasRegion = true;
        }
        if (alreadyApplied && !coarseChanged)
        {
            lock (gate) pending = null;
            return true;
        }

        var request = new RegionRequest(window, handle, png, snapshot, window.Bounds.Width, window.Bounds.Height,
            renderScale, window.Avatar.SpriteScale, Volatile.Read(ref generation), key);
        lock (gate)
        {
            if (disposed) return false;
            pending = request; // Keep only the newest animation frame.
            if (!workerRunning)
            {
                workerRunning = true;
                _ = Task.Run(ProcessPendingAsync);
            }
        }
        return true;
    }

    private async Task ProcessPendingAsync()
    {
        while (true)
        {
            RegionRequest request;
            int delay;
            lock (gate)
            {
                if (disposed || pending is null) { workerRunning = false; return; }
                request = pending; pending = null;
                delay = (int)Math.Max(0, UpdateIntervalMs - (Environment.TickCount64 - lastBuildStart));
            }
            if (delay > 0) await Task.Delay(delay);
            lock (gate)
            {
                if (disposed) { workerRunning = false; return; }
                if (pending is { } newer) { request = newer; pending = null; }
                lastBuildStart = Environment.TickCount64;
            }
            if (request.Generation != Volatile.Read(ref generation)) continue;

            nint region = 0;
            try
            {
                var mask = GetMask(request);
                var rectangles = BuildRectangles(mask, request.Width, request.Height, request.RenderScale,
                    request.SpriteScale, request.Snapshot, () => request.Generation != Volatile.Read(ref generation), request.Key.ChromeHeight);
                region = CreateRegion(rectangles);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (disposed || request.Window.UsesTightHitArea || request.Generation != Volatile.Read(ref generation) ||
                        !requestState.IsCurrent(request.Key) || !request.Window.IsVisible ||
                        request.Window.TryGetPlatformHandle()?.Handle != request.Handle) return;
                    if (SetWindowRgn(request.Handle, region, true) == 0)
                    {
                        request.Window.UseTightHitArea(request.Snapshot);
                        return;
                    }
                    region = 0; // Windows owns a successfully installed region.
                    requestState.MarkApplied(request.Key);
                    hasRegion = true;
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!disposed && request.Generation == Volatile.Read(ref generation) &&
                            requestState.IsCurrent(request.Key) && request.Window.IsVisible)
                            request.Window.UseTightHitArea(request.Snapshot);
                    });
                }
                catch (Exception) { /* The window may have closed while failure was reported. */ }
            }
            finally { if (region != 0) DeleteObject(region); }
        }
    }

    public bool EnterTightFallback(StreamerWindow window)
    {
        Interlocked.Increment(ref generation);
        lock (gate) pending = null;
        requestState.Clear(); appliedCoarse = null; hasRegion = false;
        if (!OperatingSystem.IsWindows()) return true;
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) return false;
        return ResetWindowRegion(handle, window.Bounds.Width, window.Bounds.Height, window.RenderScaling);
    }

    internal static bool ResetWindowRegion(nint handle, double width, double height, double renderScale)
    {
        if (SetWindowRgn(handle, 0, true) != 0) return true; // Remove any previously installed shape.
        var region = CreateRectRgn(0, 0, (int)Math.Ceiling(width * renderScale),
            (int)Math.Ceiling(height * renderScale));
        if (region == 0) return false;
        try
        {
            if (SetWindowRgn(handle, region, true) == 0) return false;
            region = 0; // Windows owns the replacement region.
            return true;
        }
        finally { if (region != 0) DeleteObject(region); }
    }

    private AlphaMask GetMask(RegionRequest request)
    {
        var cacheKey = request.Snapshot.PackRevision + "|" + request.Snapshot.Sprite;
        lock (gate) if (masks.TryGetValue(cacheKey, out var cached)) return cached;
        using var bitmap = SKBitmap.Decode(request.Png) ?? throw new InvalidDataException("Unreadable streamer sprite.");
        var colors = bitmap.Pixels;
        var alpha = new byte[colors.Length];
        for (var i = 0; i < colors.Length; i++) alpha[i] = colors[i].Alpha;
        var mask = new AlphaMask(bitmap.Width, bitmap.Height, alpha);
        lock (gate)
        {
            if (request.Generation != generation || disposed) throw new OperationCanceledException();
            if (alpha.Length <= MaxCachedAlphaBytes)
            {
                while (cachedAlphaBytes + alpha.Length > MaxCachedAlphaBytes && maskOrder.Count > 0)
                {
                    var oldest = maskOrder.Dequeue();
                    cachedAlphaBytes -= masks[oldest].Alpha.Length;
                    masks.Remove(oldest);
                }
                masks.Add(cacheKey, mask); maskOrder.Enqueue(cacheKey); cachedAlphaBytes += alpha.Length;
            }
        }
        return mask;
    }

    internal static PixelRect[] BuildRectangles(AlphaMask mask, double width, double height, double renderScale,
        double spriteScale, AvatarOutputSnapshot snapshot, Func<bool>? canceled = null, double chromeHeight = 0)
    {
        var placement = Placement(width, height, mask.Width, mask.Height, snapshot, spriteScale, chromeHeight);
        if (placement.Scale <= 0) return [];
        var pixelWidth = (int)Math.Ceiling(width * renderScale);
        var pixelHeight = (int)Math.Ceiling(height * renderScale);
        var bounds = SpriteBounds(mask.Width, mask.Height, placement, renderScale);
        var left = Math.Max(0, bounds.Left); var right = Math.Min(pixelWidth, bounds.Right);
        var top = Math.Max((int)Math.Ceiling(chromeHeight * renderScale), bounds.Top); var bottom = Math.Min(pixelHeight, bounds.Bottom);
        var cosine = Math.Cos(placement.Radians);
        var sine = Math.Sin(placement.Radians);
        var inverseScale = 1 / (placement.Scale * renderScale);
        var centerX = placement.CenterX * renderScale;
        var centerY = placement.CenterY * renderScale;
        var rectangles = new List<PixelRect>();
        if (chromeHeight > 0) rectangles.Add(DismissRectangle(width, chromeHeight, renderScale));
        for (var y = top; y < bottom; y++)
        {
            if ((y & 7) == 0 && canceled?.Invoke() == true) throw new OperationCanceledException();
            var run = -1;
            for (var x = left; x <= right; x++)
            {
                var opaque = x < right && OpaqueAt(mask, centerX, centerY, cosine, sine,
                    inverseScale, snapshot.Flip, x, y);
                if (opaque && run < 0) run = x;
                else if (!opaque && run >= 0)
                {
                    rectangles.Add(new PixelRect(run, y, x, y + 1));
                    if (rectangles.Count > MaxRegionRectangles)
                        throw new InvalidDataException("The streamer sprite's input shape is too complex.");
                    run = -1;
                }
            }
        }
        return rectangles.ToArray();
    }

    internal static PixelRect DismissRectangle(double width, double chromeHeight, double scale) => new(
        (int)Math.Floor((width - 32) / 2 * scale), 0, (int)Math.Ceiling((width + 32) / 2 * scale), (int)Math.Ceiling(chromeHeight * scale));
    private static AvatarComposition Placement(double width, double height, int spriteWidth, int spriteHeight,
        AvatarOutputSnapshot snapshot, double spriteScale, double chromeHeight)
    {
        var p = AvatarComposition.Calculate(width, height - chromeHeight, spriteWidth, spriteHeight,
            snapshot.Rotation, snapshot.X, snapshot.Y, spriteScale);
        return p with { CenterY = p.CenterY + chromeHeight };
    }

    private static PixelRect SpriteBounds(int spriteWidth, int spriteHeight, AvatarComposition placement, double renderScale)
    {
        var rotatedWidth = Math.Abs(spriteWidth * Math.Cos(placement.Radians)) +
            Math.Abs(spriteHeight * Math.Sin(placement.Radians));
        var rotatedHeight = Math.Abs(spriteWidth * Math.Sin(placement.Radians)) +
            Math.Abs(spriteHeight * Math.Cos(placement.Radians));
        var halfWidth = rotatedWidth * placement.Scale * renderScale / 2;
        var halfHeight = rotatedHeight * placement.Scale * renderScale / 2;
        var x = placement.CenterX * renderScale;
        var y = placement.CenterY * renderScale;
        return new PixelRect((int)Math.Floor(x - halfWidth) - 2, (int)Math.Floor(y - halfHeight) - 2,
            (int)Math.Ceiling(x + halfWidth) + 2, (int)Math.Ceiling(y + halfHeight) + 2);
    }

    private static bool OpaqueAt(AlphaMask mask, double centerX, double centerY, double cosine, double sine,
        double inverseScale, bool flip, int x, int y)
    {
        var dx = x + .5 - centerX;
        var dy = y + .5 - centerY;
        var sx = (dx * cosine + dy * sine) * inverseScale * (flip ? -1 : 1) + mask.Width / 2d;
        var sy = (-dx * sine + dy * cosine) * inverseScale + mask.Height / 2d;
        var ix = (int)Math.Floor(sx); var iy = (int)Math.Floor(sy);
        return ix >= 0 && ix < mask.Width && iy >= 0 && iy < mask.Height && mask.Alpha[iy * mask.Width + ix] > 0;
    }

    // Kept as a reference for pixel-accuracy tests of the cached managed mask.
    internal static bool OpaqueAt(SKBitmap sprite, double centerX, double centerY, double cosine, double sine,
        double inverseScale, bool flip, int x, int y)
    {
        var dx = x + .5 - centerX;
        var dy = y + .5 - centerY;
        var sx = (dx * cosine + dy * sine) * inverseScale * (flip ? -1 : 1) + sprite.Width / 2d;
        var sy = (-dx * sine + dy * cosine) * inverseScale + sprite.Height / 2d;
        var ix = (int)Math.Floor(sx); var iy = (int)Math.Floor(sy);
        return ix >= 0 && ix < sprite.Width && iy >= 0 && iy < sprite.Height && sprite.GetPixel(ix, iy).Alpha > 0;
    }

    private static bool ApplyCoarseRegion(nint handle, int spriteWidth, int spriteHeight,
        AvatarComposition placement, double renderScale, double width, double chromeHeight)
    {
        var bounds = SpriteBounds(spriteWidth, spriteHeight, placement, renderScale);
        var region = CreateRectRgn(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        if (region != 0 && chromeHeight > 0)
        {
            var button = DismissRectangle(width, chromeHeight, renderScale);
            var buttonRegion = CreateRectRgn(button.Left, button.Top, button.Right, button.Bottom);
            try { if (buttonRegion == 0 || CombineRgn(region, region, buttonRegion, 2) == 0) { DeleteObject(region); return false; } }
            finally { if (buttonRegion != 0) DeleteObject(buttonRegion); }
        }
        if (region == 0) return false;
        try
        {
            if (SetWindowRgn(handle, region, true) == 0) return false;
            region = 0;
            return true;
        }
        finally { if (region != 0) DeleteObject(region); }
    }

    internal static nint CreateRegion(PixelRect[] rectangles)
    {
        if (rectangles.Length == 0)
        {
            var empty = CreateRectRgn(0, 0, 0, 0);
            if (empty == 0) throw new InvalidOperationException("Windows could not create an empty streamer input region.");
            return empty;
        }
        // RGNDATAHEADER is eight 32-bit values, followed by non-overlapping RECTs
        // in top-to-bottom, left-to-right order.
        var values = new int[8 + rectangles.Length * 4];
        values[0] = 32; values[1] = 1; values[2] = rectangles.Length; values[3] = rectangles.Length * 16;
        values[4] = rectangles.Min(r => r.Left); values[5] = rectangles[0].Top;
        values[6] = rectangles.Max(r => r.Right); values[7] = rectangles[^1].Bottom;
        for (var i = 0; i < rectangles.Length; i++)
        {
            var rect = rectangles[i]; var offset = 8 + i * 4;
            values[offset] = rect.Left; values[offset + 1] = rect.Top;
            values[offset + 2] = rect.Right; values[offset + 3] = rect.Bottom;
        }
        var data = Marshal.AllocHGlobal(values.Length * sizeof(int));
        try
        {
            Marshal.Copy(values, 0, data, values.Length);
            var region = ExtCreateRegion(0, (uint)(values.Length * sizeof(int)), data);
            if (region == 0) throw new InvalidOperationException("Windows could not create the streamer input region.");
            return region;
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    public (double Width, double Height) TightSize(StreamerWindow window, SpriteImages images, AvatarOutputSnapshot snapshot)
    {
        using var sprite = SKBitmap.Decode(images.GetData(snapshot.Sprite)) ?? throw new InvalidDataException("Unreadable streamer sprite.");
        var radians = snapshot.Rotation * Math.PI / 180;
        var rotatedWidth = Math.Abs(sprite.Width * Math.Cos(radians)) + Math.Abs(sprite.Height * Math.Sin(radians));
        var rotatedHeight = Math.Abs(sprite.Width * Math.Sin(radians)) + Math.Abs(sprite.Height * Math.Cos(radians));
        var height = window.Height;
        var width = Math.Clamp((height - 60) * rotatedWidth / Math.Max(1, rotatedHeight) + 60, 120, 1800);
        return (width, height);
    }

    public void Dispose()
    {
        disposed = true;
        ClearPack();
    }

    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint destination, nint first, nint second, int mode);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern nint ExtCreateRegion(nint transform, uint byteCount, nint data);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
}
