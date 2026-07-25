using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using WeChatAutomation.Core.Logging;

namespace WeChatAutomation.Core.Services
{
    /// <summary>
    /// 本地定时调度服务：按用户配置的触发方式（一次/每隔/每天/每周）到点自动执行脚本。
    /// 让步规则：云端任务(TaskPolling)执行中或执行器忙时不启动新任务；云端任务抢占由 ScriptExecutor 处理。
    /// </summary>
    public class LocalSchedulerService : IHostedService, IDisposable
    {
        private static readonly Logger _logger = Logger.Instance;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly IScriptExecutor _executor;
        private readonly TaskPollingService _taskPolling;
        private readonly IConfiguration _configuration;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();

        private Task _loopTask;
        private string _schedulesPath;
        private int _checkIntervalSeconds = 5;
        private volatile bool _isRunning;
        private List<ScheduleItem> _schedules = new();

        public bool IsRunning => _isRunning;
        public int EnabledCount => _schedules.Count(s => s.Enabled);
        public int TotalCount => _schedules.Count;

        /// <summary>调度列表变更（增删改/启用切换/NextRun 推进）时触发，UI 据此刷新。</summary>
        public event EventHandler SchedulesChanged;
        /// <summary>某调度执行完成时触发，UI 据此刷新 LastRun 与日志。</summary>
        public event EventHandler<ScheduleExecutedEventArgs> ScheduleExecuted;

        public LocalSchedulerService(IScriptExecutor executor, IConfiguration configuration, TaskPollingService taskPolling)
        {
            _executor = executor;
            _configuration = configuration;
            _taskPolling = taskPolling;
            _schedulesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schedules.json");
        }

        public Task StartAsync(CancellationToken cancellationToken)
            => StartInternal(cancellationToken, checkConfig: true);

        /// <summary>手动启动（UI 点「开启」时调用），跳过 AutoStart 配置检查。</summary>
        public Task StartManual(CancellationToken cancellationToken)
            => StartInternal(cancellationToken, checkConfig: false);

        private Task StartInternal(CancellationToken cancellationToken, bool checkConfig)
        {
            if (_isRunning) return Task.CompletedTask;

            if (checkConfig)
            {
                var autoStart = _configuration.GetValue("LocalScheduler:AutoStart", true);
                if (!autoStart)
                {
                    _logger.Info("Scheduler", "本地调度服务未设置自动启动");
                    return Task.CompletedTask;
                }
            }

            _checkIntervalSeconds = Math.Max(1, _configuration.GetValue("LocalScheduler:CheckIntervalSeconds", 5));

            Load();
            // 启动时重算过期/未计算的 NextRunAt（跳过开机前错过的历史时段）
            lock (_lock)
            {
                var now = DateTime.Now;
                foreach (var s in _schedules)
                {
                    if (!s.Enabled) continue;
                    if (s.NextRunAt == null || s.NextRunAt <= now)
                        s.NextRunAt = ComputeNextRun(s, now);
                }
            }

            _isRunning = true;
            _loopTask = Task.Run(() => SchedulerLoop(_cts.Token));
            _logger.Info("Scheduler", $"本地调度服务已启动 (检查间隔 {_checkIntervalSeconds}s, {EnabledCount}/{TotalCount} 个启用)");
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (!_isRunning) return;
            _isRunning = false;
            _cts.Cancel();
            if (_loopTask != null)
            {
                try { await _loopTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
                catch { }
            }
            _logger.Info("Scheduler", "本地调度服务已停止");
        }

        // ═══ 调度循环 ═══

        private async Task SchedulerLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_checkIntervalSeconds), ct);
                    await TickOnce();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.Warn("Scheduler", $"调度循环异常: {ex.Message}");
                }
            }
        }

        private async Task TickOnce()
        {
            // 1. 找到最早到点且启用的调度（快照，不在执行期间持锁）
            ScheduleItem target = null;
            DateTime now = DateTime.Now;
            lock (_lock)
            {
                target = _schedules
                    .Where(s => s.Enabled && s.NextRunAt != null && s.NextRunAt <= now)
                    .OrderBy(s => s.NextRunAt)
                    .FirstOrDefault();
            }
            if (target == null) return;

            // 2. 让步：云端任务执行中或执行器忙 -> 跳过本 tick（不推进 NextRunAt，下 tick 续试）
            if (_taskPolling?.IsExecuting == true || _executor.IsExecuting)
                return;

            // 3. 执行（不持锁，避免阻塞 UI 的 CRUD）
            _logger.Info("Scheduler", $"定时触发: {target.Name} -> 脚本 {target.ScriptName}");
            ExecuteResult result;
            try
            {
                result = await _executor.ExecuteScript(target.ScriptName, target.Parameters, "LocalScheduler");
            }
            catch (Exception ex)
            {
                result = new ExecuteResult { Success = false, Message = $"执行异常: {ex.Message}", ExecutedAt = DateTime.Now };
            }

            // 4. 处理结果
            lock (_lock)
            {
                // 重新查找（可能已被编辑/删除）
                var item = _schedules.FirstOrDefault(s => s.Id == target.Id);
                if (item == null) return;

                item.LastRunAt = DateTime.Now;
                item.LastRunStatus = result.Success ? "completed" : "failed";
                item.LastMessage = result.Message;
                item.LastDuration = 0; // ScriptExecutor 未回传耗时，留空

                if (result.SkippedDueToBusy)
                {
                    // 没抢到锁（手动回放/云端刚好抢占），不推进，下 tick 续试
                    item.LastRunStatus = "skipped";
                    return;
                }

                // 已实际执行（成功/失败/被抢占取消）-> 推进 NextRunAt
                item.NextRunAt = ComputeNextRun(item, DateTime.Now);
                if (item.NextRunAt == null)
                {
                    // Once 到期 -> 禁用
                    item.Enabled = false;
                    _logger.Info("Scheduler", $"一次性调度已完成，已禁用: {item.Name}");
                }
                Save();
            }

            ScheduleExecuted?.Invoke(this, new ScheduleExecutedEventArgs
            {
                ScheduleId = target.Id,
                ScriptName = target.ScriptName,
                Success = result.Success,
                Message = result.Message,
                RunAt = DateTime.Now
            });
            SchedulesChanged?.Invoke(this, EventArgs.Empty);
        }

        // ═══ 触发时间计算 ═══

        /// <summary>计算 from 之后（含）的下次运行时刻；Once 已过期返回 null。</summary>
        public static DateTime? ComputeNextRun(ScheduleItem s, DateTime from)
        {
            switch (s.TriggerType)
            {
                case ScheduleTriggerType.Once:
                    return s.OnceAt >= from ? s.OnceAt : null;

                case ScheduleTriggerType.Interval:
                    {
                        int secs = Math.Max(1, s.IntervalSeconds);
                        return from.AddSeconds(secs);
                    }

                case ScheduleTriggerType.Daily:
                    {
                        var t = ParseTime(s.DailyTime) ?? new TimeSpan(9, 0, 0);
                        var today = from.Date.Add(t);
                        return today >= from ? today : today.AddDays(1);
                    }

                case ScheduleTriggerType.Weekly:
                    {
                        var t = ParseTime(s.WeeklyTime) ?? new TimeSpan(9, 0, 0);
                        var days = s.WeeklyDays?.Where(d => d >= 1 && d <= 7).Distinct().ToList();
                        if (days == null || days.Count == 0) return null;
                        for (int i = 0; i < 8; i++)
                        {
                            var d = from.Date.AddDays(i).Add(t);
                            if (d >= from && days.Contains((int)d.DayOfWeek == 0 ? 7 : (int)d.DayOfWeek))
                                return d;
                        }
                        return null;
                    }
            }
            return null;
        }

        private static TimeSpan? ParseTime(string hhmm)
        {
            if (string.IsNullOrWhiteSpace(hhmm)) return null;
            var parts = hhmm.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            if (int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
            {
                if (h >= 0 && h < 24 && m >= 0 && m < 60) return new TimeSpan(h, m, 0);
            }
            return null;
        }

        // ═══ 持久化 ═══

        private void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_schedulesPath))
                    {
                        var json = File.ReadAllText(_schedulesPath);
                        var doc = JsonSerializer.Deserialize<ScheduleFile>(json, _jsonOptions);
                        _schedules = doc?.Schedules ?? new List<ScheduleItem>();
                    }
                    else
                    {
                        _schedules = new List<ScheduleItem>();
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn("Scheduler", $"加载 schedules.json 失败: {ex.Message}");
                    _schedules = new List<ScheduleItem>();
                }
            }
        }

        private void Save()
        {
            try
            {
                var doc = new ScheduleFile { Schedules = _schedules.ToList() };
                var json = JsonSerializer.Serialize(doc, _jsonOptions);
                var tmp = _schedulesPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_schedulesPath)) File.Replace(tmp, _schedulesPath, null);
                else File.Move(tmp, _schedulesPath);
            }
            catch (Exception ex)
            {
                _logger.Warn("Scheduler", $"保存 schedules.json 失败: {ex.Message}");
            }
        }

        // ═══ CRUD（UI 调用） ═══

        public List<ScheduleItem> GetSchedules()
        {
            lock (_lock) { return _schedules.ToList(); }
        }

        public void AddOrUpdateSchedule(ScheduleItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ScriptName)) return;
            lock (_lock)
            {
                if (string.IsNullOrEmpty(item.Id))
                    item.Id = Guid.NewGuid().ToString("N")[..12];
                item.CreatedAt = item.CreatedAt == default ? DateTime.Now : item.CreatedAt;
                item.NextRunAt = item.Enabled ? ComputeNextRun(item, DateTime.Now) : null;

                var idx = _schedules.FindIndex(s => s.Id == item.Id);
                if (idx >= 0) _schedules[idx] = item;
                else _schedules.Add(item);
                Save();
            }
            SchedulesChanged?.Invoke(this, EventArgs.Empty);
            _logger.Info("Scheduler", $"已保存调度: {item.Name} ({item.TriggerType})");
        }

        public void DeleteSchedule(string id)
        {
            lock (_lock)
            {
                var idx = _schedules.FindIndex(s => s.Id == id);
                if (idx >= 0) { _schedules.RemoveAt(idx); Save(); }
            }
            SchedulesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetEnabled(string id, bool enabled)
        {
            lock (_lock)
            {
                var item = _schedules.FirstOrDefault(s => s.Id == id);
                if (item == null || item.Enabled == enabled) return;
                item.Enabled = enabled;
                item.NextRunAt = enabled ? ComputeNextRun(item, DateTime.Now) : null;
                Save();
            }
            SchedulesChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
    }

    // ═══ 模型 ═══

    public class ScheduleFile
    {
        public List<ScheduleItem> Schedules { get; set; } = new();
    }

    public class ScheduleItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ScriptName { get; set; }
        public bool Enabled { get; set; } = true;

        /// <summary>预设参数值（无人值守执行）。键=参数名，值=参数值。</summary>
        public Dictionary<string, string> Parameters { get; set; } = new();

        public ScheduleTriggerType TriggerType { get; set; } = ScheduleTriggerType.Daily;

        /// <summary>Interval 触发：间隔秒数（分钟/小时在 UI 换算后存这里）。</summary>
        public int IntervalSeconds { get; set; } = 3600;

        /// <summary>Daily 触发：HH:mm。</summary>
        public string DailyTime { get; set; } = "09:00";

        /// <summary>Weekly 触发：星期几列表，1=周一..7=周日。</summary>
        public List<int> WeeklyDays { get; set; } = new() { 1 };

        /// <summary>Weekly 触发：HH:mm。</summary>
        public string WeeklyTime { get; set; } = "09:00";

        /// <summary>Once 触发：具体时刻。</summary>
        public DateTime OnceAt { get; set; }

        // 运行期状态
        public DateTime? NextRunAt { get; set; }
        public DateTime? LastRunAt { get; set; }
        public string LastRunStatus { get; set; }   // completed / failed / skipped
        public string LastMessage { get; set; }
        public double LastDuration { get; set; }
        public DateTime CreatedAt { get; set; }

        [JsonIgnore]
        public string TriggerSummary
        {
            get
            {
                switch (TriggerType)
                {
                    case ScheduleTriggerType.Once:
                        return $"一次 {OnceAt:yyyy-MM-dd HH:mm}";
                    case ScheduleTriggerType.Interval:
                        {
                            int secs = IntervalSeconds;
                            if (secs >= 3600 && secs % 3600 == 0) return $"每隔 {secs / 3600} 时";
                            if (secs >= 60 && secs % 60 == 0) return $"每隔 {secs / 60} 分";
                            return $"每隔 {secs} 秒";
                        }
                    case ScheduleTriggerType.Daily:
                        return $"每天 {DailyTime}";
                    case ScheduleTriggerType.Weekly:
                        {
                            var names = new[] { "", "一", "二", "三", "四", "五", "六", "日" };
                            var days = (WeeklyDays ?? new()).OrderBy(d => d).Select(d => d >= 1 && d <= 7 ? names[d] : "?");
                            return $"每周 {string.Join("/", days)} {WeeklyTime}";
                        }
                }
                return TriggerType.ToString();
            }
        }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ScheduleTriggerType
    {
        Once,
        Interval,
        Daily,
        Weekly
    }

    public class ScheduleExecutedEventArgs : EventArgs
    {
        public string ScheduleId { get; set; }
        public string ScriptName { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public DateTime RunAt { get; set; }
    }
}
