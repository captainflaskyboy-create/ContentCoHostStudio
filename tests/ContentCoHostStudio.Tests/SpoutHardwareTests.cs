using System.Runtime.InteropServices;

using Xunit;

namespace ContentCoHostStudio.Tests;

public sealed class SpoutHardwareTests
{
    public static bool HardwareEnabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("CONTENT_COHOST_SPOUT_HARDWARE_TESTS") == "1"
        && File.Exists(SpoutSenderInstallation.DefaultPath);

    [Fact]
    [Trait("Category", "NativeSpout")]
    public async Task NativeReceiverPreservesBgraAlphaOrientationAndRestart()
    {
        if (!HardwareEnabled) Assert.Skip("Set CONTENT_COHOST_SPOUT_HARDWARE_TESTS=1 for a native sender/receiver test using the verified local DLL.");
        var sprites = typeof(SpriteImages).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("Sprites.") && n.EndsWith(".png")).ToArray();
        var senderPath = SpoutSenderInstallation.DefaultPath;
        await using var output = new SpoutOutputService(sprites, () => new SpoutNativeSender());
        await output.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(output.Running);
        await Task.Run(() =>
        {
            // The receiver owns its own context on this thread, independently of the sender.
            using var receiver = new Receiver(output.SenderName, senderPath);
            using var renderer = new SpoutFrameRenderer(sprites);
            long sequence = 0;
            foreach (var (flip, rotation) in new[] { (false, 0d), (true, 0d), (true, 45d), (false, -90d) })
            {
                var snapshot = new AvatarOutputSnapshot("Sprites.normal_idle001.png", 4, -3, flip, rotation, ++sequence);
                var expected = new byte[512 * 512 * 4]; Marshal.Copy(renderer.Render(snapshot), expected, 0, expected.Length);
                output.Publish(snapshot);
                var deadline = DateTime.UtcNow.AddSeconds(5); var matched = false;
                while (DateTime.UtcNow < deadline)
                {
                    TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                    if (receiver.Read().SequenceEqual(expected)) { matched = true; break; }
                    Thread.Sleep(40);
                }
                Assert.True(matched, $"Native pixels did not match BGRA/alpha/orientation for flip={flip}, rotation={rotation}.");
            }
        }, TestContext.Current.CancellationToken);
        await output.StopAsync(); Assert.False(output.Running);
        await output.StartAsync(TestContext.Current.CancellationToken); Assert.True(output.Running);
        await output.StopAsync(); Assert.False(output.Running);
    }

    private sealed class Receiver : IDisposable
    {
        private readonly nint library, instance, pixels;
        private readonly byte[] bytes = new byte[512 * 512 * 4];
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint Factory();
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)][return: MarshalAs(UnmanagedType.I1)] private delegate bool Open(nint self, nint window);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void Name(nint self, [MarshalAs(UnmanagedType.LPStr)] string name);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)][return: MarshalAs(UnmanagedType.I1)] private delegate bool Receive(nint self, nint pixels, uint format, [MarshalAs(UnmanagedType.I1)] bool invert, uint fbo);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void Release(nint self);
        [UnmanagedFunctionPointer(CallingConvention.ThisCall)][return: MarshalAs(UnmanagedType.I1)] private delegate bool Close(nint self);

        public Receiver(string sender, string senderPath)
        {
            library = NativeLibrary.Load(senderPath);
            instance = Marshal.GetDelegateForFunctionPointer<Factory>(NativeLibrary.GetExport(library, "GetSpout"))();
            pixels = Marshal.AllocHGlobal(bytes.Length);
            try { Assert.True(Method<Open>(151)(instance, 0)); Method<Name>(16)(instance, sender); }
            catch { Dispose(); throw; }
        }
        private T Method<T>(int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
        public byte[] Read()
        {
            if (Method<Receive>(20)(instance, pixels, 0x80E1, false, 0)) Marshal.Copy(pixels, bytes, 0, bytes.Length);
            return bytes;
        }
        public void Dispose()
        {
            Method<Release>(18)(instance); Method<Close>(152)(instance); Method<Release>(171)(instance);
            Marshal.FreeHGlobal(pixels); NativeLibrary.Free(library);
        }
    }
}
