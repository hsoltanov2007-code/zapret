using System.Diagnostics;
namespace Northpass.Engine;

public static class EngineProcessEnvironment
{
    public static void Harden(ProcessStartInfo info)
    {
        info.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (string key in new[] { "CYGWIN", "LD_PRELOAD", "LD_LIBRARY_PATH", "LUA_PATH", "LUA_CPATH", "LUA_INIT", "LUA_PATH_5_1", "LUA_CPATH_5_1", "__COMPAT_LAYER" })
            info.Environment.Remove(key);
    }
}
