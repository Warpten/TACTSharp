using System.Diagnostics;

namespace TACTSharp
{
    public class Identifier(string value, IdentifierKind kind)
    {
        public readonly IdentifierKind Kind = kind;
        public readonly string Value = value;

        public Config Open()
        {
            return Kind switch {
                IdentifierKind.Local => new Config(Value, true),
                IdentifierKind.Remote => new Config(Value, false),
                _ => throw new UnreachableException(),
            };
        }
    }

    public enum IdentifierKind {
        Local,
        Remote
    }

    public class Settings
    {
        public string Region { get; set; } = "us";
        public string Product { get; set; } = "wow";
        public WarptenRoot.LocaleFlags Locale  { get; set; } = WarptenRoot.LocaleFlags.enUS;
        public string? BaseDir { get; set; }
        public Identifier? BuildConfig { get; set; }
        public Identifier? CDNConfig { get; set; }
    }
}
