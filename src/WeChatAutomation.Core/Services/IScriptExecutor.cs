using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WeChatAutomation.Core.Recording;

namespace WeChatAutomation.Core.Services
{
    public interface IScriptExecutor
    {
        /// <summary>
        /// 执行指定脚本。
        /// </summary>
        /// <param name="source">执行来源标记："CloudTask"=云端任务 / "LocalScheduler"=本地定时 / null=手动回放。
        /// 用于抢占判定：来源为 CloudTask 时会抢占正在运行的 LocalScheduler 任务。</param>
        Task<ExecuteResult> ExecuteScript(string scriptName, Dictionary<string, string> parameters = null, string source = null);

        /// <summary>当前是否正在执行脚本。</summary>
        bool IsExecuting { get; }

        /// <summary>当前执行来源（"CloudTask"/"LocalScheduler"/"Manual"/null）。用于抢占判定。</summary>
        string CurrentSource { get; }

        /// <summary>停止当前正在执行的脚本（取消回放）。</summary>
        void StopExecution();

        /// <summary>等待执行器空闲（停止后等其释放信号量），超时返回 false。</summary>
        Task<bool> WaitForIdleAsync(int timeoutMs, CancellationToken ct = default);

        /// <summary>
        /// 获取所有可用脚本列表
        /// </summary>
        List<ScriptInfo> GetAvailableScripts();

        /// <summary>
        /// 检查脚本是否存在
        /// </summary>
        bool IsScriptExist(string scriptName);

        /// <summary>
        /// 获取脚本详情
        /// </summary>
        RecordingFile GetScriptInfo(string scriptName);
    }

    public class ExecuteResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string ScriptName { get; set; }
        public DateTime ExecutedAt { get; set; }
        public List<ReadContentResult> ReadResults { get; set; } = new();
        public List<VisionDetectionResult> VisionResults { get; set; } = new();

        /// <summary>
        /// 因信号量未抢到（有其他脚本正在执行）而跳过。调用方据此决定是否重试而非推进下次调度。
        /// </summary>
        public bool SkippedDueToBusy { get; set; }
    }

    public class ScriptInfo
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public int StepCount { get; set; }
        public DateTime LastModified { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
