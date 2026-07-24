using System;
using System.IO;

namespace WeChatAutomation.Core
{
    /// <summary>
    /// 运行期路径集中解析。
    /// 关键：captures/templates 不放在 bin 输出目录下，避免 VS Clean/Rebuild 时被清空，
    /// 同时让 Debug / Release 共用同一份录制数据。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>
        /// 项目根目录：向上查找包含 .git 或 .sln 或 scripts 的目录，识别开发期根目录；
        /// 找不到时退回 AppDomain.BaseDirectory（发布版场景）。
        /// </summary>
        public static string ProjectRoot
        {
            get
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var dir = new DirectoryInfo(baseDir);
                while (dir != null)
                {
                    // 识别项目根的标志：存在 .sln 或 .git 目录
                    if (File.Exists(Path.Combine(dir.FullName, "WeChatAutomation.sln")) ||
                        Directory.Exists(Path.Combine(dir.FullName, ".git")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
                return baseDir;
            }
        }

        /// <summary>
        /// 共享 captures 目录（历史训练数据采集，开发期 Clean/Rebuild 不清空）。
        /// 开发期 = 项目根/captures；发布版 = exe 同级/captures。
        /// </summary>
        public static string CapturesDir => Path.Combine(ProjectRoot, "captures");

        /// <summary>
        /// 视觉模式模板图片目录。录制视觉点击时自动截取的模板存放于此。
        /// 开发期 = 项目根/templates；发布版 = exe 同级/templates。
        /// </summary>
        public static string TemplatesDir => Path.Combine(ProjectRoot, "templates");
    }
}
