using System;
using System.IO;

namespace Procure;

/// <summary>
/// The app's per-user data directory. During the MAUI era this was
/// <c>Microsoft.Maui.Storage.FileSystem.AppDataDirectory</c>, which resolved to the
/// path below. It is hard-coded here so an installed copy keeps finding its existing
/// database, settings and update-state files after the move off MAUI - changing it
/// would look like total data loss to every colleague. See MIGRATION-PLAN.md Risk 1.
/// </summary>
public static class AppPaths
{
    public static string AppData { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "User Name", "com.companyname.procure", "Data");

    static AppPaths() => Directory.CreateDirectory(AppData);
}
