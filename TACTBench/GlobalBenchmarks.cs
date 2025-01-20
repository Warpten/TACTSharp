using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using TACTSharp;

namespace TACTBench
{
    [MemoryDiagnoser]
    public class GlobalBenchmarks
    {
        /*
        [GlobalSetup]
        public async Task SpecificSetup() {
            var versions = await CDN.GetProductVersions("wow");
            foreach (var line in versions.Split('\n'))
            {
                if (!line.StartsWith("us|"))
                    continue;

                var splitLine = line.Split('|');

                Settings.BuildConfig ??= splitLine[1];
                Settings.CDNConfig ??= splitLine[2];
                break;
            }
        }
        
        [Benchmark]
        public async Task<EncodingInstance.Entry?> CompleteWorkflow() {
            var build = new BuildInstance(Settings.BuildConfig!, Settings.CDNConfig!);
            await build.Load();

            if (build.Encoding == null || build.Root == null || build.Install == null || build.GroupIndex == null)
                throw new Exception("Failed to load build");
            
            ref readonly var fileEntry = ref build.Root.FindFileDataID(1349477);
            Debug.Assert(!Unsafe.IsNullRef(in fileEntry));

            build.Encoding.FindSpec(fileEntry.ContentKey);

            build.Encoding.TryFindEntry(fileEntry.ContentKey, out var entry);
            return entry;
        }*/
    }

}