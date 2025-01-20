using System.Diagnostics;

namespace TACTSharp
{
    public class Settings
    {
        public string Region { get; set; } = "us";
        public string Product { get; set; } = "wow";
        public WarptenRoot.LocaleFlags Locale  { get; set; } = WarptenRoot.LocaleFlags.enUS;

        /// <summary>
        /// Path to the game's installation path.
        /// </summary>
        public string? BaseDirectory { get; set; }

        /// <summary>
        /// Path to a folder containing cached data.
        /// </summary>
        public string CacheDirectory { get; set; } = "./cache";
    }
}
