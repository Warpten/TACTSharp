using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace TACTSharp.Instance
{
    public class RemoteCache
    {
        private readonly struct Servers(HttpClient[] clients)
        {
            public readonly HttpClient[] Clients = clients;
            public readonly Lock @lock = new();
        }
        private Servers _servers;
        private readonly HttpClient _patchClient;

        private readonly DirectoryInfo _baseDirectory;
        private readonly string _product;
        private readonly string _region;

        public RemoteCache(string baseDirectory, string product, string region)
        {
            _product = product;
            _region = region;

            _patchClient = new HttpClient() {
                BaseAddress = new Uri($"http://{_region}.patch.battle.net:1119/{_product}/"),
            };

            (_servers, var stem) = InitializeServers();

            _baseDirectory = new DirectoryInfo(baseDirectory + '/' + stem);
            _baseDirectory.Create();
        }

        private Stream Download(string resourcePath)
        {
            using var @lock  = _servers.@lock.EnterScope();
            for (var i = 0; i < _servers.Clients.Length; ++i)
            {
                var request = new HttpRequestMessage(HttpMethod.Head, resourcePath);
                var response = _servers.Clients[i].Send(request);
                if (!response.IsSuccessStatusCode)
                    continue;

                return response.Content.ReadAsStream();
            }

            return Stream.Null;
        }

        public Resource OpenResource(ResourceType resourceType, string fileName, long offset = 0, long length = 0)
        {
            var localPath = Path.Combine(_baseDirectory.FullName, resourceType.LocalPath, fileName[0..2], fileName[2..4], fileName);

            var fileInfo = new FileInfo(localPath);
            var resource = new Resource(fileInfo, offset, length);

            if (!fileInfo.Exists)
            {
                fileInfo.Directory!.Create();

                var remotePath = Path.Combine(resourceType.RemotePath, fileName[0..2], fileName[2..4], fileName);
                using (var fileStream = fileInfo.Create())
                    Download(remotePath).CopyTo(fileStream);

                fileInfo.Refresh();
            }

            return resource;
        }

        public Resource OpenResource(ResourceType resourceType, ReadOnlySpan<byte> encodingKey, string fileName, long offset = 0, long length = 0)
        {
            var resource = OpenResource(resourceType, fileName, offset, length);
            if (resource.Exists)
            {
                var checksum = resource.OpenMemoryMapped(MD5.HashData);
                if (encodingKey.SequenceEqual(checksum))
                    return resource;
                
                resource.FileInfo.Delete();
                resource.FileInfo.Refresh();
            }

            return resource;
        }

        public Resource OpenResource(ResourceType resourceType, ReadOnlySpan<byte> encodingKey)
        {
            var fileName = Convert.ToHexStringLower(encodingKey);
            return OpenResource(resourceType, encodingKey, fileName);
        }

        public (string Build, string CDN) QueryLatestVersions()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"versions");
            var response = _patchClient.Send(request);
            if (response.IsSuccessStatusCode)
            {
                using MemoryStream memoryStream = new(8192);
                response.Content.ReadAsStream().CopyTo(memoryStream);

                ReadOnlySpan<byte> dataSpan = new(memoryStream.GetBuffer(), 0, (int)memoryStream.Position);

                return ConfigurationFile<(string, string)?>.ParseOne(dataSpan, (fields, data) =>
                {
                    var region = Encoding.UTF8.GetString(data[fields[0].Value])!;
                    if (region != _region)
                        return null;

                    var buildConfig = Encoding.UTF8.GetString(data[fields[1].Value]);
                    var cdnConfig = Encoding.UTF8.GetString(data[fields[2].Value]);

                    return (buildConfig, cdnConfig);
                }) ?? (string.Empty, string.Empty);
            }

            return (string.Empty, string.Empty);
        }

        private (Servers, string) InitializeServers()
        {
            var servers = new List<HttpClient>();
            var stem = default(string);

            var request = new HttpRequestMessage(HttpMethod.Get, $"cdns");
            var response = _patchClient.Send(request);
            if (response.IsSuccessStatusCode)
            {
                using MemoryStream memoryStream = new (8192);
                response.Content.ReadAsStream().CopyTo(memoryStream);

                ReadOnlySpan<byte> dataSpan = new (memoryStream.GetBuffer(), 0, (int) memoryStream.Position);

                servers.AddRange(ConfigurationFile<HttpClient>.Parse(dataSpan, (fields, data) =>
                {
                    var region = Encoding.UTF8.GetString(data[fields[0].Value])!;
                    if (region != _region)
                        return [];

                    stem = Encoding.UTF8.GetString(data[fields[1].Value]);
                    var servers = Encoding.UTF8.GetString(data[fields[2].Value]);

                    return [.. servers.Split(' ', StringSplitOptions.TrimEntries)
                        .Select(server => new HttpClient() {
                            BaseAddress = new Uri(server.StartsWith("http")
                                ? $"{server}/{stem}/"
                                : $"http://{server}/{stem}/")
                        })];
                }));
            }

            // Now that we have a set of eligible servers, ping them and sort them.
            var pingTasks = servers.Select(server => Task.Run(async () => {
                try
                {
                    var response = await new Ping().SendPingAsync(server.BaseAddress!.Host, 400);

                    return (Server: server, Ping: response.RoundtripTime);
                }
                catch (PingException pingException)
                {
                    // The system does not support pings, pretend the server replied last.
                    if (pingException.InnerException is NotSupportedException)
                        return (Server: server, Ping: 1000L);

                    return (Server: server, Ping: long.MaxValue);
                }
                catch (Exception)
                {
                    return (Server: server, Ping: long.MaxValue);
                }
            })).ToArray();

            var cts = new CancellationTokenSource(1000);
            var sortedServers = Task.WhenEach(pingTasks)
                .ToBlockingEnumerable(cts.Token)
                .Select(task => task.Result)
                .Where(pingInfo => pingInfo.Ping != long.MaxValue)
                .OrderBy(pingInfo => pingInfo.Ping)
                .Select(pingInfo => pingInfo.Server)
                .ToList();

            if (stem != null)
                sortedServers.Add(new HttpClient() {
                    BaseAddress = new Uri($"http://archive.wow.tools/{stem}/")
                });

            return (new Servers([.. sortedServers]), stem ?? "unknown");
        }
    }
}
