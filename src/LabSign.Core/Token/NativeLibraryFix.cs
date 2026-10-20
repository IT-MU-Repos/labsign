using System.Reflection;
using System.Runtime.InteropServices;
using Net.Pkcs11Interop.HighLevelAPI;

namespace LabSign.Core.Token;

/// <summary>
/// Pkcs11Interop 5.x loads PKCS#11 modules through <c>dlopen</c> from a library it names
/// <c>"libdl"</c>. glibc 2.34+ (Ubuntu 22.04, Debian 12 and later) ships only
/// <c>libdl.so.2</c> (dlopen itself now lives in libc), so on those systems LabSign could load
/// no token driver at all, SafeNet's included. Resolve the name ourselves.
/// </summary>
internal static class NativeLibraryFix
{
    private static int _applied;

    public static void Apply()
    {
        if (!OperatingSystem.IsLinux() || Interlocked.Exchange(ref _applied, 1) == 1) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Pkcs11InteropFactories).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Someone set a resolver already; theirs stands.
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != "libdl") return IntPtr.Zero;
        foreach (var candidate in new[] { "libdl.so.2", "libc.so.6", "libdl.so" })
        {
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        }
        return IntPtr.Zero;
    }
}
