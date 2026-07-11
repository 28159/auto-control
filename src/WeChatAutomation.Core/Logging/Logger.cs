using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace WeChatAutomation.Core.Logging
{
    /// <summary>
    /// 日志级别
    /// </summary>
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
        Fatal = 4
    }

    /// <summary>
    /// 日志条目
    /// </summary>
    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Category { get; set; }
        public string Message { get; set; }
        public Exception Exception { get; set; }

        public override string ToString()
        {
            string ex = Exception != null ? $"\n{Exception}" : "";
            return $"[{Timestamp:HH:mm:ss.fff}] [{Level}] [{Category}] {Message}{ex}";
        }
    }

    /// <summary>
    /// 日志事件参数
    /// </summary>
    public class LogEventArgs : EventArgs
    {
        public LogEntry Entry { get; }

        public LogEventArgs(LogEntry entry)
        {
            Entry = entry;
        }
    }

    /// <summary>
    /// 日志记录器
    /// </summary>
    public class Logger
    {
        private static Logger _instance;
        private static readonly object _instanceLock = new();

        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private readonly List<ILogSink> _sinks = new();
        private readonly object _sinkLock = new();

        private LogLevel _minimumLevel = LogLevel.Debug;
        private int _maxEntries = 10000;

        /// <summary>
        /// 单例实例
        /// </summary>
        public static Logger Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                        {
                            _instance = new Logger();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// 最低日志级别
        /// </summary>
        public LogLevel MinimumLevel
        {
            get => _minimumLevel;
            set => _minimumLevel = value;
        }

        /// <summary>
        /// 最大日志条数
        /// </summary>
        public int MaxEntries
        {
            get => _maxEntries;
            set => _maxEntries = value;
        }

        /// <summary>
        /// 新日志事件
        /// </summary>
        public event EventHandler<LogEventArgs> LogAdded;

        private Logger()
        {
            // 默认添加控制台输出
            AddSink(new ConsoleSink());
        }

        /// <summary>
        /// 添加日志输出目标
        /// </summary>
        public void AddSink(ILogSink sink)
        {
            lock (_sinkLock)
            {
                _sinks.Add(sink);
            }
        }

        /// <summary>
        /// 移除日志输出目标
        /// </summary>
        public void RemoveSink(ILogSink sink)
        {
            lock (_sinkLock)
            {
                _sinks.Remove(sink);
            }
        }

        /// <summary>
        /// 记录日志
        /// </summary>
        public void Log(LogLevel level, string category, string message, Exception ex = null)
        {
            if (level < _minimumLevel)
                return;

            var entry = new LogEntry
            {
                Timestamp = DateTime.Now,
                Level = level,
                Category = category,
                Message = message,
                Exception = ex
            };

            _entries.Enqueue(entry);

            // 限制条数
            while (_entries.Count > _maxEntries)
            {
                _entries.TryDequeue(out _);
            }

            // 输出到所有 sink
            lock (_sinkLock)
            {
                foreach (var sink in _sinks)
                {
                    try
                    {
                        sink.Write(entry);
                    }
                    catch
                    {
                        // 忽略 sink 写入错误
                    }
                }
            }

            // 触发事件
            LogAdded?.Invoke(this, new LogEventArgs(entry));
        }

        /// <summary>
        /// Debug 级别日志
        /// </summary>
        public void Debug(string category, string message)
        {
            Log(LogLevel.Debug, category, message);
        }

        /// <summary>
        /// Info 级别日志
        /// </summary>
        public void Info(string category, string message)
        {
            Log(LogLevel.Info, category, message);
        }

        /// <summary>
        /// Warn 级别日志
        /// </summary>
        public void Warn(string category, string message)
        {
            Log(LogLevel.Warn, category, message);
        }

        /// <summary>
        /// Error 级别日志
        /// </summary>
        public void Error(string category, string message, Exception ex = null)
        {
            Log(LogLevel.Error, category, message, ex);
        }

        /// <summary>
        /// Fatal 级别日志
        /// </summary>
        public void Fatal(string category, string message, Exception ex = null)
        {
            Log(LogLevel.Fatal, category, message, ex);
        }

        /// <summary>
        /// 获取最近的日志
        /// </summary>
        public List<LogEntry> GetRecentEntries(int count = 100)
        {
            return _entries.TakeLast(count).ToList();
        }

        /// <summary>
        /// 清空日志
        /// </summary>
        public void Clear()
        {
            while (_entries.TryDequeue(out _)) { }
        }
    }

    /// <summary>
    /// 日志输出接口
    /// </summary>
    public interface ILogSink
    {
        void Write(LogEntry entry);
    }

    /// <summary>
    /// 控制台输出
    /// </summary>
    public class ConsoleSink : ILogSink
    {
        private static readonly object _lock = new();

        public void Write(LogEntry entry)
        {
            lock (_lock)
            {
                var originalColor = Console.ForegroundColor;
                Console.ForegroundColor = GetColor(entry.Level);
                Console.WriteLine(entry.ToString());
                Console.ForegroundColor = originalColor;
            }
        }

        private ConsoleColor GetColor(LogLevel level)
        {
            return level switch
            {
                LogLevel.Debug => ConsoleColor.Gray,
                LogLevel.Info => ConsoleColor.White,
                LogLevel.Warn => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Fatal => ConsoleColor.Magenta,
                _ => ConsoleColor.White
            };
        }
    }

    /// <summary>
    /// 文件输出
    /// </summary>
    public class FileSink : ILogSink
    {
        private readonly string _filePath;
        private readonly object _lock = new();
        private StreamWriter _writer;

        public FileSink(string filePath)
        {
            _filePath = filePath;
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _writer = new StreamWriter(filePath, append: true) { AutoFlush = true };
        }

        public void Write(LogEntry entry)
        {
            lock (_lock)
            {
                _writer.WriteLine(entry.ToString());
            }
        }

        public void Dispose()
        {
            _writer?.Dispose();
        }
    }
}
