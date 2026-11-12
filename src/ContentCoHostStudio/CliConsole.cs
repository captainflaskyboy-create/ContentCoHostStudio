using System.Runtime.InteropServices;
using System.Text;

namespace ContentCoHostStudio;

internal static class CliConsole
{
    public static void Initialize()
    {
        if (OperatingSystem.IsWindows())
        {
            uint[] kinds = [unchecked((uint)-10), unchecked((uint)-11), unchecked((uint)-12)];
            var inherited = kinds.Select(GetStdHandle).ToArray();
            // A GUI-subsystem executable can inherit pipes without having a console.
            // Preserve those handles when attaching for interactive Ctrl+C support.
            if (!AttachConsole(uint.MaxValue) && Marshal.GetLastWin32Error() != 5 && inherited.All(handle => !Valid(handle)))
                AllocConsole();
            for (int i = 0; i < kinds.Length; i++)
                if (Valid(inherited[i])) SetStdHandle(kinds[i], inherited[i]);
        }
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), true));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
    }
    private static bool Valid(nint handle) => handle != 0 && handle != -1;
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(uint kind);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetStdHandle(uint kind, nint handle);
}
