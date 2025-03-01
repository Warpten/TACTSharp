﻿namespace TACTSharp
{
    public static class Settings
    {
        public static string Region = "us";
        public static string Product = "wow";
        public static RootInstance.LocaleFlags Locale = RootInstance.LocaleFlags.enUS;
        public static string? BaseDir;
        public static string? BuildConfig;
        public static string? CDNConfig;
        public static string CacheDir = "cache";
        public static bool ListfileFallback = true;
        public static string ListfileURL = "https://github.com/wowdev/wow-listfile/releases/latest/download/community-listfile.csv";
    }
}
