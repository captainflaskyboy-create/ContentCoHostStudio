using System.Text.Json;
using SkiaSharp;
using Xunit;

namespace ContentCoHostStudio.Tests;

public class AvatarPackTests : IDisposable
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "cohost-avatar-tests-" + Guid.NewGuid().ToString("N"));
    private string LibraryRoot => Path.Combine(temporary, "library");
    private string PackFolder => Path.Combine(temporary, "test_avatar");
    private AvatarPackLibrary Library() => new(LibraryRoot);
    private void CreatePack()
    {
        Directory.CreateDirectory(PackFolder);
        foreach (string frame in new[] { "normal_idle001", "normal_idle002", "normal_idle_blink001", "normal_talk001", "normal_talk_small001", "normal_talk_large001", "sly_idle001", "sly_talk001" })
            File.WriteAllBytes(Path.Combine(PackFolder, frame + ".png"), Png());
        File.WriteAllBytes(Path.Combine(PackFolder, "ignored.gif"), [71, 73, 70]);
    }
    internal static byte[] Png(int width = 8, int height = 8)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(220, 60, 30, 150));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void CompatiblePackImportsReopensRenamesAndRemovesWithoutChangingSource()
    {
        CreatePack();
        var inspected = AvatarPackLibrary.InspectFolder(PackFolder, "My Robot");
        Assert.Equal(8, inspected.Sprites.Count);
        Assert.True(inspected.Catalog.Available("Sly"));
        Assert.Equal(2, inspected.Catalog.Sets["Normal"].Idle.Length);
        Assert.True(inspected.Catalog.Sets["Normal"].HasTier(MouthTier.Small));
        var library = Library();
        var installed = library.Install(inspected);
        Assert.Equal("Captain Flasky Boy", library.Starter.DisplayName);
        Assert.Equal(2, library.Packs.Count);
        var reopened = Library(); reopened.LoadInstalled();
        Assert.Equal("My Robot", reopened.Find(installed.Id)!.DisplayName);
        var renamed = reopened.Rename(installed.Id, "Robot Two");
        Assert.Equal(installed.Revision, renamed.Revision);
        var again = Library(); again.LoadInstalled();
        Assert.Equal("Robot Two", again.Find(installed.Id)!.DisplayName);
        again.Remove(installed.Id);
        Assert.Null(again.Find(installed.Id));
        Assert.True(File.Exists(Path.Combine(PackFolder, "normal_idle001.png")));
        Assert.Single(again.Packs);
    }

    [Fact]
    public void InstalledStreamAvatarLayoutAndOptionalIconImportIntoSeparateLibrary()
    {
        CreatePack();
        var starter = Library().Starter;
        File.WriteAllBytes(Path.Combine(PackFolder, "test_avatar_app_icon.ico"), starter.Icon!);
        var first = Library().Install(AvatarPackLibrary.InspectFolder(PackFolder));
        string installedFolder = Path.Combine(LibraryRoot, first.Id);
        var imported = AvatarPackLibrary.InspectFolder(installedFolder);
        var separate = new AvatarPackLibrary(Path.Combine(temporary, "separate"));
        var saved = separate.Install(imported);
        Assert.Equal(first.Icon, saved.Icon);
        Assert.Equal(first.Sprites.Keys, saved.Sprites.Keys);
        Assert.True(File.Exists(Path.Combine(installedFolder, "pack.json")));
    }

    [Fact]
    public void ReplacementRequiresConfirmationAndPreviousRevisionRemainsImmutable()
    {
        CreatePack();
        var library = Library();
        var first = library.Install(AvatarPackLibrary.InspectFolder(PackFolder));
        File.WriteAllBytes(Path.Combine(PackFolder, "normal_idle001.png"), Png(10, 10));
        var next = AvatarPackLibrary.InspectFolder(PackFolder);
        Assert.Throws<InvalidOperationException>(() => library.Install(next));
        Assert.Same(first, library.Find(first.Id));
        var replaced = library.Install(next, replace: true);
        Assert.NotEqual(first.Revision, replaced.Revision);
        Assert.NotEqual(first.Sprites["Sprites.normal_idle001.png"], replaced.Sprites["Sprites.normal_idle001.png"]);
        Assert.Equal(8, first.Sprites.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid-png")]
    [InlineData("oversized")]
    [InlineData("unsafe-metadata")]
    public void InvalidPackLeavesInstalledArtworkIntact(string failure)
    {
        CreatePack();
        var library = Library();
        var first = library.Install(AvatarPackLibrary.InspectFolder(PackFolder));
        string idle = Path.Combine(PackFolder, "normal_idle001.png");
        switch (failure)
        {
            case "missing": File.Delete(idle); break;
            case "invalid-png": File.WriteAllText(idle, "not an image"); break;
            case "oversized": File.WriteAllBytes(idle, Png(4097, 1)); break;
            case "unsafe-metadata": File.WriteAllText(Path.Combine(PackFolder, "pack.json"), JsonSerializer.Serialize(new { Id = "test_avatar", DisplayName = "Bad", Revision = "../outside" })); break;
        }
        Assert.Throws<InvalidDataException>(() => AvatarPackLibrary.InspectFolder(PackFolder));
        Assert.Same(first, library.Find(first.Id));
        var reopened = Library(); reopened.LoadInstalled();
        Assert.Equal(first.Revision, reopened.Find(first.Id)!.Revision);
    }

    [Fact]
    public void DamagedInstalledPackDoesNotPreventStarterAndValidPacksLoading()
    {
        CreatePack(); var library = Library(); library.Install(AvatarPackLibrary.InspectFolder(PackFolder));
        string bad = Path.Combine(LibraryRoot, "broken"); Directory.CreateDirectory(bad);
        File.WriteAllText(Path.Combine(bad, "pack.json"), "bad metadata");
        var reopened = Library(); reopened.LoadInstalled();
        Assert.Equal(2, reopened.Packs.Count);
        Assert.Throws<InvalidOperationException>(() => reopened.Remove(AvatarPackLibrary.StarterId));
        Assert.Throws<InvalidOperationException>(() => reopened.Rename(AvatarPackLibrary.StarterId, "Other"));
    }

    [Fact]
    public void UnsafeFrameNamesAndIdentitiesCannotWriteOutsideLibrary()
    {
        CreatePack();
        var pack = AvatarPackLibrary.InspectFolder(PackFolder);
        Assert.Throws<InvalidDataException>(() => Library().Install(pack with { Id = "../outside" }));
        Assert.Throws<InvalidDataException>(() => Library().Install(pack with { Sprites = new Dictionary<string, byte[]> { ["Sprites.../outside.png"] = Png() } }));
        Assert.False(File.Exists(Path.Combine(temporary, "outside.png")));
    }

    [Fact]
    public void LinkedArtworkIsRejectedWithoutChangingTheLinkedTarget()
    {
        CreatePack();
        string original = Path.Combine(temporary, "original.png"); File.WriteAllBytes(original, Png());
        string link = Path.Combine(PackFolder, "normal_idle003.png");
        try { File.CreateSymbolicLink(link, original); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        { Assert.Skip("This Windows test environment does not permit creating symbolic links."); }
        Assert.Throws<InvalidDataException>(() => AvatarPackLibrary.InspectFolder(PackFolder));
        File.Delete(link);
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void SpoutBinaryRejectsMissingAndUnverifiedFiles()
    {
        Directory.CreateDirectory(temporary);
        string dll = Path.Combine(temporary, "wrong.dll");
        Assert.Throws<InvalidOperationException>(() => SpoutSenderInstallation.Validate(dll));
        File.WriteAllText(dll, "not the tested library");
        Assert.Throws<InvalidOperationException>(() => SpoutSenderInstallation.Validate(dll));
    }

    public void Dispose()
    {
        string full = Path.GetFullPath(temporary);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
