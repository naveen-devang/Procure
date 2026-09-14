using System;
using System.Net.Http;
using System.Net.Sockets;

namespace Procure.Utilities
{
    /// <summary>
    /// What Settings > Updates says after a check, kept short and plain. The release list the check reads
    /// is sometimes slow or briefly unavailable (a 504 after 11 s, fine a minute later); the raw exception
    /// text meant nothing to a colleague.
    /// </summary>
    public static class UpdateCheckMessages
    {
        /// <summary>How long a check may run before it is abandoned with a message.</summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        public static string UpToDate(string version) => $"You're up to date ({version}).";

        public static string Failure(Exception ex)
        {
            var http = Find<HttpRequestException>(ex);
            if ((http is not null && http.StatusCode is null) || Find<SocketException>(ex) is not null)
                return "No internet connection. Check your connection and try again.";

            return "Couldn't check for updates right now. Please try again later.";
        }

        private static T? Find<T>(Exception? ex) where T : Exception
        {
            for (var e = ex; e is not null; e = e.InnerException)
                if (e is T match) return match;
            return null;
        }
    }
}
