using System.Text;

namespace TACTSharp
{
    public class Config
    {
        public static Config? FromFile(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Exists)
                return FromResource(new Resource(filePath, 0, fileInfo.Length));

            return null;
        }

        public static Config? FromHash(CDN cdn, string hash)
        {
            var resource = cdn.OpenOrDownload(CDN.FileType.Config, hash, 0);
            return FromResource(resource);
        }

        /// <summary>
        /// Tries to load and parse the configuration file that matches the provided hash.
        /// 
        /// The order of lookups for this file goes:
        /// <list type="number">
        ///     <item>The game's files as located with <see cref="Settings.BaseDirectory" />.</item>
        ///     <item>The cached files within the directory <see cref="Settings.CacheDirectory"/>.</item>
        ///     <item>Any of the CDNs in the </item>
        /// </list>
        /// </summary>
        /// <param name="cdn"></param>
        /// <param name="hash"></param>
        /// <returns></returns>
        public static Config? FromHash(CDN cdn, ReadOnlySpan<byte> hash)
        {
            var resource = cdn.OpenOrDownload(CDN.FileType.Config, hash, 0);
            return FromResource(resource);
        }

        public static Config? FromResource(Resource resource)
        {
            if (resource.Exists)
                return resource.OpenMap(data => new Config(data));

            return null;
        }

        public static Config? FromDisk(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Exists)
                return FromResource(new Resource(fileInfo.FullName, 0, fileInfo.Length));

            return null;
        }

        private Dictionary<string, string[]> _values = [];

        private Config(ReadOnlySpan<byte> rawData)
        {
            foreach (var lineRange in rawData.Split((byte) '\n'))
            {
                var line = rawData[lineRange];
                if (line.Length == 0 || line[0] == (byte)'#')
                    continue;

                var enumerator = line.SplitAny([ (byte) ' ', (byte) '=' ]);
                while (true)
                {
                    if (!enumerator.MoveNext())
                        break;

                    var key = enumerator.Current;

                    var values = new List<string>();
                    while (enumerator.MoveNext())
                    {
                        var value = enumerator.Current;
                        if (value.Start.Value == value.End.Value)
                            continue;

                        values.Add(Encoding.UTF8.GetString(line[value]));
                    }

                    _values.Add(Encoding.UTF8.GetString(line[key]), [.. values]);
                }
            }
        }

        public string[] this[string key] => _values[key];

        public bool TryGetValue(string key, out string[] value)
        {
            if (!_values.TryGetValue(key, out value!))
            {
                value = [];
                return false;
            }

            return true;
        }
    }
}
