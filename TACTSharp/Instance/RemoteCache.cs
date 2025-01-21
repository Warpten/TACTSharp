using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

namespace TACTSharp.Instance
{
    public sealed partial class RemoteCache : LoggingEnabledBase<RemoteCache>
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

        public RemoteCache(string baseDirectory, string product, string region, ILoggerFactory? loggerFactory = null) : base(loggerFactory)
        {
            _product = product;
            _region = region;

            _patchClient = new HttpClient() {
                BaseAddress = new Uri($"http://{_region}.patch.battle.net:1119/{_product}/"),
            };

            (_servers, var stem) = InitializeServers();

            _baseDirectory = new DirectoryInfo(baseDirectory + '/' + stem);
            _baseDirectory.Create();

            LogInitializationComplete(Logger, _product, _region, _baseDirectory);
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Downloading `{ResourcePath}`.")]
        private static partial void LogDownloadStart(ILogger logger, string resourcePath);

        [LoggerMessage(Level = LogLevel.Trace, Message = "Attempting to retrieve `{ResourcePath}` from `{BaseAddress}`.")]
        private static partial void LogDownloadStartAttempt(ILogger logger, string resourcePath, Uri baseAddress);

        private Stream Download(string resourcePath)
        {
            using var @lock  = _servers.@lock.EnterScope();
            for (var i = 0; i < _servers.Clients.Length; ++i)
            {
                LogDownloadStartAttempt(Logger, resourcePath, _servers.Clients[i].BaseAddress!);

                var request = new HttpRequestMessage(HttpMethod.Get, resourcePath);
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

            if (!fileInfo.Exists)
            {
                var remotePath = Path.Combine(resourceType.RemotePath, fileName[0..2], fileName[2..4], fileName);
                LogDownloadStart(Logger, remotePath);

                using var loggingScope = BeginTimedScope(
                    timeSpent => Logger.LogInformation("Downloaded `{ResourcePath}` ({Duration:c}).", remotePath, timeSpent)
                );

                fileInfo.Directory!.Create();

                using (var fileStream = fileInfo.OpenWrite())
                    Download(remotePath).CopyTo(fileStream);

                fileInfo.Refresh();
                if (fileInfo.Length == 0)
                {
                    fileInfo.Delete();
                    fileInfo.Refresh();
                }

                return new Resource(fileInfo, offset, length);
            }

            return new Resource(fileInfo, offset, length);
        }

        public Resource OpenResource(ResourceType resourceType, ReadOnlySpan<byte> encodingKey, string fileName, long offset = 0, long length = 0, bool validate = false)
        {
            var resource = OpenResource(resourceType, fileName, offset, length);
            if (resource.Exists && validate)
            {
                var checksum = resource.OpenMemoryMapped(MD5.HashData, static () => []);
                if (encodingKey.SequenceEqual(checksum))
                    return resource;

                if (Logger.IsEnabled(LogLevel.Error))
                    LogChecksumError(Logger, resource.FileInfo.FullName, Convert.ToHexStringLower(encodingKey), Convert.ToHexStringLower(checksum));

                resource.FileInfo.Delete();
                resource.FileInfo.Refresh();
            }

            return resource;
        }

        public Resource OpenResource(ResourceType resourceType, ReadOnlySpan<byte> encodingKey, bool validate = false)
        {
            var fileName = Convert.ToHexStringLower(encodingKey);
            return OpenResource(resourceType, encodingKey, fileName, validate: validate);
        }

        public (string Build, string CDN) QueryLatestVersions()
        {
            Logger.LogTrace("Querying versions from patch service.");

            var request = new HttpRequestMessage(HttpMethod.Get, $"versions");
            var response = _patchClient.Send(request);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType!.MediaType == "text/plain")
            {
                using MemoryStream memoryStream = new(8192);
                response.Content.ReadAsStream().CopyTo(memoryStream);

                ReadOnlySpan<byte> dataSpan = new(memoryStream.GetBuffer(), 0, (int)memoryStream.Position);

                return ConfigurationFile.ParseOne<(string, string)?>(dataSpan, (fields, data) =>
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
            Logger.LogTrace("Querying CDNs from patch server.");

            var servers = new List<HttpClient>();
            var stem = default(string);

            var request = new HttpRequestMessage(HttpMethod.Get, $"cdns");
            var response = _patchClient.Send(request);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType!.MediaType == "text/plain")
            {
                using MemoryStream memoryStream = new (8192);
                response.Content.ReadAsStream().CopyTo(memoryStream);

                ReadOnlySpan<byte> dataSpan = new (memoryStream.GetBuffer(), 0, (int) memoryStream.Position);

                servers.AddRange(ConfigurationFile.Parse<HttpClient>(dataSpan, (fields, data) =>
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
                    Logger.LogTrace("`{ServerName}` replied in less than {Ping} ms.", server.BaseAddress!.Host, response.RoundtripTime);

                    return (Server: server, Ping: response.RoundtripTime);
                }
                catch (PingException pingException)
                {
                    // The system does not support pings, pretend the server replied last.
                    if (pingException.InnerException is NotSupportedException)
                        return (Server: server, Ping: 1000L);

                    Logger.LogError(pingException, "An error occured while pinging `{ServerName}`.", server.BaseAddress!.Host);
                    return (Server: server, Ping: long.MaxValue);
                }
                catch (Exception)
                {
                    Logger.LogTrace("`{ServerName}` did not reply; discarding.", server.BaseAddress!.Host);
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

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Checksum mismatch when validating `{FileName}`: expected `{Expected}`, found `{Found}`.",
            SkipEnabledCheck = true)]
        private static partial void LogChecksumError(ILogger logger, string fileName, string expected, string found);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "(`{Product}`)(`{Region}`) CDN cache folder initialized to `{BaseDirectory}`.",
            SkipEnabledCheck = true)]
        private static partial void LogInitializationComplete(ILogger logger, string product, string region, DirectoryInfo baseDirectory);
    }
}
