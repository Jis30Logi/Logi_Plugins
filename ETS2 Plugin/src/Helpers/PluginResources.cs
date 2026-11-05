namespace Loupedeck.ETS2Plugin
{
    using System;
    using System.IO;
    using System.Reflection;

    /// <summary>
    /// Helper class for loading plugin resources (SVG icons).
    /// </summary>
    internal static class PluginResources
    {
        private static Assembly _assembly;

        public static void Init(Assembly assembly)
        {
            _assembly = assembly;
        }

        // Finds the first resource file with the specified file name, and returns the full name.
        public static String FindFile(String fileName) => _assembly.FindFileOrThrow(fileName);

        // Reads content of the specified image file, and returns the file content as a bitmap image.
        public static BitmapImage ReadImage(String resourceName) => _assembly.ReadImage(FindFile(resourceName));
    }
}
