namespace TACTSharp.Instance
{
    public readonly struct ResourceType
    {
        public readonly string RemotePath;
        public readonly string LocalPath;

        private ResourceType(string remotePath, string localPath)
        {
            RemotePath = remotePath;
            LocalPath = localPath;
        }

        public static readonly ResourceType Config = new("config", "config");
        public static readonly ResourceType Data = new ("data", "data");
        public static readonly ResourceType Indice = new ("data", "indices");
    }
}
