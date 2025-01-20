using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using BenchmarkDotNet.Attributes;

using TACTSharp.Instance;

namespace TACTBench
{
    public class FutureBenchmark
    {
        private RemoteCache? _remoteCache;

        [GlobalSetup]
        public void Initialize()
        {
            _remoteCache = new RemoteCache("./CACHE", "wow", "eu");
        }

        [Benchmark] public RemoteCache OpenRemoteCache() => new ("./CACHE", "wow", "eu");

        [Benchmark]
        public (string, string) QueryLatestVersions()
        {
            var (build, cdn) = _remoteCache!.QueryLatestVersions();

            return (build, cdn);
        }
    }
}
