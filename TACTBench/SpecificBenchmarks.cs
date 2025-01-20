using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using TACTSharp;

namespace TACTBench
{
    [MemoryDiagnoser]
    public class SpecificBenchmarks
    {/*
        private BuildInstance _build;

        [GlobalSetup]
        public async Task SpecificSetup() {
            var settings = new Settings();

            var versions = await CDN.GetProductVersions("wow");
            foreach (var line in versions.Split('\n'))
            {
                if (!line.StartsWith("us|"))
                    continue;

                var splitLine = line.Split('|');

                settings.BuildConfig ??= splitLine[1];
                settings.CDNConfig ??= splitLine[2];
                break;
            }

            var build = new BuildInstance(Settings.BuildConfig!, Settings.CDNConfig!);
            await build.Load();
        }

        [IterationSetup]
        public void PrepareBuild() => _build.Encoding!.ClearSpecs();

        [Benchmark]
        public (string, ulong)? BenchmarkEncodingSpecs() {
            ref readonly var fileEntry = ref _build.Root!.FindFileDataID(1349477);
            Debug.Assert(!Unsafe.IsNullRef(in fileEntry));

            return _build.Encoding!.FindSpec(fileEntry.ContentKey);
        }*/
    }

}