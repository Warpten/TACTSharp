using TACTSharp.Instance;

namespace TACTSharp
{
    public sealed class ResourceManager
    {
        private LocalCache? _localInstallation;
        private RemoteCache _cache;

        public RemoteCache Remote => _cache;

        public ResourceManager(Settings settings)
        {
            if (!string.IsNullOrEmpty(settings.BaseDirectory))
                _localInstallation = new LocalCache(new DirectoryInfo(settings.BaseDirectory));

            _cache = new RemoteCache(settings.CacheDirectory, settings.Product, settings.Region);
        }

        public Resource Resolve(ResourceType resourceType, string filePath)
        {
            if (_localInstallation != null)
            {
                var resource = _localInstallation.OpenResource(resourceType, filePath);
                if (resource != null)
                    return resource.Value;
            }

            return _cache.OpenResource(resourceType, filePath);
        }

        public Resource Resolve(ResourceType resourceType, ReadOnlySpan<byte> encodingKey, long offset = 0, long length = 0)
        {
            // Should local installation be checked here?

            return _cache.OpenResource(resourceType, encodingKey, Convert.ToHexStringLower(encodingKey), offset, length);
        }
    }
}