using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using WeChatAutomation.Core.Logging;

namespace WeChatAutomation.Core.Services
{
    /// <summary>
    /// 进程级等待信号中心：HttpWait 步骤注册一个 key 等待，外部 HTTP 请求带该 key 唤醒并传参。
    /// 用于流程编排中的"暂停等外部触发"场景（人工确认、外部系统回调等）。
    /// </summary>
    public static class WaitSignalHub
    {
        private static readonly Logger _logger = Logger.Instance;

        // key -> 等待任务源。同一 key 只保留最近一个等待者（后注册覆盖并取消前者）。
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _waiters = new();

        /// <summary>
        /// 注册等待指定 key 的外部信号。返回唤醒时携带的 payload（可空）。
        /// timeoutMs=0 表示无限等待（仍受 ct 取消）。超时抛 TimeoutException。
        /// </summary>
        public static async Task<string?> WaitAsync(string key, int timeoutMs, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("等待 key 不能为空", nameof(key));

            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            // 若同 key 已有等待者，取消旧的（避免堆积）
            if (_waiters.TryGetValue(key, out var existing))
                existing.TrySetCanceled();
            _waiters[key] = tcs;

            _logger.Info("WaitSignal", $"开始等待信号: key={key}, 超时={(timeoutMs > 0 ? timeoutMs + "ms" : "无限")}");

            using var reg = ct.Register(() => tcs.TrySetCanceled());

            try
            {
                if (timeoutMs > 0)
                {
                    var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, ct));
                    if (completed != tcs.Task)
                    {
                        _waiters.TryRemove(key, out _);
                        throw new TimeoutException($"等待信号超时: key={key}");
                    }
                }
                return await tcs.Task;
            }
            finally
            {
                // 清理：仅移除自身（避免误删后注册的同 key 等待者）
                if (_waiters.TryGetValue(key, out var cur) && ReferenceEquals(cur, tcs))
                    _waiters.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// 唤醒等待指定 key 的信号，携带 payload。返回是否存在等待者。
        /// </summary>
        public static bool Signal(string key, string? payload)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (_waiters.TryGetValue(key, out var tcs))
            {
                bool set = tcs.TrySetResult(payload);
                if (set) _logger.Info("WaitSignal", $"信号已送达: key={key}");
                return set;
            }
            _logger.Warn("WaitSignal", $"无等待者: key={key}");
            return false;
        }

        /// <summary>当前等待中的 key 列表（供状态查询）。</summary>
        public static System.Collections.Generic.IReadOnlyCollection<string> PendingKeys
            => new System.Collections.Generic.List<string>(_waiters.Keys);
    }
}
