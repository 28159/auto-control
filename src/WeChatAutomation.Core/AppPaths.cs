using System;
using System.IO;

namespace WeChatAutomation.Core
{
    /// <summary>
    /// 运行期路径集中解析。
    /// 关键：captures 不放在 bin 输出目录下，避免 VS Clean/Rebuild 时被清空，
    /// 同时让 Debug / Release 共用同一份录制数据。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>
        /// 项目根目录：向上查找包含 yolo_train（含 label_and_train.py）的目录。
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
                    string yt = Path.Combine(dir.FullName, "yolo_train");
                    if (Directory.Exists(yt) && File.Exists(Path.Combine(yt, "label_and_train.py")))
                        return dir.FullName;
                    dir = dir.Parent;
                }
                return baseDir;
            }
        }

        /// <summary>
        /// 共享 captures 目录。
        /// 开发期 = 项目根/captures（Clean/Rebuild 不会清空，Debug/Release 共用）；
        /// 发布版 = exe 同级/captures。
        /// </summary>
        public static string CapturesDir => Path.Combine(ProjectRoot, "captures");
    }
}
