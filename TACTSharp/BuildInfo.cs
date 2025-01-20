﻿using System.Diagnostics;

namespace TACTSharp
{
    public class BuildInfo
    {
        public struct AvailableBuild
        {
            public string Branch;
            public string BuildConfig;
            public string CDNConfig;
            public string CDNPath;
            public string KeyRing;
            public string Version;
            public string Product;
            public string Folder;
            public string Armadillo;
        }

        public readonly AvailableBuild[] Entries = [];

        public BuildInfo(Settings settings, string path)
        {
            Debug.Assert(settings.BaseDirectory != null);

            var headerMap = new Dictionary<string, byte>();

            var folderMap = new Dictionary<string, string>();
            foreach (var flavorFile in Directory.GetFiles(settings.BaseDirectory, ".flavor.info", SearchOption.AllDirectories))
            {
                var flavorLines = File.ReadAllLines(flavorFile);
                if (flavorLines.Length < 2)
                    continue;

                folderMap.Add(flavorLines[1], Path.GetFileName(Path.GetDirectoryName(flavorFile)!));
            }

            var entries = new List<AvailableBuild>();
            foreach (var line in File.ReadAllLines(path))
            {
                var splitLine = line.Split("|");
                if (splitLine[0] == "Branch!STRING:0")
                {
                    foreach (var header in splitLine)
                        headerMap.Add(header.Split("!")[0], (byte)Array.IndexOf(splitLine, header));

                    continue;
                }

                var availableBuild = new AvailableBuild
                {
                    BuildConfig = splitLine[headerMap["Build Key"]],
                    CDNConfig = splitLine[headerMap["CDN Key"]],
                    CDNPath = splitLine[headerMap["CDN Path"]],
                    Version = splitLine[headerMap["Version"]],
                    Armadillo = splitLine[headerMap["Armadillo"]],
                    Product = splitLine[headerMap["Product"]]
                };

                if (headerMap.TryGetValue("KeyRing", out byte keyRing))
                    availableBuild.KeyRing = splitLine[keyRing];

                // if (headerMap.TryGetValue("CDN Hosts", out byte cdnHosts))
                //     CDN.SetCDNs(splitLine[cdnHosts].Split(' '));

                if (folderMap.TryGetValue(availableBuild.Product, out var folder))
                    availableBuild.Folder = folder;
                else
                    Console.WriteLine("No flavor found matching " + availableBuild.Product);

                entries.Add(availableBuild);
            }

            Entries = [.. entries];
        }
    }
}
