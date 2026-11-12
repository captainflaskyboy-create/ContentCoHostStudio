using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SkiaSharp;

namespace ContentCoHostStudio;

public static class SpoutSenderInstallation
{
    public const string ExpectedHash = "81BC61C29872CBAA6CE7442C9B2A94765F31EB6563A526587FA158ADA4748546";
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "native", "Spout2", "SpoutLibrary.dll");
    public static string Validate(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) throw new InvalidOperationException("The tested Spout2 sender DLL is missing. See README for local SDK setup.");
        if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(ExpectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Spout2 sender DLL does not match the tested SDK 2.007.017. Restore the verified local DLL before enabling output.");
        return path;
    }
}

public interface IAvatarFrameRenderer : IDisposable
{
    nint Render(AvatarOutputSnapshot snapshot);
}

// The sender worker owns all Skia and native memory, including replacement and cleanup.
public sealed class SpoutFrameRenderer : IAvatarFrameRenderer
{
    private readonly IReadOnlyDictionary<string, byte[]> sprites;
    private readonly Dictionary<string, SKBitmap> images = new();
    private readonly Queue<string> order = new();
    private long decodedBytes;
    private readonly SKBitmap target;
    private readonly int size;
    private readonly SKColor background;
    private readonly SKCanvas canvas;
    private readonly byte[] pixels;
    private nint straightPixels;
    private readonly SKPaint paint = new() { IsAntialias = false };
    public ReadOnlyMemory<byte> Pixels => pixels;
    public SpoutFrameRenderer(AvatarPack pack, int size = 512, uint? backgroundArgb = null) : this(pack.Sprites, size, backgroundArgb) { }
    private SpoutFrameRenderer(IReadOnlyDictionary<string, byte[]> sprites, int size, uint? backgroundArgb)
    {
        if (size is not (512 or 1080)) throw new ArgumentOutOfRangeException(nameof(size));
        this.sprites = sprites; this.size = size;
        background = backgroundArgb is { } argb ? new SKColor((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, 255) : SKColors.Transparent;
        target = new(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        canvas = new(target);
        pixels = new byte[size * size * 4];
        straightPixels = Marshal.AllocHGlobal(pixels.Length);
    }
    public SpoutFrameRenderer(IEnumerable<string> names) : this(names.ToDictionary(name => name, name =>
        {
            using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidDataException("Missing sprite.");
            using var buffer = new MemoryStream(); input.CopyTo(buffer); return buffer.ToArray();
        }), 512, null)
    { }
    public nint Render(AvatarOutputSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(straightPixels == 0, this);
        if (!images.TryGetValue(snapshot.Sprite, out var bitmap))
        {
            bitmap = SKBitmap.Decode(sprites[snapshot.Sprite]) ?? throw new InvalidDataException("Unreadable avatar frame.");
            long cost = (long)bitmap.Width * bitmap.Height * 4;
            while (decodedBytes + cost > 64 * 1024 * 1024 && order.TryDequeue(out string? oldest))
            {
                var old = images[oldest]; decodedBytes -= (long)old.Width * old.Height * 4; old.Dispose(); images.Remove(oldest);
            }
            images.Add(snapshot.Sprite, bitmap); order.Enqueue(snapshot.Sprite); decodedBytes += cost;
        }
        canvas.Clear(background);
        var p = AvatarComposition.Calculate(512, 512, bitmap.Width, bitmap.Height, snapshot.Rotation, snapshot.X, snapshot.Y);
        canvas.Save(); canvas.Scale(size / 512f); canvas.Translate((float)p.CenterX, (float)p.CenterY); canvas.RotateRadians((float)p.Radians);
        canvas.Scale((float)(snapshot.Flip ? -p.Scale : p.Scale), (float)p.Scale);
        using (var image = SKImage.FromBitmap(bitmap)) canvas.DrawImage(image, -bitmap.Width / 2f, -bitmap.Height / 2f, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        canvas.Restore(); canvas.Flush();
        Marshal.Copy(target.GetPixels(), pixels, 0, pixels.Length);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int alpha = pixels[i + 3];
            if (alpha is 0 or 255) continue;
            for (int channel = 0; channel < 3; channel++) pixels[i + channel] = (byte)Math.Min(255, (pixels[i + channel] * 255 + alpha / 2) / alpha);
        }
        Marshal.Copy(pixels, 0, straightPixels, pixels.Length);
        return straightPixels;
    }
    public void Dispose()
    {
        if (straightPixels == 0) return;
        foreach (var bitmap in images.Values) bitmap.Dispose();
        images.Clear(); order.Clear(); paint.Dispose(); canvas.Dispose(); target.Dispose();
        Marshal.FreeHGlobal(straightPixels); straightPixels = 0;
    }
}
