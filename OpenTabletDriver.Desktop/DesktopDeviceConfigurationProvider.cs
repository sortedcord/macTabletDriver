using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenTabletDriver.Components;
using OpenTabletDriver.Configurations;
using OpenTabletDriver.Desktop.Interop.AppInfo;
using OpenTabletDriver.Tablet;

namespace OpenTabletDriver.Desktop
{
    public class DesktopDeviceConfigurationProvider : IDeviceConfigurationProvider
    {
        private const int THRESHOLD_MS = 250;
        private readonly DeviceConfigurationProvider _inAssemblyConfigurationProvider = new();
        private readonly IAppInfo _appInfo;
        private readonly FileSystemWatcher? _watcher;

        private CancellationTokenSource? _cts;
        private ImmutableArray<TabletConfiguration> _tabletConfigurations;

        public DesktopDeviceConfigurationProvider(IAppInfo appInfo)
        {
            _appInfo = appInfo;

            _tabletConfigurations = GetTabletConfigurations();

            if (!Directory.Exists(_appInfo.ConfigurationDirectory))
                return;

            _watcher = new FileSystemWatcher(_appInfo.ConfigurationDirectory)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                Filter = "*.json",
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _watcher.Changed += debouncedUpdateConfigurations;
            _watcher.Renamed += debouncedUpdateConfigurations;
            _watcher.Created += debouncedUpdateConfigurations;
            _watcher.Deleted += debouncedUpdateConfigurations;

            // wait THRESHOLD_MS before updating configurations
            // if another change occurs within THRESHOLD_MS, cancel this update
            void debouncedUpdateConfigurations(object sender, FileSystemEventArgs e)
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;

                Task.Run(async () =>
                {
                    await Task.Delay(THRESHOLD_MS, ct);
                    Log.Debug("Detect", "Refreshing configurations...");
                    _tabletConfigurations = GetTabletConfigurations();
                    TabletConfigurationsChanged?.Invoke(_tabletConfigurations);
                }, ct);
            }
        }

        public bool RaisesTabletConfigurationsChanged => true;
        public ImmutableArray<TabletConfiguration> TabletConfigurations => _tabletConfigurations;
        public event Action<ImmutableArray<TabletConfiguration>>? TabletConfigurationsChanged;

        private ImmutableArray<TabletConfiguration> GetTabletConfigurations()
        {
            if (Directory.Exists(_appInfo.ConfigurationDirectory))
            {
                var files = Directory.EnumerateFiles(_appInfo.ConfigurationDirectory, "*.json", SearchOption.AllDirectories);

                IEnumerable<(ConfigurationSource, TabletConfiguration)> jsonConfigurations = files
                    .Select(path => Serialization.Deserialize<TabletConfiguration>(File.OpenRead(path)))
                    .Select(jsonConfig => (ConfigurationSource.File, jsonConfig))!;

                return _inAssemblyConfigurationProvider.TabletConfigurations
                    .Select(asmConfig => (ConfigurationSource.Assembly, asmConfig))
                    .Concat(jsonConfigurations)
                    .GroupBy(sourcedConfig => sourcedConfig.Item2.Name)
                    .Select(multiSourcedConfig =>
                    {
                        var asmConfig = multiSourcedConfig.Where(m => m.Item1 == ConfigurationSource.Assembly)
                            .Select(m => m.Item2)
                            .FirstOrDefault();
                        var jsonConfig = multiSourcedConfig.Where(m => m.Item1 == ConfigurationSource.File)
                            .Select(m => m.Item2)
                            .FirstOrDefault();

                        return jsonConfig ?? asmConfig!;
                    })
                    .ToImmutableArray();
            }

            return _inAssemblyConfigurationProvider.TabletConfigurations;
        }

        private enum ConfigurationSource
        {
            Assembly,
            File
        }
    }
}
