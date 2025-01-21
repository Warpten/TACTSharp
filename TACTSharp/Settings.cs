using Microsoft.Extensions.Logging;

namespace TACTSharp
{
    public class Settings
    {
        public string Region { get; set; } = "us";
        public string Product { get; set; } = "wow";
        public RootInstance.LocaleFlags Locale  { get; set; } = RootInstance.LocaleFlags.enUS;

        /// <summary>
        /// Path to the game's installation path.
        /// </summary>
        public string? BaseDirectory { get; set; }

        /// <summary>
        /// Path to a folder containing cached data.
        /// </summary>
        public string CacheDirectory { get; set; } = "./cache";

        /// <summary>
        /// A logging factory to carry around.
        /// </summary>
        public ILoggerFactory? LoggerFactory { get; set; }
    }
}
