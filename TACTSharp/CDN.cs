using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Reflection;

namespace TACTSharp
{
    public sealed class CDN
    {
        public enum FileType
        {
            Config,
            Index,
            Data
        }
        private static string[] FILE_TYPES = ["config", "data", "data"];

        public delegate T Parser<T>(ReadOnlySpan<byte> data);

        private readonly string? _baseDirectory;
        private readonly string _cacheDirectory;
        private readonly string _region;
        private readonly string _product;

        private readonly struct Servers(HttpClient[] clients, string stem)
        {
            public readonly List<HttpClient> Clients = [.. clients];
            public readonly string Stem = stem;
            public readonly Lock @lock = new();
        }

        private Servers _servers;
        private readonly HttpClient _patchClient;

        private readonly CASCIndexInstance[] _localIndices;

        private static readonly ConcurrentDictionary<string, Lock> _fileLocks = [];
        private static readonly Lock cdnLock = new();

        // TODO: Memory mapped cache file access?
        // TODO: Product is build-specific so that might not be good to have statically in Settings/used below
        public CDN(Settings settings)
        {
            _baseDirectory = settings.BaseDirectory;
            _cacheDirectory = settings.CacheDirectory;

            _product = settings.Product;
            _region = settings.Region;

            _patchClient = new HttpClient() {
                BaseAddress = new Uri($"http://{_region}.patch.battle.net:1119/"),
            };

            _servers = LoadServers();
            _localIndices = LoadLocalIndices();
        }

        public (string BuildConfig, string CDNConfig)? QueryVersions(Settings settings)
        {
            var response = _patchClient.Send(new HttpRequestMessage(HttpMethod.Get, $"/{_product}/versions"));
            if (!response.IsSuccessStatusCode)
                return null;

            using var reader = new StreamReader(response.Content.ReadAsStream());

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.AsSpan().StartsWith(settings.Region) && line[settings.Region.Length] == '|')
                {
                    var ranges = GC.AllocateUninitializedArray<Range>(4);
                    var tokenCount = line.AsSpan().Split(ranges, '|', StringSplitOptions.RemoveEmptyEntries);
                    if (tokenCount >= 3)
                        return (line[ranges[1]], line[ranges[2]]);
                }
            }

            return null;
        }

        private Servers LoadServers()
        {
            var servers = new List<HttpClient>();
            var stem = $"/tpr/{_product}"; // By default

            var request = new HttpRequestMessage(HttpMethod.Get, $"/{_product}/cdns");
            var response = _patchClient.Send(request);
            if (response.IsSuccessStatusCode)
            {
                using var reader = new StreamReader(response.Content.ReadAsStream());

                string? line = null;
                while ((line = reader.ReadLine()) != null)
                {
                    // Name|Path|Hosts|Servers|ConfigPath
                    if (line.Length == 0)
                        continue;

                    var lineSpan = line.AsSpan();

                    // Collect at most 3 ranges, we don't need more.
                    var tokenRanges = GC.AllocateUninitializedArray<Range>(4);
                    var lineTokens = lineSpan.Split(tokenRanges, '|', StringSplitOptions.TrimEntries);

                    // If less than 3 tokens found, or if region does not match, continue to next line.
                    if (lineTokens < 3 || !lineSpan[tokenRanges[0]].SequenceEqual(_region))
                        continue;

                    // Register clients
                    stem = lineSpan[tokenRanges[1]].ToString();
                    var serverNames = lineSpan[tokenRanges[2]];
                    foreach (var serverNameRange in serverNames.SplitAny(' '))
                    {
                        var serverName = serverNames[serverNameRange];

                        var httpClient = new HttpClient()
                        {
                            BaseAddress = serverName.StartsWith("http://") || serverName.StartsWith("https://")
                                ? new Uri(serverName.ToString())
                                : new Uri($"https://{serverName}/")
                        };
                        servers.Add(httpClient);
                    }
                    break;
                }
            }

            // Kakapos unite
            servers.Add(new HttpClient() {
                BaseAddress = new Uri($"https://archive.wow.tools/")
            });

            // Create ping tasks for each client.
            var pingTasks = servers.Select(server => Task.Run(async () => {
                try 
                {
                    var response = await new Ping().SendPingAsync(server.BaseAddress!.Host, 400);

                    return (Server: server, Ping: response.RoundtripTime);
                }
                catch (Exception)
                {
                    return (Server: server, Ping: long.MaxValue);
                }
            })).ToArray();

            // Add a 1s overall timeout and collect all pings, sorting and mapping to the actual clients.
            // Servers that exceed the total timeout will be the last to be used.
            var cts = new CancellationTokenSource(1000);
            return new Servers(Task.WhenEach(pingTasks)
                .ToBlockingEnumerable(cts.Token)
                .Select(task => task.Result)
                .Where(pingInfo => pingInfo.Ping != long.MaxValue)
                .OrderBy(pingInfo => pingInfo.Ping)
                .Select(pingInfo => pingInfo.Server)
                .ToArray(), stem!);
        }

        private CASCIndexInstance[] LoadLocalIndices()
        {
            if (_baseDirectory == null)
                return [];

            var dataDir = Path.Combine(_baseDirectory, "Data/data");
            if (!Directory.Exists(dataDir))
                return [];

            var indexFiles = Directory.GetFiles(dataDir, "*.idx");
            var indices = new CASCIndexInstance[indexFiles.Length];

            foreach (var indexFile in indexFiles)
            {
                if (indexFile.Contains("tempfile"))
                    continue;

                // Consider avoiding this allocation as well and just using backwards indexing to find the bucket marker.
                var fileName = Path.GetFileNameWithoutExtension(indexFile);

                var indexBucket = Convert.FromHexString(fileName.AsSpan()[0..2])[0];
                indices[indexBucket] = new CASCIndexInstance(indexFile);
            }

            return indices;
        }

        /// <summary>
        /// Synchronously opens a network stream to the specific file. Returns an empty stream if
        /// the file could not be found on any CDN.
        /// </summary>
        /// <param name="fileName">The name of the file to open.</param>
        /// <param name="expectedLength">The expected size of the file. A size of zero disables size checks.</param>
        /// <returns>A stream over the network resource, or an empty stream if the file could not be found.</returns>
        public Stream OpenRemote(FileType fileType, string fileName, long expectedLength = 0)
        {
            var requestUri = $"/{_servers.Stem}/{FILE_TYPES[(int) fileType]}/{fileName[0..2]}/{fileName[2..4]}/{fileName}";

            for (var i = 0; i < _servers.Clients.Count; ++i)
            {
                var client = _servers.Clients[i];

                try
                {
                    HttpResponseMessage? httpResponse;

                    // Send a HEAD request to check resource size, but ignore status code 405.
                    if (expectedLength != 0)
                    {
                        var headRequest = new HttpRequestMessage(HttpMethod.Head, requestUri);
                        httpResponse = client.Send(headRequest);
                        if (!httpResponse!.IsSuccessStatusCode)
                        {
                            if (httpResponse.StatusCode != HttpStatusCode.MethodNotAllowed
                                && httpResponse.Content.Headers.ContentLength != expectedLength)
                                continue;
                        }
                        else if (httpResponse!.Content.Headers.ContentLength != expectedLength)
                            continue;
                    }

                    var getRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
                    httpResponse = client.Send(getRequest);
                    if (!httpResponse!.IsSuccessStatusCode)
                        continue;

                    if (expectedLength != 0 && httpResponse.Content.Headers.ContentLength != expectedLength)
                        continue;

                    return httpResponse!.Content.ReadAsStream();
                }
                catch (Exception)
                {

                }
            }

            return Stream.Null;
        }

        /// <summary>
        /// Asynchronously opens a network stream to the specific file. Returns an empty stream if
        /// the file could not be found on any CDN.
        /// </summary>
        /// <param name="fileName">The name of the file to open.</param>
        /// <param name="expectedLength">The expected size of the file.</param>
        /// <returns>A stream over the network resource, or an empty stream if the file could not be found.</returns>
        public async Task<Stream> OpenRemoteAsync(FileType fileType, string fileName, long expectedLength = 0)
        {
            var requestUri = $"/{_servers.Stem}/{FILE_TYPES[(int)fileType]}/{fileName[0..2]}/{fileName[2..4]}/{fileName}";
            var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

            for (var i = 0; i < _servers.Clients.Count; ++i)
            {
                var client = _servers.Clients[i];

                try
                {
                    HttpResponseMessage? httpResponse;

                    // Send a HEAD request to check resource size, but ignore status code 405.
                    if (expectedLength != 0)
                    {
                        request.Method = HttpMethod.Head;
                        httpResponse = await client.SendAsync(request);
                        if (!httpResponse!.IsSuccessStatusCode)
                        {
                            if (httpResponse.StatusCode != HttpStatusCode.MethodNotAllowed
                                && httpResponse.Content.Headers.ContentLength != expectedLength)
                                continue;
                        }
                        else if (httpResponse!.Content.Headers.ContentLength != expectedLength)
                            continue;
                    }

                    request.Method = HttpMethod.Get;
                    httpResponse = await client.SendAsync(request);
                    if (!httpResponse!.IsSuccessStatusCode)
                        continue;
                    
                    if (expectedLength != 0 && httpResponse.Content.Headers.ContentLength != expectedLength)
                        continue;

                    return await httpResponse.Content.ReadAsStreamAsync();
                }
                catch (Exception)
                {

                }
            }

            return Stream.Null;
        }

        /// <summary>
        /// Attempts to obtain a file from the locally installed CASC file system.
        /// </summary>
        /// <param name="encodingKey">The encoding key of the file to look for.?</param>
        /// <param name="data">A buffer to be written to containing the data read from the file.</param>
        /// <returns></returns>
        public unsafe Resource OpenLocalFile(ReadOnlySpan<byte> encodingKey)
        {
            if (_localIndices.Length != 0 && _baseDirectory != null)
            {
                // Identify bucket.
                var indexBucket = encodingKey[0];
                for (var i = 1; i < encodingKey.Length / 2 + 1; ++i)
                    indexBucket ^= encodingKey[i];

                indexBucket = (byte)((indexBucket & 0xF) ^ (indexBucket >> 4));

                var targetIndex = _localIndices[indexBucket];
                var (archiveOffset, archiveSize, archiveIndex) = targetIndex.GetIndexInfo(encodingKey);

                if (archiveOffset != -1)
                {
                    // We will probably want to cache these but battle.net scares me so I'm not going to do it right now
                    var archivePath = Path.Combine(_baseDirectory, "Data/data", "data." + archiveIndex.ToString().PadLeft(3, '0'));

                    return new Resource(archivePath, archiveOffset, archiveSize);
                }
            }

            return Resource.Empty;
        }

        /// <summary>
        /// Opens or downloads a file given its encoding key.
        /// 
        /// <list type="number">
        ///     <item>The local game installation, if available.</item>
        ///     <item>The local cache directory, if available.</item>
        ///     <item>Any CDN server.</item>
        /// </list>
        /// </summary>
        /// <param name="fileType">The type of the file.</param>
        /// <param name="encodingKey">The file's encoding key.</param>
        /// <param name="expectedLength">The file's expected size.</param>
        /// <returns></returns>
        public Resource OpenOrDownload(FileType fileType, ReadOnlySpan<byte> encodingKey, long expectedLength)
        {
            var resource = OpenLocalFile(encodingKey);
            if (resource.Exists)
                return resource;

            return OpenOrDownload(fileType, Convert.ToHexStringLower(encodingKey), expectedLength);
        }

        /// <summary>
        /// Opens or downloads a file.
        /// 
        /// <list type="number">
        ///     <item>The local cache directory, if available.</item>
        ///     <item>Any CDN server.</item>
        /// </list>
        /// </summary>
        /// <param name="fileType">The type of this file.</param>
        /// <param name="fileName">The name of the file being looked for.</param>
        /// <param name="expectedLength">The expected size of the file.</param>
        /// <returns></returns>
        public Resource OpenOrDownload(FileType fileType, string fileName, long expectedLength)
        {
            var localResource = LocateCachedResource("tpr", _product, FILE_TYPES[(int) fileType], fileName);
            if (expectedLength == 0 ? localResource.Length != 0 : localResource.Length == expectedLength)
                return localResource;

            var fileInfo = new FileInfo(localResource.Path);
            lock (_fileLocks.GetOrAdd(localResource.Path, _ => new()))
            {
                if (!fileInfo.Exists)
                {
                    Directory.CreateDirectory(fileInfo.DirectoryName!);
                    var stream = OpenRemote(fileType, fileName, expectedLength);

                    using var fileStream = fileInfo.Create();
                    stream.CopyTo(fileStream);

                    fileInfo.Refresh();
                }

                if (!fileInfo.Exists)
                    return Resource.Empty;

                if (expectedLength != 0 && fileInfo.Length != expectedLength)
                {
                    fileInfo.Delete();
                    return Resource.Empty;
                }
            }

            return new Resource(localResource.Path, 0, (int) fileInfo.Length);
        }

        /// <summary>
        /// Obtains a <see cref="Resource"/> to a file available in the local cache.
        /// </summary>
        /// <param name="pathParameters"></param>
        /// <returns></returns>
        public Resource LocateCachedResource(params ReadOnlySpan<string> pathParameters)
        {
            var fileInfo = new FileInfo(Path.Combine([_cacheDirectory, .. pathParameters]));
            return new Resource(fileInfo.FullName, 0, fileInfo.Exists ? fileInfo.Length : 0);
        }

        /// <summary>
        /// Obtains a <see cref="Resource"/> to a file available in the game's base directory.
        /// 
        /// If this class was configured without such a directory specified, systematically returns
        /// <see cref="Resource.Empty"/>.
        /// </summary>
        /// <param name="pathParameters"></param>
        /// <returns></returns>
        public Resource LocateLocalResource(params ReadOnlySpan<string> pathParameters)
        {
            var fileInfo = new FileInfo(Path.Combine([_baseDirectory ?? "kakapos/united", .. pathParameters]));
            return new Resource(fileInfo.FullName, 0, fileInfo.Exists ? fileInfo.Length : 0);
        }

        /// <summary>
        /// Opens or downloads a file from an archive.
        /// </summary>
        /// <param name="encodingKey">An encoding key identifying the file.</param>
        /// <param name="archiveKey">Identifies the archive containing the file.</param>
        /// <param name="fileOffset">The offset at which this file is located.</param>
        /// <param name="fileSize">The (compressed) size of this file within the archive.</param>
        /// <returns>A stream to the file on disk, or an empty stream if the file could not be found.</returns>
        public Resource OpenOrDownload(ReadOnlySpan<byte> encodingKey, ReadOnlySpan<byte> archiveKey, FileType fileType, int fileOffset, int fileSize)
        {
            var resource = OpenLocalFile(encodingKey);
            if (resource.Exists && resource.Length == fileSize)
                return resource;

            var localFileName = Convert.ToHexStringLower(encodingKey);
            var archiveName = Convert.ToHexStringLower(archiveKey);

            var localFile = LocateCachedResource("tpr", _product, FILE_TYPES[(int) fileType], localFileName);
            if (localFile.Length == fileSize)
                return localFile;

            lock (_fileLocks.GetOrAdd(localFile.Path, _ => new()))
            {
                var fileInfo = new FileInfo(localFile.Path);
                if (!fileInfo.Exists)
                {
                    Directory.CreateDirectory(fileInfo.DirectoryName!);

                    var requestUri = $"/{_servers.Stem}/{FILE_TYPES[(int)fileType]}/{archiveName[0..2]}/{archiveName[2..4]}/{archiveName}";
                    var request = new HttpRequestMessage(HttpMethod.Get, requestUri)
                    {
                        Headers = { Range = new RangeHeaderValue(fileOffset, fileOffset + fileSize - 1) }
                    };

                    lock (_servers.@lock)
                    {
                        for (var i = 0; i < _servers.Clients.Count; ++i)
                        {
                            var client = _servers.Clients[i];

                            var response = client.Send(request);
                            if (response == null)
                                continue;

                            using var fileStream = fileInfo.Create();
                            response.Content.CopyTo(fileStream, null, CancellationToken.None);
                            break;
                        }
                    }

                    fileInfo.Refresh();
                }

                if (!fileInfo.Exists)
                    return Resource.Empty;

                Debug.Assert(fileInfo.Length == fileSize);
                return new Resource(localFileName, 0, fileSize);
            }
        }
    }
}
