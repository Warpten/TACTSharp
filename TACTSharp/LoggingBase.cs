using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TACTSharp
{
    public delegate void TimedScopeHandler(TimeSpan timeElapsed);

    public abstract class LoggingEnabledBase<T>
    {
        protected readonly ILogger Logger;

        protected LoggingEnabledBase(ILoggerFactory? loggerFactory)
        {
            Logger = loggerFactory == null 
                ? NullLogger.Instance
                : loggerFactory.CreateLogger<T>();
        }

        protected TimedScope BeginTimedScope(TimedScopeHandler handler) => new(handler);
    }

    public sealed class TimedScope : IDisposable
    {
        private readonly TimedScopeHandler _handler;
        private readonly Stopwatch _stopwatch;

        internal TimedScope(TimedScopeHandler handler)
        {
            _handler = handler;
            _stopwatch = Stopwatch.StartNew();
        }

        ~TimedScope() => Dispose(false);

        public void Cancel() => _stopwatch.Stop();

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (!disposing || !_stopwatch.IsRunning)
                return;

            _stopwatch.Stop();
            _handler(_stopwatch.Elapsed);
        }
    }
}
