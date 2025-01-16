using System;
using BenchmarkDotNet.Running;

namespace TACTBench
{
    public class Program
    {
        public static void Main() {
            var summary = BenchmarkRunner.Run<Benchmarks>();
        }
    }
}