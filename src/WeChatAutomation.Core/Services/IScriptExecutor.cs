using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WeChatAutomation.Core.Recording;

namespace WeChatAutomation.Core.Services
{
    public interface IScriptExecutor
    {
        /// <summary>
        /// 执行指定脚本
        /// </summary>
        Task<ExecuteResult> ExecuteScript(string scriptName, Dictionary<string, string> parameters = null);

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
