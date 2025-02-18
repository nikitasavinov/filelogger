﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Karambolo.Extensions.Logging.File
{
    public interface IFileLoggerSettingsBase
    {
        IFileAppender FileAppender { get; }
        string BasePath { get; }
        bool EnsureBasePath { get; }
        Encoding FileEncoding { get; }
        string FallbackFileName { get; }
        string DateFormat { get; }
        string CounterFormat { get; }
        int MaxFileSize { get; }
        IFileLogEntryTextBuilder TextBuilder { get; }
        bool IncludeScopes { get; }
        int MaxQueueSize { get; }

        string MapToFileName(string categoryName, string fallbackFileName);
        Func<string, LogLevel, bool> BuildFilter(string categoryName);

        IFileLoggerSettingsBase Freeze();
    }

    public interface IFileLoggerSettings : IFileLoggerSettingsBase
    {
        IFileLoggerSettings Reload();
        IChangeToken ChangeToken { get; }
    }

    public abstract class FileLoggerSettingsBase : IFileLoggerSettingsBase
    {
        protected internal delegate bool TryGetLogLevel(string categoryName, out LogLevel level);

        public const string DefaultCategoryName = "Default";

        public static IEnumerable<string> GetPrefixes(string categoryName, bool returnDefault = true)
        {
            while (!string.IsNullOrEmpty(categoryName))
            {
                yield return categoryName;
                var index = categoryName.LastIndexOf('.');
                if (index == -1)
                {
                    if (returnDefault)
                        yield return DefaultCategoryName;

                    break;
                }
                categoryName = categoryName.Substring(0, index);
            }
        }

        protected internal static Func<string, LogLevel, bool> BuildFilter(string categoryName, TryGetLogLevel tryGetLogLevel)
        {
            foreach (var prefix in GetPrefixes(categoryName))
                if (tryGetLogLevel(prefix, out LogLevel level))
                    return (c, l) => l >= level;

            return (c, l) => false;
        }

        private bool _isFrozen;

        protected FileLoggerSettingsBase() { }

        protected FileLoggerSettingsBase(FileLoggerSettingsBase other)
        {
            FileAppender = other.FileAppender;
            BasePath = other.BasePath;
            EnsureBasePath = other.EnsureBasePath;
            FileEncoding = other.FileEncoding;

            FallbackFileName = other.FallbackFileName;
            if (other.FileNameMappings != null)
                FileNameMappings = new Dictionary<string, string>(other.FileNameMappings);

            DateFormat = other.DateFormat;
            CounterFormat = other.CounterFormat;
            MaxFileSize = other.MaxFileSize;
            TextBuilder = other.TextBuilder;
            IncludeScopes = other.IncludeScopes;
            MaxQueueSize = other.MaxQueueSize;
        }

        public IFileAppender FileAppender { get; set; }
        public string BasePath { get; set; }
        public bool EnsureBasePath { get; set; }
        public Encoding FileEncoding { get; set; }
        public string FallbackFileName { get; set; }
        public IDictionary<string, string> FileNameMappings { get; set; }
        public string DateFormat { get; set; }
        public string CounterFormat { get; set; }
        public int MaxFileSize { get; set; }
        public IFileLogEntryTextBuilder TextBuilder { get; set; }
        public bool IncludeScopes { get; set; }
        public int MaxQueueSize { get; set; } = -1;

        public virtual string MapToFileName(string categoryName, string fallbackFileName)
        {
            if (FileNameMappings != null)
                foreach (var prefix in GetPrefixes(categoryName))
                    if (FileNameMappings.TryGetValue(prefix, out string fileName))
                        return fileName;

            return fallbackFileName;
        }

        public abstract Func<string, LogLevel, bool> BuildFilter(string categoryName);

        protected abstract FileLoggerSettingsBase CreateClone();

        IFileLoggerSettingsBase IFileLoggerSettingsBase.Freeze()
        {
            if (_isFrozen)
                return this;

            FileLoggerSettingsBase clone = CreateClone();
            clone._isFrozen = true;
            return clone;
        }
    }

    public class FileLoggerOptions : FileLoggerSettingsBase
    {
        public FileLoggerOptions() { }

        protected FileLoggerOptions(FileLoggerOptions other) : base(other) { }

        public string RootPath
        {
            get => (FileAppender as PhysicalFileAppender)?.FileProvider.Root;
            set => FileAppender = new PhysicalFileAppender(value);
        }

        public string FileEncodingName
        {
            get => FileEncoding?.WebName;
            set => FileEncoding = !string.IsNullOrEmpty(value) ? Encoding.GetEncoding(value) : null;
        }

        public string TextBuilderType
        {
            get => TextBuilder?.GetType().AssemblyQualifiedName;
            set
            {
                if (string.IsNullOrEmpty(value))
                    TextBuilder = null;

                var type = Type.GetType(value, throwOnError: true);

                if (!type.GetTypeInfo().ImplementedInterfaces.Contains(typeof(IFileLogEntryTextBuilder)))
                    throw new ArgumentException($"Type must implement the {typeof(IFileLogEntryTextBuilder).Name} interface.", nameof(value));

                if (!type.GetTypeInfo().DeclaredConstructors.Any(ci => ci.GetParameters().Length == 0))
                    throw new ArgumentException("Type must provide a parameterless constructor.", nameof(value));

                TextBuilder = (IFileLogEntryTextBuilder)Activator.CreateInstance(type);
            }
        }

        public override Func<string, LogLevel, bool> BuildFilter(string categoryName)
        {
            return (c, l) => true;
        }

        protected override FileLoggerSettingsBase CreateClone()
        {
            return new FileLoggerOptions(this);
        }
    }

    public class FileLoggerSettings : FileLoggerSettingsBase, IFileLoggerSettings
    {
        public FileLoggerSettings()
        {
            FileNameMappings = new Dictionary<string, string>();
            Switches = new Dictionary<string, LogLevel>();
        }

        protected FileLoggerSettings(FileLoggerSettings other) : base(other)
        {
            if (other.Switches != null)
                Switches = new Dictionary<string, LogLevel>(other.Switches);
        }

        public IDictionary<string, LogLevel> Switches { get; set; }

        public IChangeToken ChangeToken { get; set; }

        public IFileLoggerSettings Reload()
        {
            return this;
        }

        public virtual bool TryGetSwitch(string categoryName, out LogLevel level)
        {
            return Switches.TryGetValue(categoryName, out level);
        }

        public sealed override Func<string, LogLevel, bool> BuildFilter(string categoryName)
        {
            return BuildFilter(categoryName, TryGetSwitch);
        }

        protected override FileLoggerSettingsBase CreateClone()
        {
            return new FileLoggerSettings(this);
        }
    }

    public class ConfigurationFileLoggerSettings : IFileLoggerSettings
    {
        public const string LogLevelSectionName = "LogLevel";
        private readonly Action<FileLoggerSettingsBase> _postConfigure;
        private readonly FileLoggerOptions _options;
        private Dictionary<string, LogLevel> _switches;

        public ConfigurationFileLoggerSettings(IConfiguration configuration)
            : this(configuration, null) { }

        public ConfigurationFileLoggerSettings(IConfiguration configuration, Action<FileLoggerSettingsBase> postConfigure)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            Configuration = configuration;
            _postConfigure = postConfigure;

            _options = CreateLoggerOptions();
            configuration.Bind(_options);

            _switches = new Dictionary<string, LogLevel>();
            configuration.Bind(LogLevelSectionName, _switches);

            _postConfigure?.Invoke(_options);

            ChangeToken = Configuration.GetReloadToken();
        }

        protected IConfiguration Configuration { get; }

        public IFileAppender FileAppender => _options.FileAppender;
        public string BasePath => _options.BasePath;
        public bool EnsureBasePath => _options.EnsureBasePath;
        public Encoding FileEncoding => _options.FileEncoding;
        public string FallbackFileName => _options.FallbackFileName;
        public string DateFormat => _options.DateFormat;
        public string CounterFormat => _options.CounterFormat;
        public int MaxFileSize => _options.MaxFileSize;
        public IFileLogEntryTextBuilder TextBuilder => _options.TextBuilder;
        public bool IncludeScopes => _options.IncludeScopes;
        public int MaxQueueSize => _options.MaxQueueSize;

        public IChangeToken ChangeToken { get; private set; }

        protected virtual FileLoggerOptions CreateLoggerOptions()
        {
            return new FileLoggerOptions();
        }

        protected virtual ConfigurationFileLoggerSettings CreateLoggerSettings()
        {
            return new ConfigurationFileLoggerSettings(Configuration, _postConfigure);
        }

        public string MapToFileName(string categoryName, string fallbackFileName)
        {
            return _options.MapToFileName(categoryName, fallbackFileName);
        }

        public virtual bool TryGetSwitch(string categoryName, out LogLevel level)
        {
            return _switches.TryGetValue(categoryName, out level);
        }

        public Func<string, LogLevel, bool> BuildFilter(string categoryName)
        {
            return FileLoggerSettingsBase.BuildFilter(categoryName, TryGetSwitch);
        }

        public IFileLoggerSettings Reload()
        {
            ChangeToken = null;
            return CreateLoggerSettings();
        }

        IFileLoggerSettingsBase IFileLoggerSettingsBase.Freeze()
        {
            return this;
        }
    }
}
