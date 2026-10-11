using System.Runtime.InteropServices;

namespace TurboManager.Agent.Security;

internal static partial class UnixFileOwnership
{
    [LibraryImport("libc", EntryPoint = "chown", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Chown(string path, int owner, int group);

    /// <summary>Changes only the group of <paramref name="path"/> (owner unchanged).</summary>
    public static void SetGroup(string path, int gid)
    {
        if (Chown(path, -1, gid) != 0)
        {
            throw new IOException($"chown {path} failed with errno {Marshal.GetLastPInvokeError()}");
        }
    }
}
