using System.CommandLine;
using System.CommandLine.Binding;
using System.CommandLine.Invocation;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;

using TACTSharp;
using TACTSharp.Instance;

using static TACTSharp.RootInstance;

namespace TACTTool
{
    internal class Program
    {
        private static Option<string> BuildConfig = new(
            name: "--buildConfig",
            description: "A hexadecimal string identifying the build configuration to load, or the path to a file on disk."
        );
        private static Option<string> CDNConfig = new(
            name: "--cdnConfig",
            description: "A hexadecimal string identifying the CDN configuration to load, or the path to a file on disk."
        );
        private static Option<string> Product = new(
            name: "--product",
            description: "The TACT product code that should be loaded."
        );
        private static Option<string> Region = new(
            name: "--region",
            description: "The region to use for patch service, build selection, and CDNs.",
            getDefaultValue: () => "us"
        );
        private static Option<LocaleFlags> Locale = new(
            name: "--locale",
            description: "The locale to use during file extraction.",
            getDefaultValue: () => LocaleFlags.enUS
        );
        private static Option<FileSystemInfo> Destination = new(
            name: "--output",
            getDefaultValue: () => new DirectoryInfo("./")
        );
        private static Option<DirectoryInfo> CacheDirectory = new(
            name: "--cacheDirectory",
            description: "A path on disk that will be used as a cache for files downloaded by this tool.",
            getDefaultValue: () => new DirectoryInfo("./cache")
        );
        private static Option<DirectoryInfo> BaseDirectory = new(
            name: "--baseDirectory",
            description: "A path to an installation of WoW to use as source for build informations and local read-only cache."
        );
        private static Option<LogLevel> LogLevel = new(
            name: "--logLevel",
            getDefaultValue: () => Microsoft.Extensions.Logging.LogLevel.Information,
            description: "Determines what level of logging should be enabled."
        );

        private static bool ValidateConfigurationIdentifier(string? identifier)
        {
            if (string.IsNullOrEmpty(identifier))
                return false;

            return new FileInfo(identifier).Exists || (identifier.Length == 32 && identifier.All(c =>
            {
                if (c >= '0' && c <= '9')
                    return true;

                c = (char)(c & ~0b0100_0000);
                return c >= 'a' && c <= 'f';
            }));
        }

        static Program()
        {
            Product.AddAlias("-p");

            BuildConfig.AddValidator(result => {
                var value = result.GetValueForOption(BuildConfig);
                if (!ValidateConfigurationIdentifier(value))
                    result.ErrorMessage = "The provided value is neither a valid file path nor a valid hex-encoded 16-bytes string.";
            });
            CDNConfig.AddValidator(result => {
                var value = result.GetValueForOption(BuildConfig);
                if (!ValidateConfigurationIdentifier(value))
                    result.ErrorMessage = "The provided value is neither a valid file path nor a valid hex-encoded 16-bytes string.";
            });
        }

        private static (BuildInstance, ILogger) Open(InvocationContext invocationContext)
        {
            var logLevel = invocationContext.ParseResult.GetValueForOption(LogLevel);

            var loggerFactory = LoggerFactory.Create(builder => {
                builder.SetMinimumLevel(logLevel)
                    .AddSimpleConsole(options => {
                        options.IncludeScopes = false;
                        options.SingleLine = true;
                        options.TimestampFormat = "HH:mm:ss.fff ";
                    });
            });
            var logger = loggerFactory.CreateLogger<Program>();

            logger.LogInformation("TACTTool is starting...");

            var settings = new Settings()
            {
                Product = invocationContext.ParseResult.GetValueForOption(Product)!,
                Locale = invocationContext.ParseResult.GetValueForOption(Locale)!,
                Region = invocationContext.ParseResult.GetValueForOption(Region)!,
                CacheDirectory = invocationContext.ParseResult.GetValueForOption(CacheDirectory)!.FullName,
                BaseDirectory = invocationContext.ParseResult.GetValueForOption(BaseDirectory)?.FullName,
                LoggerFactory = loggerFactory
            };

            var resourceManager = new ResourceManager(settings);

            var buildConfigName = invocationContext.ParseResult.GetValueForOption(BuildConfig);
            var cdnConfigName = invocationContext.ParseResult.GetValueForOption(CDNConfig);

            var buildConfig = OpenConfiguration(resourceManager, buildConfigName);
            var cdnConfig = OpenConfiguration(resourceManager, cdnConfigName);

            if ((buildConfig == null) != (cdnConfig == null))
                throw new InvalidOperationException("Providing either a build or cdn configuration requires the other.");

            if (buildConfig == null && cdnConfig == null)
            {
                (buildConfigName, cdnConfigName) = resourceManager.Remote.QueryLatestVersions();
                buildConfig = OpenConfiguration(resourceManager, buildConfigName);
                cdnConfig = OpenConfiguration(resourceManager, cdnConfigName);
            }

            if (buildConfig == null) throw new ArgumentNullException(nameof(buildConfig));
            if (cdnConfig == null) throw new ArgumentNullException(nameof(cdnConfig));

            logger.LogInformation("Using configuration (Build: {Build}, CDN: {CDN})", buildConfigName, cdnConfigName);

            var configuration = new Configuration(buildConfig, cdnConfig);
            return (new BuildInstance(settings, configuration, resourceManager), logger);
        }

        public static void Main(string[] args)
        {
            var root = new RootCommand();
            root.AddGlobalOptions(BuildConfig, CDNConfig, Product, Region, Locale, CacheDirectory, BaseDirectory, LogLevel);

            var fileDataCommand = new Command("extractFileDataID", "Extracts a file given its file data ID.");
            var fileDataArg = new Argument<uint>("fileDataID");
            fileDataCommand.AddArgument(fileDataArg);
            fileDataCommand.SetHandler(context =>
            {
                var (buildInstance, logger) = Open(context);
                var fileDataID = context.ParseResult.GetValueForArgument(fileDataArg);
                var destination = context.ParseResult.GetValueForOption(Destination);

                var fileData = HandleFileDataID(buildInstance, fileDataID);
                ProcessExtractTarget(logger, destination, fileData, fileDataID.ToString());
            });

            var contentKeyCommand = new Command("extractContentKey", "Extracts a file given its content key.");
            var contentKeyArg = new Argument<string>("contentKey");
            contentKeyCommand.AddAlias("ckey");
            contentKeyCommand.AddAlias("chash");
            contentKeyCommand.AddArgument(contentKeyArg);
            contentKeyCommand.SetHandler(context =>
            {
                var (buildInstance, logger) = Open(context);
                var contentKeyStr = context.ParseResult.GetValueForArgument(contentKeyArg);
                var destination = context.ParseResult.GetValueForOption(Destination);

                var contentKey = Convert.FromHexString(contentKeyStr);

                if (!buildInstance.Encoding.TryFindEntry(contentKey, out var fileEKeys) || fileEKeys == null)
                    return;

                var fileData = HandleContentKey(buildInstance, contentKey);
                ProcessExtractTarget(logger, destination, fileData, contentKeyStr);
            });

            var encodingKeyCommand = new Command("extractEncodingKey", "Extracts a file given its encoding key.");
            var encodingKeyArg = new Argument<string>("encodingKey");
            encodingKeyCommand.AddAlias("ekey");
            encodingKeyCommand.AddAlias("ehash");
            encodingKeyCommand.AddArgument(encodingKeyArg);
            encodingKeyCommand.SetHandler(context =>
            {
                var (buildInstance, logger) = Open(context);
                var encodingKeyStr = context.ParseResult.GetValueForArgument(encodingKeyArg);
                var destination = context.ParseResult.GetValueForOption(Destination);

                var encodingKey = Convert.FromHexString(encodingKeyStr);
                var fileData = HandleContentKey(buildInstance, encodingKey);

                ProcessExtractTarget(logger, destination, fileData, encodingKeyStr);
            });

            var fileNameCommand = new Command("extractFileName", "Extracts a file given its complete path.");
            var fileNameArg = new Argument<string>("fileName");
            fileNameCommand.AddArgument(fileNameArg);
            fileNameCommand.SetHandler(context =>
            {
                var (buildInstance, logger) = Open(context);
                var fileName = context.ParseResult.GetValueForArgument(fileNameArg);
                var destination = context.ParseResult.GetValueForOption(Destination);

                var fileData = HandleFileName(buildInstance, fileName);
                ProcessExtractTarget(logger, destination, fileData, fileName);
            });

            var commandFileCommand = new Command("runCommands", "Extracts a set of files from the provided file.");
            var commandFileArg = new Argument<FileInfo>("filePath");
            commandFileCommand.AddArgument(commandFileArg);
            commandFileCommand.SetHandler(context =>
            {
                var (buildInstance, logger) = Open(context);
                var commandFile = context.ParseResult.GetValueForArgument(commandFileArg);
                var destination = context.ParseResult.GetValueForOption(Destination) as DirectoryInfo;

            });

            root.AddCommand(fileDataCommand);
            root.AddCommand(encodingKeyCommand);
            root.AddCommand(contentKeyCommand);
            root.AddCommand(fileNameCommand);

            foreach (var command in root.OfType<Command>().Cast<Command>())
                command.Add(Destination);

            root.Invoke(args);
        }

        private static void ProcessExtractTarget(ILogger logger, FileSystemInfo? destination, ReadOnlySpan<byte> fileData, string sourceName)
        {
            if (fileData.Length == 0)
                logger.LogError("`{SourceName}` could not be extracted.", sourceName);
            else if (destination is FileInfo fileInfo)
            {
                File.WriteAllBytes(fileInfo.FullName, fileData);

                logger.LogInformation("Extracted `{SourceName}` to `{Destination}`.", sourceName, fileInfo.FullName);
            }
            else if (destination is DirectoryInfo directoryInfo)
            {
                var concretePath = Path.Combine(directoryInfo.FullName, sourceName);
                File.WriteAllBytes(concretePath, fileData);

                logger.LogInformation("Extracted `{SourceName}` to `{Destination}`.", sourceName, concretePath);
            }
        }

        private static byte[] HandleFileDataID(BuildInstance buildInstance, uint fileDataID)
        {
            ref readonly var fileEntry = ref buildInstance.Root.FindFileDataID(fileDataID);
            if (Unsafe.IsNullRef(in fileEntry))
                return [];

            return HandleContentKey(buildInstance, fileEntry.ContentKey);
        }

        private static byte[] HandleContentKey(BuildInstance buildInstance, ReadOnlySpan<byte> contentKey)
        {
            if (!buildInstance.Encoding.TryFindEntry(contentKey, out var fileEKeys) || fileEKeys == null)
                return [];

            for (var i = 0; i < fileEKeys.Value.Length; ++i)
            {
                var fileData = HandleEncodingKey(buildInstance, fileEKeys.Value[i]);
                if (fileData.Length != 0)
                    return fileData;
            }

            return [];
        }

        private static byte[] HandleFileName(BuildInstance buildInstance, string fileName)
        {
            // TODO: Add listfile support?
            var fileEntries = buildInstance.Install.Entries
                .Where(x => x.name.Equals(fileName, StringComparison.InvariantCultureIgnoreCase))
                .ToList();

            if (fileEntries.Count == 0)
                return [];

            byte[] targetCKey;
            if (fileEntries.Count > 1)
            {
                var filter = fileEntries.Where(x => x.tags.Contains("4=US")).Select(x => x.md5);
                if (filter.Any())
                {
                    // Console.WriteLine("Multiple results found in install for file " + filename + ", using US version..");
                    targetCKey = filter.First();
                }
                else
                {
                    // Console.WriteLine("Multiple results found in install for file " + filename + ", using first result..");
                    targetCKey = fileEntries[0].md5;
                }
            }
            else
            {
                targetCKey = fileEntries[0].md5;
            }

            return HandleContentKey(buildInstance, targetCKey);
        }

        private static byte[] HandleEncodingKey(BuildInstance buildInstance, ReadOnlySpan<byte> encodingKey)
            => buildInstance.OpenFileByEKey(encodingKey);

        private static Config? OpenConfiguration(ResourceManager resourceManager, string? input)
        {
            if (string.IsNullOrEmpty(input))
                return null;

            // If link type was explicitely specified trust it, but still assert the resource exists.
            if (input[4] == ':')
            {
                if (input.AsSpan().StartsWith("file:") && File.Exists(input[5..]))
                    return Config.FromDisk(input[5..]);

                try
                {
                    if (input.AsSpan().StartsWith("hash:"))
                        return Config.FromHash(resourceManager, Convert.FromHexString(input.AsSpan()[5..]));
                }
                catch (Exception)
                {
                }

                return null;
            }
            else
            {
                try
                {
                    var configKey = Convert.FromHexString(input);
                    return Config.FromHash(resourceManager, configKey);
                }
                catch (FormatException)
                {
                    return Config.FromDisk(input);
                }
            }
        }
    }
}