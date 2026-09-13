using System;
using Microsoft.Data.Sqlite;

namespace Procure.Utilities
{
    /// <summary>Turns the database's own wording into something a person can act on.
    ///
    /// SQLite says "FOREIGN KEY constraint failed", which is accurate and useless: what actually
    /// happened is that the requisition the user is working on was deleted - by them in another
    /// window, or by a colleague sharing the database - while the board still had it on screen.
    /// Showing the raw message leaves them with nothing to do about it.
    ///
    /// One place, used by every host's error handler, so the translation cannot drift between them.
    /// </summary>
    public static class UserFacingError
    {
        public static string Describe(Exception ex)
        {
            var sqlite = Find<SqliteException>(ex);
            if (sqlite is not null)
            {
                var message = sqlite.Message ?? string.Empty;

                if (message.Contains("FOREIGN KEY constraint failed", StringComparison.OrdinalIgnoreCase))
                    return "That requisition no longer exists - it looks like it was deleted after this screen was opened. " +
                           "Refresh the board and try again.";

                if (message.Contains("database is locked", StringComparison.OrdinalIgnoreCase))
                    return "The database is busy with another change. Wait a moment and try again.";

                if (message.Contains("readonly", StringComparison.OrdinalIgnoreCase))
                    return "Procure cannot write to its database file. Check that the database folder " +
                           "(Settings > Database location) is reachable and not read-only.";

                if (message.Contains("disk", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("full", StringComparison.OrdinalIgnoreCase))
                    return "There is not enough disk space to save that change. Free some space and try again.";
            }

            return ex.Message;
        }

        private static T? Find<T>(Exception? ex) where T : Exception
        {
            for (var e = ex; e is not null; e = e.InnerException)
                if (e is T match) return match;
            return null;
        }
    }
}
