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
        private RemoteCache _remoteCache;

        [GlobalSetup]
        public void Initialize()
        {
            _remoteCache = new RemoteCache("./CACHE", "wow", "eu");
        }

        [Benchmark] public RemoteCache OpenRemoteCache() => new ("./CACHE", "wow", "eu");

        [Benchmark]
        public (byte[], byte[]) GetVersion()
        {
            _remoteCache = new RemoteCache("./CACHE", "wow", "eu");
            var (build, cdn) = _remoteCache.GetVersion();

            Console.WriteLine(Convert.ToHexStringLower(build));
            Console.WriteLine(Convert.ToHexStringLower(cdn));

            return (build, cdn);
        }
    }
}
