using Microsoft.Extensions.Logging;

using TACTSharp.Instance;

namespace TACTSharp
{
    public sealed class ResourceManager : LoggingEnabledBase<ResourceManager>
    {
        private LocalCache? _localInstallation;
        private RemoteCache _cache;

        public RemoteCache Remote => _cache;

        public ResourceManager(Settings settings) : base(settings.LoggerFactory)
        {
            if (!string.IsNullOrEmpty(settings.BaseDirectory))
                _localInstallation = new LocalCache(new DirectoryInfo(settings.BaseDirectory));

            _cache = new RemoteCache(settings.CacheDirectory, settings.Product, settings.Region, settings.LoggerFactory);
        }

        public Resource Resolve(ResourceType resourceType, string filePath)
        {
            if (_localInstallation != null)
            {
                var resource = _localInstallation.OpenResource(resourceType, filePath);
                if (resource != null)
                {
                    Logger.LogTrace("Resource {ResourcePath} exists in local installation.", filePath);

                    return resource.Value;
                }
            }

            return _cache.OpenResource(resourceType, filePath);
        }

        public Resource Resolve(ResourceType resourceType, ReadOnlySpan<byte> encodingKey, long offset = 0, long length = 0, bool validate = false)
        {
            // Should local installation be checked here?

            return _cache.OpenResource(resourceType, encodingKey, Convert.ToHexStringLower(encodingKey), offset, length, validate);
        }
    }
}