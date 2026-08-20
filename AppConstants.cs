namespace Procure
{
    public static class AppConstants
    {
        /// <summary>
        /// The GitHub repository used for checking releases and automatic updates.
        /// Update this with your GitHub username/repository name when ready (e.g. "your-username/Procure").
        /// </summary>
        public const string GitHubRepository = "Procure/Procure";

        /// <summary>
        /// Sidebar layout dimensions and responsive breakpoints (in DIPs).
        /// </summary>
        public const double SidebarExpandedWidth = 240.0;
        public const double SidebarCompactWidth = 68.0;
        public const double ResponsiveCollapseBreakpoint = 1024.0;

        /// <summary>
        /// Standard global currency codes supported by the application.
        /// </summary>
        public static readonly string[] SupportedCurrencies = new[]
        {
            "AED", "USD", "EUR", "GBP", "SAR", "QAR", "OMR", "KWD", "BHD", "INR", "SGD", "CAD", "AUD", "JPY", "CNY"
        };
    }
}
