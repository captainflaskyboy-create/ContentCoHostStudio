using System.Runtime.InteropServices;

namespace ContentCoHostStudio;

public interface ISpoutSender : IDisposable
{
    string Name { get; }
    void Send(nint bgraPixels);
}

/// <summary>Scalar/pointer ABI tested with the x64 SpoutLibrary 2.007.017 MT build.
/// The installation validator rejects all other binary versions.</summary>
public sealed class SpoutNativeSender : ISpoutSender
{
    private nint library, instance;
    // MSVC groups overloaded virtual methods: GetName(std::string) precedes GetName().
    public string Name => Marshal.PtrToStringAnsi(Method<GetName>(8)(instance)) ?? "ContentCoHostStudio";
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint Factory();
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void SetName(nint self, [MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void SetFormat(nint self, uint format);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void ReleaseSender(nint self, uint wait);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate nint GetName(nint self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)][return: MarshalAs(UnmanagedType.I1)] private delegate bool CreateGl(nint self, nint window);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)][return: MarshalAs(UnmanagedType.I1)] private delegate bool CloseGl(nint self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void Release(nint self);
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)][return: MarshalAs(UnmanagedType.I1)] private delegate bool SendImage(nint self, nint pixels, uint width, uint height, uint format, [MarshalAs(UnmanagedType.I1)] bool invert);

    public SpoutNativeSender()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Spout2 output requires Windows x64. Avatar preview remains available.");
        var path = SpoutSenderInstallation.Validate();
        try
        {
            library = NativeLibrary.Load(path);
            instance = Marshal.GetDelegateForFunctionPointer<Factory>(NativeLibrary.GetExport(library, "GetSpout"))();
            if (instance == 0) throw new InvalidOperationException("Spout2 could not create a sender.");
            if (!Method<CreateGl>(151)(instance, 0)) throw new InvalidOperationException("Spout2 could not initialize graphics. Update your graphics driver or disable Spout2.");
            Method<SetName>(0)(instance, "ContentCoHostStudio");
            Method<SetFormat>(1)(instance, 87); // DXGI_FORMAT_B8G8R8A8_UNORM
        }
        catch { Dispose(); throw; }
    }

    private T Method<T>(int slot) where T : Delegate
    {
        ObjectDisposedException.ThrowIf(instance == 0, this);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    }

    public void Send(nint bgraPixels)
    {
        if (!Method<SendImage>(5)(instance, bgraPixels, 512, 512, 0x80E1, false))
            throw new InvalidOperationException("Spout2 could not share the avatar frame. Check graphics drivers and use the same GPU for OBS and this application, or disable Spout2.");
    }

    public void Dispose()
    {
        if (instance != 0)
        {
            try { Method<ReleaseSender>(2)(instance, 0); Method<CloseGl>(152)(instance); Method<Release>(171)(instance); }
            finally { instance = 0; }
        }
        if (library != 0) { NativeLibrary.Free(library); library = 0; }
    }
}
