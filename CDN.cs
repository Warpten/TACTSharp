﻿namespace TACTIndexTestCSharp
{
    public static class CDN
    {
        private static readonly HttpClient Client = new();

        private static readonly List<string> CDNServers = [];

        static CDN()
        {
            if (!Directory.Exists("cache"))
                Directory.CreateDirectory("cache");

            var cdnsFile = Client.GetStringAsync("http://us.patch.battle.net:1119/wow/cdns").Result;

            foreach (var line in cdnsFile.Split('\n'))
            {
                if (line.Length == 0)
                    continue;
                
                if(!line.StartsWith("us|"))
                    continue;

                var splitLine = line.Split('|');
                if (splitLine.Length < 2)
                    continue;

                CDNServers.AddRange(splitLine[2].Trim().Split(' '));
            }

            CDNServers.Add("archive.wow.tools");

            var pingTasks = new List<Task<(string server, long ping)>>();
            foreach (var server in CDNServers)
            {
                pingTasks.Add(Task.Run(() =>
                {
                    var ping = new System.Net.NetworkInformation.Ping().Send(server).RoundtripTime;
                    return (server, ping);
                }));
            }

            var pings = Task.WhenAll(pingTasks).Result;

            CDNServers = pings.OrderBy(p => p.ping).Select(p => p.server).ToList();
        }

        private static async Task<byte[]> DownloadFile(string tprDir, string type, string hash, ulong size = 0)
        {
            var cachePath = Path.Combine("cache", tprDir, type, hash);
            if (File.Exists(cachePath))
            {
                if (size > 0 && (ulong)new FileInfo(cachePath).Length != size)
                    File.Delete(cachePath);
                else
                    return File.ReadAllBytes(cachePath);
            }

            var url = $"http://{CDNServers[0]}/tpr/{tprDir}/{type}/{hash[0]}{hash[1]}/{hash[2]}{hash[3]}/{hash}";

            Console.WriteLine("Downloading " + url);

            var response = await Client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                throw new Exception("Failed to download " + url);

            var data = await response.Content.ReadAsByteArrayAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(cachePath, data);
            return data;
        }

        private static async Task<byte[]> DownloadFileFromArchive(string eKey, string tprDir, string archive, int offset, int size)
        {
            var cachePath = Path.Combine("cache", tprDir, "data", eKey);
            if (File.Exists(cachePath))
            {
                if (new FileInfo(cachePath).Length == size)
                    return File.ReadAllBytes(cachePath);
                else
                    File.Delete(cachePath);
            }

            var url = $"http://{CDNServers[0]}/tpr/{tprDir}/data/{archive[0]}{archive[1]}/{archive[2]}{archive[3]}/{archive}";

            Console.WriteLine("Downloading file " + eKey + " from archive " + archive + " at offset " + offset + " with size " + size);

            var request = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Headers =
                {
                    Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + size - 1)
                }
            };

            var response = await Client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
                throw new Exception("Failed to download " + url);

            var data = await response.Content.ReadAsByteArrayAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllBytes(cachePath, data);
            return data;
        }

        public static async Task<byte[]> GetFile(string tprDir, string type, string hash, ulong compressedSize = 0, ulong decompressedSize = 0, bool decoded = false)
        {
            var data = await DownloadFile(tprDir, type, hash, compressedSize);
            if (!decoded)
                return data;
            else
                return BLTE.Decode(data, decompressedSize);
        }

        public static async Task<byte[]> GetFileFromArchive(string eKey, string tprDir, string archive, int offset, int length, ulong decompressedSize = 0, bool decoded = false)
        {
            var data = await DownloadFileFromArchive(eKey, tprDir, archive, offset, length);
            if (!decoded)
                return data;
            else
                return BLTE.Decode(data, decompressedSize);
        }
    }
}
