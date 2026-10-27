using System.IO;
using CUE4Parse.Compression;
using CUE4Parse.UE4.Lua.unluac;
using CUE4Parse_Conversion.Textures.BC;
using FModel.Settings;
using FModel.Framework;
using OodleDotNet;
using ZlibngDotNet;

namespace FModel.Automation;

internal static class InstalledCodecs
{
    public static void Initialize()
    {
        // Use the same profile data directory as the GUI without downloading or updating native tools.
        var directory = CacheManager.DataDirectory;
        var oodle = Path.Combine(directory, OodleHelper.OODLE_NAME_OLD);
        if (!File.Exists(oodle)) oodle = Path.Combine(directory, OodleHelper.OODLE_NAME_CURRENT);
        if (File.Exists(oodle)) OodleHelper.Initialize(new Oodle(oodle));
        var zlib = Path.Combine(directory, ZlibHelper.DLL_NAME);
        if (File.Exists(zlib)) ZlibHelper.Initialize(new Zlibng(zlib));
        var detex = Path.Combine(directory, DetexHelper.DLL_NAME);
        if (File.Exists(detex)) DetexHelper.Initialize(detex);
        var unluac = Path.Combine(directory, UnluacHelper.DllName);
        if (UserSettings.Default.DecompileLua && File.Exists(unluac)) UnluacHelper.Initialize(new Unluac(unluac));
    }
}
