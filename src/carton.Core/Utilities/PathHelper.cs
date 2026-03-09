using System;
using System.IO;

namespace carton.Core.Utilities;

public static class PathHelper
{
    public const string PortableMarkerFileName = ".carton_portable_data";

    public static string GetAppDataPath()
    {
        var exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var markerPath = Path.Combine(exeDirectory, PortableMarkerFileName);

        if (File.Exists(markerPath))
        {
            return exeDirectory;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Carton");
    }
}
