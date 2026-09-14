using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Procure.Utilities
{
    /// <summary>
    /// What Settings > Updates says after a check. The check reads Procure's release list from GitHub's
    /// API, which is sometimes slow or briefly down on GitHub's side (it answered "504 Gateway Time-out"
    /// after 11 s, then worked a minute later) - the raw exception text meant nothing to a colleague.
    /// </summary>
    public static class UpdateCheckMessages
    {
        /// <summary>How long a check may run before it is abandoned with a message.</summary>
        public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

        public static string UpToDate(string version) => $"You're up to date - {version} is the latest version.";

        public static string Failure(Exception ex)
        {
            if (Find<TimeoutException>(ex) is not null || Find<OperationCanceledException>(ex) is not null)
                return $"GitHub didn't respond within {(int)Timeout.TotalSeconds} seconds, so the update check stopped. Try again in a minute.";

            if (Find<HttpRequestException>(ex) is { } http)
            {
                var code = http.StatusCode;
                if (code is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    return "GitHub is limiting update checks from this network for now. Try again in an hour.";
                if (code is { } c && (int)c >= 500)
                    return "GitHub's update service isn't responding right now - the problem is on GitHub's side. Try again in a few minutes.";
                if (code is null)
                    return "Couldn't reach GitHub. Check the internet connection and try again.";
            }

            if (Find<SocketException>(ex) is not null)
                return "Couldn't reach GitHub. Check the internet connection and try again.";

            return "Couldn't check for updates. Try again later.";
        }

        private static T? Find<T>(Exception? ex) where T : Exception
        {
            for (var e = ex; e is not null; e = e.InnerException)
                if (e is T match) return match;
            return null;
        }
    }
}
