// Phase 0 spike only. DatabaseConstants.cs (linked from the MAUI project) references
// Microsoft.Maui.Storage.FileSystem and Preferences on its *fallback* path - the one
// taken only when PROCURE_DB_DIR is unset. The spike always sets PROCURE_DB_DIR, so
// this code never runs; it only has to compile. Phase 1 replaces the real call with
// an AppPaths helper in Procure.Core and deletes this file.

using System;
using System.IO;

namespace Microsoft.Maui.Storage
{
    internal static class FileSystem
    {
        public static string AppDataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Procure.Spike");
    }

    internal sealed class PreferencesShim
    {
        public string Get(string key, string defaultValue) => defaultValue;
        public void Set(string key, string value) { }
    }

    internal static class Preferences
    {
        public static PreferencesShim Default { get; } = new();
    }
}
