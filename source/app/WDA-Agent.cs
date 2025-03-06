using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient; // Ensure Microsoft.Data.SqlClient is referenced via NuGet
using Newtonsoft.Json;
using System.Reflection;
using System.Runtime.Versioning; // For SupportedOSPlatform attribute

namespace ProcessWatchdog
{
    #region Models and Enums
    public enum RequestType
    {
        GET,
        POST,
        PUT,
        DELETE,
        PATCH,
        HEAD,
        OPTIONS,
        CONNECT,
        TRACE
    }

    public enum LogFormat
    {
        TextOnly,
        DatabaseOnly,
        TextAndDatabase
    }

    public enum LogLevel
    {
        Debug,
        Information,
        Warning,
        Error
    }

    public class WatchdogEntry
    {
        public DateTime Timestamp { get; set; }
        public RequestType RequestType { get; set; }
        public string RequestCode { get; set; } = string.Empty;
        public long PayloadSize { get; set; }
        public string OriginPath { get; set; } = string.Empty;
        public string DestinationPath { get; set; } = string.Empty;
        public string OriginDomain { get; set; } = string.Empty;
        public string DestinationDomain { get; set; } = string.Empty;
        public string OriginRegion { get; set; } = string.Empty;
        public string DestinationRegion { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public TimeSpan Latency { get; set; } = TimeSpan.Zero;

        public override string ToString()
        {
            return $"{Timestamp:yyyy-MM-dd HH:mm:ss} | " +
                   $"{RequestType} | " +
                   $"{(string.IsNullOrEmpty(RequestCode) ? "N/A" : RequestCode)} | " +
                   $"{PayloadSize} | " +
                   $"{(string.IsNullOrEmpty(OriginPath) ? "N/A" : OriginPath)} -> {(string.IsNullOrEmpty(DestinationPath) ? "N/A" : DestinationPath)} | " +
                   $"{(string.IsNullOrEmpty(OriginDomain) ? "N/A" : OriginDomain)} -> {(string.IsNullOrEmpty(DestinationDomain) ? "N/A" : DestinationDomain)} | " +
                   $"{(string.IsNullOrEmpty(OriginRegion) ? "N/A" : OriginRegion)} -> {(string.IsNullOrEmpty(DestinationRegion) ? "N/A" : DestinationRegion)} | " +
                   $"{Status} | " +
                   $"{Latency.TotalMilliseconds}ms";
        }
    }

    public class WatchdogConfiguration
    {
        public List<TargetConfig> Targets { get; set; } = new List<TargetConfig>();
        public LoggingConfig Logging { get; set; } = new LoggingConfig();
        public RequestConfig Requests { get; set; } = new RequestConfig();
        public DatabaseConfig Database { get; set; } = new DatabaseConfig();
    }

    public class TargetConfig
    {
        // Allowed values: "Process", "File", "Directory"
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int? ProcessId { get; set; }
        public string Path { get; set; } = string.Empty;
        public bool Recursive { get; set; } = false;
        public Dictionary<string, string> AdditionalParams { get; set; } = new Dictionary<string, string>();
    }

    public class LoggingConfig
    {
        public string LogPath { get; set; } = @"WD-Settings\logs";
        public LogFormat LogFormat { get; set; } = LogFormat.TextAndDatabase;
        public LogLevel LogLevel { get; set; } = LogLevel.Information;
        public bool LogRotation { get; set; } = true;
        public int MaxLogFiles { get; set; } = 7;
    }

    public class RequestConfig
    {
        public List<RequestType> AllowedRequestTypes { get; set; } = Enum.GetValues(typeof(RequestType)).Cast<RequestType>().ToList();
        public int MonitoringDelayMs { get; set; } = 1000;
        public int MaxLogEntries { get; set; } = 10000;
        public bool EnableFiltering { get; set; } = false;
        public List<string> FilterPatterns { get; set; } = new List<string>();
    }

    public class DatabaseConfig
    {
        public string ConnectionString { get; set; } = string.Empty;
        public bool EnableLogging { get; set; } = false;
        public string TableName { get; set; } = "WatchdogLogs";
        public int BatchSize { get; set; } = 100;
    }
    #endregion

    #region Core Watchdog Implementation
    public class ProcessWatchdog
    {
        private WatchdogConfiguration _configuration = new WatchdogConfiguration();
        private readonly ConcurrentQueue<WatchdogEntry> _logQueue;
        private readonly List<FileSystemWatcher> _fileWatchers;
        private readonly List<Process> _monitoredProcesses;
        private CancellationToken _cancellationToken;
        private Task? _logProcessingTask;
        private Task? _statisticsTask;
        private DateTime _startTime;
        private int _totalLogEntries = 0;
        private readonly object _lockObject = new object();
        private bool _isRunning = false;

        // Replace auto-properties with explicit backing fields
        private int _processEventsDetected = 0;
        public int ProcessEventsDetected => _processEventsDetected;

        private int _fileEventsDetected = 0;
        public int FileEventsDetected => _fileEventsDetected;

        public int NetworkEventsDetected { get; private set; } = 0;

        // Use a backing field for RequestTypeCounts and expose it as a read-only dictionary.
        private readonly Dictionary<RequestType, int> _requestTypeCounts = new Dictionary<RequestType, int>();
        public IReadOnlyDictionary<RequestType, int> RequestTypeCounts => _requestTypeCounts;

        public ProcessWatchdog(string configPath)
        {
            LoadConfiguration(configPath);
            _logQueue = new ConcurrentQueue<WatchdogEntry>();
            _fileWatchers = new List<FileSystemWatcher>();
            _monitoredProcesses = new List<Process>();

            foreach (RequestType type in Enum.GetValues(typeof(RequestType)))
            {
                _requestTypeCounts[type] = 0;
            }
        }

        private void LoadConfiguration(string configPath)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    WriteColoredLine($"Configuration file not found at {configPath}. Creating default configuration.", ConsoleColor.Yellow);
                    _configuration = CreateDefaultConfiguration();
                    SaveConfiguration(configPath);
                    return;
                }

                string configJson = File.ReadAllText(configPath);
                _configuration = JsonConvert.DeserializeObject<WatchdogConfiguration>(configJson) ?? CreateDefaultConfiguration();
                ValidateConfiguration();
            }
            catch (Exception ex)
            {
                WriteColoredLine($"Configuration Error: {ex.Message}", ConsoleColor.Red);
                _configuration = CreateDefaultConfiguration();
            }
        }

        private WatchdogConfiguration CreateDefaultConfiguration()
        {
            return new WatchdogConfiguration
            {
                Targets = new List<TargetConfig>
                {
                    new TargetConfig { Type = "Process", Name = "explorer" }
                },
                Logging = new LoggingConfig(),
                Requests = new RequestConfig(),
                Database = new DatabaseConfig()
            };
        }

        private void SaveConfiguration(string configPath)
        {
            try
            {
                string directory = Path.GetDirectoryName(configPath) ?? string.Empty;
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string configJson = JsonConvert.SerializeObject(_configuration, Formatting.Indented);
                File.WriteAllText(configPath, configJson);
                WriteColoredLine("Configuration saved successfully.", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                WriteColoredLine($"Failed to save configuration: {ex.Message}", ConsoleColor.Red);
            }
        }

        private void ValidateConfiguration()
        {
            if (_configuration.Logging != null)
            {
                Directory.CreateDirectory(_configuration.Logging.LogPath);
            }

            if (_configuration.Requests?.AllowedRequestTypes == null || _configuration.Requests.AllowedRequestTypes.Count == 0)
            {
                _configuration.Requests.AllowedRequestTypes = Enum.GetValues(typeof(RequestType)).Cast<RequestType>().ToList();
            }
        }

        public bool Start(CancellationToken cancellationToken)
        {
            if (_isRunning)
            {
                WriteColoredLine("Watchdog is already running.", ConsoleColor.Yellow);
                return false;
            }

            try
            {
                if (!IsRunAsAdministrator())
                {
                    WriteColoredLine("Application must be run with administrator privileges!", ConsoleColor.Red);
                    return false;
                }

                _cancellationToken = cancellationToken;
                _startTime = DateTime.Now;
                _isRunning = true;

                foreach (var target in _configuration.Targets)
                {
                    try
                    {
                        switch (target.Type.ToLower())
                        {
                            case "process":
                                MonitorProcess(target);
                                break;
                            case "file":
                                WatchFile(target);
                                break;
                            case "directory":
                                WatchDirectory(target);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteColoredLine($"Error monitoring target {target.Name ?? target.Path}: {ex.Message}", ConsoleColor.Red);
                    }
                }

                _logProcessingTask = Task.Run(() => ProcessLogQueue(), _cancellationToken);
                _statisticsTask = Task.Run(() => CollectStatistics(), _cancellationToken);

                WriteColoredLine($"Watchdog Started. Monitoring {_configuration.Targets.Count} targets.", ConsoleColor.Green);
                return true;
            }
            catch (Exception ex)
            {
                WriteColoredLine($"Failed to start watchdog: {ex.Message}", ConsoleColor.Red);
                _isRunning = false;
                return false;
            }
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                WriteColoredLine("Watchdog is not running.", ConsoleColor.Yellow);
                return;
            }

            _isRunning = false;

            foreach (var watcher in _fileWatchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _fileWatchers.Clear();

            foreach (var process in _monitoredProcesses)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        LogProcessTermination(process);
                    }
                }
                catch { }
            }
            _monitoredProcesses.Clear();

            ProcessRemainingLogs();
            WriteColoredLine("Watchdog stopped. All monitoring ceased.", ConsoleColor.Yellow);
        }

        private void ProcessRemainingLogs()
        {
            WriteColoredLine("Processing remaining log entries...", ConsoleColor.Blue);
            while (!_logQueue.IsEmpty)
            {
                if (_logQueue.TryDequeue(out var entry))
                {
                    LogEntry(entry);
                }
            }
        }

        private void MonitorProcess(TargetConfig target)
        {
            List<Process> processes = new List<Process>();

            if (!string.IsNullOrEmpty(target.Name))
            {
                processes.AddRange(Process.GetProcessesByName(target.Name));
                WriteColoredLine($"Monitoring {processes.Count} instances of process '{target.Name}'", ConsoleColor.Cyan);
            }
            else if (target.ProcessId.HasValue)
            {
                try
                {
                    Process process = Process.GetProcessById(target.ProcessId.Value);
                    processes.Add(process);
                    WriteColoredLine($"Monitoring process with PID {target.ProcessId.Value} ({process.ProcessName})", ConsoleColor.Cyan);
                }
                catch (Exception ex)
                {
                    WriteColoredLine($"Could not find process with PID {target.ProcessId}: {ex.Message}", ConsoleColor.Red);
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(target.Path))
            {
                try
                {
                    Process process = Process.Start(target.Path)!;
                    processes.Add(process);
                    WriteColoredLine($"Started and monitoring process from path: {target.Path}", ConsoleColor.Cyan);
                }
                catch (Exception ex)
                {
                    WriteColoredLine($"Error starting process from {target.Path}: {ex.Message}", ConsoleColor.Red);
                    return;
                }
            }

            if (processes.Count == 0)
            {
                WriteColoredLine($"No matching processes found for target: {target.Name ?? target.Path ?? target.ProcessId?.ToString()}", ConsoleColor.Yellow);
                return;
            }

            foreach (var process in processes)
            {
                _monitoredProcesses.Add(process);
                Task.Run(() =>
                {
                    try
                    {
                        process.WaitForExit();
                        LogProcessTermination(process);
                        lock (_lockObject)
                        {
                            _monitoredProcesses.Remove(process);
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteColoredLine($"Error monitoring process exit: {ex.Message}", ConsoleColor.Red);
                    }
                }, _cancellationToken);
            }
        }

        private void WatchFile(TargetConfig target)
        {
            if (string.IsNullOrEmpty(target.Path))
            {
                WriteColoredLine("Invalid file path for monitoring.", ConsoleColor.Red);
                return;
            }

            string filePath = target.Path;
            if (!File.Exists(filePath))
            {
                WriteColoredLine($"File not found: {filePath}", ConsoleColor.Yellow);
                string? directory = Path.GetDirectoryName(filePath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                {
                    WriteColoredLine($"Directory does not exist: {directory}", ConsoleColor.Red);
                    return;
                }

                string fileName = Path.GetFileName(filePath);
                var watcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime
                };

                watcher.Created += OnFileChanged;
                watcher.EnableRaisingEvents = true;
                _fileWatchers.Add(watcher);
                WriteColoredLine($"Watching for creation of file: {filePath}", ConsoleColor.Cyan);
                return;
            }

            var fileInfo = new FileInfo(filePath);
            var fileWatcher = new FileSystemWatcher(fileInfo.DirectoryName!, fileInfo.Name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.Attributes
            };

            fileWatcher.Changed += OnFileChanged;
            fileWatcher.Created += OnFileChanged;
            fileWatcher.Deleted += OnFileChanged;
            fileWatcher.Renamed += OnFileChanged;
            fileWatcher.EnableRaisingEvents = true;
            _fileWatchers.Add(fileWatcher);
            WriteColoredLine($"Watching file: {filePath}", ConsoleColor.Cyan);
        }

        private void WatchDirectory(TargetConfig target)
        {
            if (string.IsNullOrEmpty(target.Path) || !Directory.Exists(target.Path))
            {
                WriteColoredLine($"Invalid directory path: {target.Path}", ConsoleColor.Red);
                return;
            }

            var watcher = new FileSystemWatcher(target.Path)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = target.Recursive
            };

            watcher.Changed += OnFileChanged;
            watcher.Created += OnFileChanged;
            watcher.Deleted += OnFileChanged;
            watcher.Renamed += OnFileChanged;
            watcher.EnableRaisingEvents = true;
            _fileWatchers.Add(watcher);
            WriteColoredLine($"Watching directory: {target.Path} {(target.Recursive ? "(including subdirectories)" : string.Empty)}", ConsoleColor.Cyan);
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            Interlocked.Increment(ref _fileEventsDetected);

            if (_configuration.Requests.EnableFiltering)
            {
                bool filtered = _configuration.Requests.FilterPatterns.Any(pattern => e.FullPath.Contains(pattern));
                if (filtered)
                    return;
            }

            long fileSize = 0;
            try
            {
                if (File.Exists(e.FullPath))
                {
                    fileSize = new FileInfo(e.FullPath).Length;
                }
            }
            catch { }

            var entry = new WatchdogEntry
            {
                Timestamp = DateTime.Now,
                RequestType = RequestType.GET,
                OriginPath = e.FullPath,
                Status = e.ChangeType.ToString(),
                PayloadSize = fileSize
            };

            if (e.FullPath.EndsWith(".mdf", StringComparison.OrdinalIgnoreCase) ||
                e.FullPath.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase) ||
                e.FullPath.EndsWith(".ndf", StringComparison.OrdinalIgnoreCase) ||
                e.FullPath.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                entry.OriginDomain = "Database";
                entry.RequestType = DetermineRequestTypeFromFileOperation(e.ChangeType);
            }

            _logQueue.Enqueue(entry);
            lock (_requestTypeCounts)
            {
                _requestTypeCounts[entry.RequestType] = _requestTypeCounts[entry.RequestType] + 1;
            }
        }

        private RequestType DetermineRequestTypeFromFileOperation(WatcherChangeTypes changeType)
        {
            return changeType switch
            {
                WatcherChangeTypes.Created => RequestType.POST,
                WatcherChangeTypes.Changed => RequestType.PUT,
                WatcherChangeTypes.Deleted => RequestType.DELETE,
                WatcherChangeTypes.Renamed => RequestType.PATCH,
                _ => RequestType.GET,
            };
        }

        private void LogProcessTermination(Process process)
        {
            Interlocked.Increment(ref _processEventsDetected);
            string processName = "Unknown";
            string filePath = "Unknown";

            try
            {
                processName = process.ProcessName;
                filePath = process.MainModule?.FileName ?? "Unknown";
            }
            catch { }

            var entry = new WatchdogEntry
            {
                Timestamp = DateTime.Now,
                RequestType = RequestType.DELETE,
                OriginPath = filePath,
                Status = "Terminated",
                RequestCode = process.Id.ToString(),
                OriginDomain = processName
            };

            _logQueue.Enqueue(entry);
            lock (_requestTypeCounts)
            {
                _requestTypeCounts[RequestType.DELETE] = _requestTypeCounts[RequestType.DELETE] + 1;
            }

            WriteColoredLine($"Process terminated: {processName} (PID: {process.Id})", ConsoleColor.Yellow);
        }

        private void ProcessLogQueue()
        {
            while (!_cancellationToken.IsCancellationRequested && _isRunning)
            {
                int processingDelay = _configuration.Requests?.MonitoringDelayMs ?? 1000;
                int processedCount = 0;
                int maxPerBatch = 100;

                while (processedCount < maxPerBatch && _logQueue.TryDequeue(out var entry))
                {
                    if (_configuration.Requests.AllowedRequestTypes.Contains(entry.RequestType))
                    {
                        LogEntry(entry);
                        processedCount++;
                        Interlocked.Increment(ref _totalLogEntries);
                    }
                }

                if (!_cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(processingDelay);
                }
            }
        }

        private void CollectStatistics()
        {
            while (!_cancellationToken.IsCancellationRequested && _isRunning)
            {
                Thread.Sleep(5000);
                // Additional detailed statistics collection could be added here.
            }
        }

        private void LogEntry(WatchdogEntry entry)
        {
            try
            {
                if (_configuration.Logging.LogFormat == LogFormat.TextOnly ||
                    _configuration.Logging.LogFormat == LogFormat.TextAndDatabase)
                {
                    LogToFile(entry);
                }

                if ((_configuration.Logging.LogFormat == LogFormat.DatabaseOnly ||
                    _configuration.Logging.LogFormat == LogFormat.TextAndDatabase) &&
                    _configuration.Database.EnableLogging)
                {
                    LogToDatabase(entry);
                }
            }
            catch (Exception ex)
            {
                WriteColoredLine($"Error logging entry: {ex.Message}", ConsoleColor.Red);
            }
        }

        private void LogToFile(WatchdogEntry entry)
        {
            string logFilePath = Path.Combine(
                _configuration.Logging.LogPath,
                $"watchdog_log_{DateTime.Now:yyyyMMdd}.txt"
            );

            try
            {
                File.AppendAllText(logFilePath, entry.ToString() + Environment.NewLine);
            }
            catch (Exception ex)
            {
                WriteColoredLine($"Failed to write to log file: {ex.Message}", ConsoleColor.Red);
            }
        }

        private void LogToDatabase(WatchdogEntry entry)
        {
            if (string.IsNullOrEmpty(_configuration.Database.ConnectionString))
            {
                return;
            }

            try
            {
                using (var connection = new SqlConnection(_configuration.Database.ConnectionString))
                {
                    connection.Open();
                    var cmd = new SqlCommand($@"
                        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '{_configuration.Database.TableName}')
                        BEGIN
                            CREATE TABLE {_configuration.Database.TableName} (
                                Id INT IDENTITY(1,1) PRIMARY KEY,
                                Timestamp DATETIME NOT NULL,
                                RequestType NVARCHAR(50) NOT NULL,
                                RequestCode NVARCHAR(100),
                                PayloadSize BIGINT,
                                OriginPath NVARCHAR(MAX),
                                DestinationPath NVARCHAR(MAX),
                                OriginDomain NVARCHAR(255),
                                DestinationDomain NVARCHAR(255),
                                OriginRegion NVARCHAR(100),
                                DestinationRegion NVARCHAR(100),
                                Status NVARCHAR(100),
                                Latency FLOAT
                            )
                        END", connection);
                    cmd.ExecuteNonQuery();

                    cmd = new SqlCommand($@"
                        INSERT INTO {_configuration.Database.TableName}
                        (Timestamp, RequestType, RequestCode, PayloadSize, 
                         OriginPath, DestinationPath, OriginDomain, DestinationDomain, 
                         OriginRegion, DestinationRegion, Status, Latency) 
                        VALUES 
                        (@Timestamp, @RequestType, @RequestCode, @PayloadSize, 
                         @OriginPath, @DestinationPath, @OriginDomain, @DestinationDomain, 
                         @OriginRegion, @DestinationRegion, @Status, @Latency)", connection);

                    cmd.Parameters.AddWithValue("@Timestamp", entry.Timestamp);
                    cmd.Parameters.AddWithValue("@RequestType", entry.RequestType.ToString());
                    cmd.Parameters.AddWithValue("@RequestCode", (object)entry.RequestCode ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PayloadSize", entry.PayloadSize);
                    cmd.Parameters.AddWithValue("@OriginPath", (object)entry.OriginPath ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@DestinationPath", (object)entry.DestinationPath ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@OriginDomain", (object)entry.OriginDomain ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@DestinationDomain", (object)entry.DestinationDomain ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@OriginRegion", (object)entry.OriginRegion ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@DestinationRegion", (object)entry.DestinationRegion ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Status", (object)entry.Status ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Latency", entry.Latency.TotalMilliseconds);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                WriteColoredLine($"Database logging error: {ex.Message}", ConsoleColor.Red);
            }
        }

        [SupportedOSPlatform("windows")]
        private static bool IsRunAsAdministrator()
        {
            // Using null-forgiving operator (!) because GetCurrent() is expected to succeed on Windows.
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()!).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static void WriteColoredLine(string message, ConsoleColor color)
        {
            lock (Console.Out)
            {
                Console.ForegroundColor = color;
                Console.WriteLine(message);
                Console.ResetColor();
            }
        }

        public WatchdogStatistics GetStatistics()
        {
            return new WatchdogStatistics
            {
                RunningTime = DateTime.Now - _startTime,
                TotalLogEntries = _totalLogEntries,
                FileEventsDetected = FileEventsDetected,
                ProcessEventsDetected = ProcessEventsDetected,
                NetworkEventsDetected = NetworkEventsDetected,
                RequestTypeCounts = new Dictionary<RequestType, int>(_requestTypeCounts),
                QueuedEvents = _logQueue.Count,
                ActiveProcesses = _monitoredProcesses.Count,
                ActiveFileWatchers = _fileWatchers.Count
            };
        }
    }
    #endregion

    #region Statistics
    public class WatchdogStatistics
    {
        public TimeSpan RunningTime { get; set; }
        public int TotalLogEntries { get; set; }
        public int FileEventsDetected { get; set; }
        public int ProcessEventsDetected { get; set; }
        public int NetworkEventsDetected { get; set; }
        public Dictionary<RequestType, int> RequestTypeCounts { get; set; }
        public int QueuedEvents { get; set; }
        public int ActiveProcesses { get; set; }
        public int ActiveFileWatchers { get; set; }
    }
    #endregion

    #region CLI Interface
    public class WatchdogCLI
    {
        private readonly ProcessWatchdog _watchdog;
        private WatchdogConfiguration _configuration;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly string _configPath;
        private bool _isRunning = false;
        private bool _shouldExit = false;
        private readonly int _refreshRate = 1000; // milliseconds
        private readonly Timer _refreshTimer;

        public WatchdogCLI(string configPath)
        {
            _configPath = configPath;
            LoadConfiguration(configPath);
            _watchdog = new ProcessWatchdog(configPath);

            Console.Title = "Advanced Process Watchdog";
            Console.CursorVisible = false;
            _refreshTimer = new Timer(RefreshUI, null, Timeout.Infinite, _refreshRate);
        }

        private void LoadConfiguration(string configPath)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    WriteColoredLine($"Configuration file not found at {configPath}. Creating default configuration.", ConsoleColor.Yellow);
                    _configuration = new WatchdogConfiguration
                    {
                        Targets = new List<TargetConfig>
                        {
                            new TargetConfig { Type = "Process", Name = "explorer" }
                        },
                        Logging = new LoggingConfig(),
                        Requests = new RequestConfig(),
                        Database = new DatabaseConfig()
                    };
                    SaveConfiguration(configPath);
                    return;
                }

                string configJson = File.ReadAllText(configPath);
                _configuration = JsonConvert.DeserializeObject<WatchdogConfiguration>(configJson) ?? new WatchdogConfiguration();
            }
            catch (Exception ex)
            {
                WriteColoredLine("Configuration Error: " + ex.Message, ConsoleColor.Red);
                _configuration = new WatchdogConfiguration();
            }
        }

        private void SaveConfiguration(string configPath)
        {
            try
            {
                string directory = Path.GetDirectoryName(configPath) ?? string.Empty;
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string configJson = JsonConvert.SerializeObject(_configuration, Formatting.Indented);
                File.WriteAllText(configPath, configJson);
                WriteColoredLine("Configuration saved successfully.", ConsoleColor.Green);
            }
            catch (Exception ex)
            {
                WriteColoredLine($"Failed to save configuration: {ex.Message}", ConsoleColor.Red);
            }
        }

        public void RunInteractiveCLI()
        {
            Console.Clear();
            DisplayHeader();
            _refreshTimer.Change(0, _refreshRate);

            while (!_shouldExit)
            {
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(true);
                    ProcessKeyPress(keyInfo);
                }
                Thread.Sleep(50);
            }

            _refreshTimer.Change(Timeout.Infinite, _refreshRate);
            if (_isRunning)
            {
                StopWatchdog();
            }
            WriteColoredLine("Exiting Watchdog CLI...", ConsoleColor.Yellow);
        }

        private void DisplayHeader()
        {
            Console.Clear();
            Console.WriteLine("=== Advanced Process Watchdog CLI ===");
            Console.WriteLine($"Current Time: {DateTime.Now}");
            if (_isRunning)
            {
                var stats = _watchdog.GetStatistics();
                Console.WriteLine("Status: WATCHDOG IS RUNNING");
                Console.WriteLine($"Running Time: {stats.RunningTime}");
                Console.WriteLine($"Total Log Entries: {stats.TotalLogEntries}");
                Console.WriteLine($"Active Processes: {stats.ActiveProcesses}");
                Console.WriteLine($"Active File Watchers: {stats.ActiveFileWatchers}");
                Console.WriteLine("Press 'T' to stop watchdog, 'Q' to quit.");
            }
            else
            {
                Console.WriteLine("Status: WATCHDOG IS NOT RUNNING");
                Console.WriteLine("Press 'S' to start watchdog, 'Q' to quit.");
            }
            Console.WriteLine("Press 'H' for help.");
        }

        private void ProcessKeyPress(ConsoleKeyInfo keyInfo)
        {
            switch (keyInfo.Key)
            {
                case ConsoleKey.S:
                    StartWatchdog();
                    break;
                case ConsoleKey.T:
                    StopWatchdog();
                    break;
                case ConsoleKey.Q:
                    _shouldExit = true;
                    break;
                case ConsoleKey.H:
                    ShowHelp();
                    break;
                default:
                    break;
            }
        }

        private void StartWatchdog()
        {
            if (!_isRunning)
            {
                _cancellationTokenSource = new CancellationTokenSource();
                if (_watchdog.Start(_cancellationTokenSource.Token))
                {
                    _isRunning = true;
                    WriteColoredLine("Watchdog started.", ConsoleColor.Green);
                }
                else
                {
                    WriteColoredLine("Failed to start watchdog.", ConsoleColor.Red);
                }
            }
            else
            {
                WriteColoredLine("Watchdog is already running.", ConsoleColor.Yellow);
            }
            DisplayHeader();
        }

        private void StopWatchdog()
        {
            if (_isRunning)
            {
                _watchdog.Stop();
                _cancellationTokenSource?.Cancel();
                _isRunning = false;
                WriteColoredLine("Watchdog stopped.", ConsoleColor.Yellow);
            }
            else
            {
                WriteColoredLine("Watchdog is not running.", ConsoleColor.Yellow);
            }
            DisplayHeader();
        }

        private void RefreshUI(object? state)
        {
            DisplayHeader();
        }

        private void ShowHelp()
        {
            Console.WriteLine("Help:");
            Console.WriteLine("S - Start Watchdog");
            Console.WriteLine("T - Stop Watchdog");
            Console.WriteLine("Q - Quit the application");
            Console.WriteLine("H - Show this help message");
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey(true);
            DisplayHeader();
        }

        private void WriteColoredLine(string message, ConsoleColor color)
        {
            ProcessWatchdog.WriteColoredLine(message, color);
        }
    }
    #endregion

    #region Application Entry Point
    class Program
    {
        static void Main(string[] args)
        {
            // Adjust configuration file path as needed.
            string configPath = "WD-Settings\\WDA-Config.json";
            var cli = new WatchdogCLI(configPath);
            cli.RunInteractiveCLI();
        }
    }
    #endregion
}
