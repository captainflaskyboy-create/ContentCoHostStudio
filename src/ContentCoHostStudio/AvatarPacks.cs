using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace ContentCoHostStudio;

public sealed record AvatarPack(string Id, string DisplayName, string Revision,
    IReadOnlyDictionary<string, byte[]> Sprites, SpriteCatalog Catalog, bool Bundled, byte[]? Icon = null);

public sealed record AvatarPackInfo(string Id, string DisplayName, bool Bundled)
{
    public override string ToString() => DisplayName;
}

// Compatible with Stream Avatar Studio's PNG folders and installed revision layout.
public sealed class AvatarPackLibrary
{
    public const string StarterId = "cfb_avatar_pack";
    private static readonly Regex SafeId = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex FrameName = new("^[a-z][a-z0-9_]{0,63}(?:_idle(?:_blink)?|_talk(?:_small|_large)?)(?:00[1-9]|0[1-9][0-9])\\.png$", RegexOptions.CultureInvariant);
    private static readonly Lazy<AvatarPack> BundledStarter = new(ReadStarter);
    private readonly string root;
    private readonly Dictionary<string, AvatarPack> installed = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Metadata(string Id, string DisplayName, string Revision);
    public AvatarPack Starter => BundledStarter.Value;
    public AvatarPackLibrary(string? folder = null) => root = Path.GetFullPath(folder ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ContentCoHostStudio", "Avatars"));
    public IReadOnlyList<AvatarPackInfo> Packs => [new(Starter.Id, Starter.DisplayName, true), .. installed.Values
        .OrderBy(pack => pack.DisplayName).Select(pack => new AvatarPackInfo(pack.Id, pack.DisplayName, false))];
    public AvatarPack? Find(string id) => id.Equals(StarterId, StringComparison.OrdinalIgnoreCase) ? Starter : installed.GetValueOrDefault(id);

    public void LoadInstalled()
    {
        if (!Directory.Exists(root)) return;
        CheckPath(root);
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            try
            {
                var pack = InspectFolder(folder);
                installed[pack.Id] = pack;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { /* Damaged packs do not prevent the starter and other valid packs loading. */ }
        }
    }

    private static AvatarPack ReadStarter()
    {
        var assembly = typeof(AvatarPackLibrary).Assembly;
        var sprites = assembly.GetManifestResourceNames().Where(name => name.StartsWith("Sprites.") && name.EndsWith(".png"))
            .ToDictionary(name => name, name => ReadResource(assembly, name));
        return new(StarterId, "Captain Flasky Boy", "bundled", sprites, new SpriteCatalog(sprites.Keys), true, ReadResource(assembly, "StarterIcon.ico"));
    }
    private static byte[] ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("Bundled avatar artwork is missing.");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }

    public static AvatarPack InspectFolder(string selectedFolder, string? displayName = null)
    {
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedFolder));
        CheckPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an avatar-pack folder.");
        string id = Path.GetFileName(folder);
        string revision = Guid.NewGuid().ToString("N");
        string name = id.Replace('_', ' ');
        string metadataPath = Path.Combine(folder, "pack.json");
        if (File.Exists(metadataPath))
        {
            CheckPath(metadataPath);
            if (new FileInfo(metadataPath).Length > 16_384) throw new InvalidDataException("Avatar metadata is too large.");
            var metadata = JsonSerializer.Deserialize<Metadata>(File.ReadAllText(metadataPath)) ?? throw new InvalidDataException("Invalid pack metadata.");
            ValidateIdentity(metadata.Id, metadata.DisplayName, metadata.Revision);
            if (!id.Equals(metadata.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The installed pack's ID does not match its folder.");
            id = metadata.Id; name = metadata.DisplayName; revision = metadata.Revision;
            folder = Path.Combine(folder, "revisions", revision);
            CheckPath(folder);
        }
        id = id.ToLowerInvariant();
        name = string.IsNullOrWhiteSpace(displayName) ? name : displayName.Trim();
        ValidateIdentity(id, name, revision);
        var sprites = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(folder).ToArray();
        if (files.Length > 4096) throw new InvalidDataException("The avatar folder contains too many files.");
        var icons = files.Where(path => Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase)).ToArray();
        string? iconPath = icons.FirstOrDefault(path => Path.GetFileName(path).Equals(id + "_app_icon.ico", StringComparison.OrdinalIgnoreCase))
            ?? icons.FirstOrDefault(path => Path.GetFileName(path).Equals("app_icon.ico", StringComparison.OrdinalIgnoreCase));
        if (iconPath == null && icons.Length > 1) throw new InvalidDataException("Keep one ICO or name the intended icon '<pack-folder-name>_app_icon.ico'.");
        iconPath ??= icons.SingleOrDefault();
        long total = 0;
        foreach (string path in files)
        {
            CheckPath(path);
            string file = Path.GetFileName(path);
            if (!FrameName.IsMatch(file)) continue;
            byte[] bytes = ReadLimited(path, 20_000_000);
            total += bytes.Length;
            if (total > 500_000_000 || sprites.Count >= 2000) throw new InvalidDataException("The pack exceeds 2,000 frames or 500 MB of artwork.");
            ValidatePng(bytes);
            sprites.Add("Sprites." + file, bytes);
        }
        if (!sprites.ContainsKey("Sprites.normal_idle001.png") || !sprites.ContainsKey("Sprites.normal_talk001.png"))
            throw new InvalidDataException("The pack needs normal_idle001.png and normal_talk001.png.");
        byte[]? icon = iconPath == null ? null : ReadLimited(iconPath, 20_000_000);
        if (icon != null && (icon.Length < 6 || icon[0] != 0 || icon[1] != 0 || icon[2] != 1 || icon[3] != 0))
            throw new InvalidDataException("The pack icon must be an ICO file.");
        if (icon != null)
        {
            int entries = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(icon.AsSpan(4, 2));
            if (entries is < 1 or > 256 || icon.Length < 6 + entries * 16) throw new InvalidDataException("Invalid ICO directory.");
            for (int i = 0; i < entries; i++)
            {
                var entry = icon.AsSpan(6 + i * 16, 16);
                uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
                uint offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
                if (length == 0 || offset < 6 + entries * 16 || (long)offset + length > icon.Length) throw new InvalidDataException("Invalid ICO image data.");
            }
        }
        if (total + (icon?.Length ?? 0) > 500_000_000) throw new InvalidDataException("The pack exceeds 500 MB.");
        return new(id, name, revision, sprites, new SpriteCatalog(sprites.Keys), false, icon);
    }

    private static byte[] ReadLimited(string path, long maximum)
    {
        var file = new FileInfo(path);
        if (file.Length is <= 0 || file.Length > maximum) throw new InvalidDataException("Invalid or oversized avatar artwork.");
        return File.ReadAllBytes(path);
    }
    private static void ValidatePng(byte[] bytes)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Avatar frames must be PNG images.");
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec == null || codec.Info.Width is < 1 or > 4096 || codec.Info.Height is < 1 or > 4096)
            throw new InvalidDataException("Avatar frames must be readable PNGs with dimensions between 1 and 4096 pixels.");
        using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Unreadable PNG frame.");
    }
    private static void ValidateIdentity(string id, string name, string revision)
    {
        if (string.IsNullOrWhiteSpace(id) || !SafeId.IsMatch(id) || id.Equals(StarterId, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(revision) || !SafeId.IsMatch(revision) || string.IsNullOrWhiteSpace(name) || name.Length > 80)
            throw new InvalidDataException("Invalid avatar identity or name. Use letters, numbers, hyphens, or underscores for folder names; do not use the bundled avatar's ID.");
    }
    public static void CheckPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked avatar folders and files are not accepted.");
    }

    public AvatarPack Install(AvatarPack inspected, bool replace = false)
    {
        ValidateIdentity(inspected.Id, inspected.DisplayName, inspected.Revision);
        if (inspected.Sprites.Count is < 2 or > 2000 || !inspected.Sprites.ContainsKey("Sprites.normal_idle001.png") ||
            !inspected.Sprites.ContainsKey("Sprites.normal_talk001.png") || inspected.Sprites.Values.Any(bytes => bytes.Length is 0 or > 20_000_000) ||
            inspected.Sprites.Values.Sum(bytes => (long)bytes.Length) + (inspected.Icon?.Length ?? 0) > 500_000_000)
            throw new InvalidDataException("Invalid or oversized avatar artwork.");
        if (installed.ContainsKey(inspected.Id) && !replace) throw new InvalidOperationException("This avatar is already installed. Confirm replacement first.");
        string directory = Path.Combine(root, inspected.Id);
        CheckPath(directory);
        string revision = Guid.NewGuid().ToString("N");
        string target = Path.Combine(directory, "revisions", revision);
        var pack = inspected with { Revision = revision, Bundled = false };
        Directory.CreateDirectory(target);
        try
        {
            foreach (var (key, bytes) in pack.Sprites)
            {
                string filename = key.StartsWith("Sprites.", StringComparison.Ordinal) ? key[8..] : "";
                if (!FrameName.IsMatch(filename)) throw new InvalidDataException("Unsafe avatar frame name.");
                File.WriteAllBytes(Path.Combine(target, filename), bytes);
            }
            if (pack.Icon != null) File.WriteAllBytes(Path.Combine(target, "app_icon.ico"), pack.Icon);
            WriteMetadata(directory, new(pack.Id, pack.DisplayName, revision));
            installed[pack.Id] = pack;
            return pack;
        }
        catch { DeleteSafeTree(target); throw; }
    }
    public AvatarPack Rename(string id, string displayName)
    {
        var pack = Find(id) ?? throw new InvalidOperationException("Select an installed avatar.");
        if (pack.Bundled) throw new InvalidOperationException("The bundled avatar cannot be renamed.");
        displayName = displayName.Trim();
        ValidateIdentity(id, displayName, pack.Revision);
        string directory = Path.Combine(root, id);
        CheckPath(directory);
        WriteMetadata(directory, new(id, displayName, pack.Revision));
        installed[id] = pack = pack with { DisplayName = displayName };
        return pack;
    }
    public void Remove(string id)
    {
        if (Find(id) is not { Bundled: false }) throw new InvalidOperationException("The bundled avatar cannot be removed.");
        DeleteSafeTree(Path.Combine(root, id));
        installed.Remove(id);
    }
    private static void WriteMetadata(string directory, Metadata metadata)
    {
        CheckPath(directory);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(metadata));
            string target = Path.Combine(directory, "pack.json");
            CheckPath(target);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void DeleteSafeTree(string target)
    {
        string absolute = Path.GetFullPath(target);
        if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Avatar removal must stay within this application's library.");
        CheckPath(absolute);
        if (!Directory.Exists(absolute)) return;
        // Check every child without following links before recursive deletion.
        var pending = new Stack<string>(); pending.Push(absolute);
        while (pending.TryPop(out var directory))
            foreach (string child in Directory.EnumerateFileSystemEntries(directory))
            {
                CheckPath(child);
                if (Directory.Exists(child)) pending.Push(child);
            }
        Directory.Delete(absolute, true);
    }
}
