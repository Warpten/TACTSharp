using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace TACTSharp.Extensions
{
    internal static class LoggingExtensions
    {
        public delegate void TimeHandler(ILogger logger, TimeSpan elapsedTime);

        public static DelayedLog BeginTimedScope(this ILogger logger, TimeHandler parser) => new(logger, parser);

        public class DelayedLog : IDisposable
        {
            private readonly ILogger _logger;
            private readonly Stopwatch _stopwatch;
            private readonly TimeHandler _handler;

            internal DelayedLog(ILogger logger, TimeHandler handler)
            {
                _logger = logger;
                _handler = handler;
                _stopwatch = Stopwatch.StartNew();
            }

            ~DelayedLog()
            {
                Dispose(false);
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            private void Dispose(bool disposing)
            {
                if (!disposing)
                    return;

                _stopwatch.Stop();
                _handler(_logger, _stopwatch.Elapsed);
            }
        }
    }
}
