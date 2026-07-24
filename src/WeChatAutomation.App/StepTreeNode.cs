using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using WeChatAutomation.Core.Recording;

namespace WeChatAutomation.App
{
    /// <summary>
    /// TreeView 节点包装类，用于在 MainWindow 的步骤列表中显示层级结构。
    /// If 步骤的 True/False 分支作为子节点展示。
    /// </summary>
    public class StepTreeNode : INotifyPropertyChanged
    {
        // ── 静态缓存：动作类型颜色 ──
        private static readonly SolidColorBrush BrushBlue = new(Color.FromRgb(66, 133, 244));
        private static readonly SolidColorBrush BrushGreen = new(Color.FromRgb(52, 168, 83));
        private static readonly SolidColorBrush BrushYellow = new(Color.FromRgb(251, 188, 4));
        private static readonly SolidColorBrush BrushPurple = new(Color.FromRgb(171, 71, 188));
        private static readonly SolidColorBrush BrushCyan = new(Color.FromRgb(0, 172, 193));
        private static readonly SolidColorBrush BrushOrange = new(Color.FromRgb(255, 112, 67));
        private static readonly SolidColorBrush BrushAmber = new(Color.FromRgb(255, 152, 0));
        private static readonly SolidColorBrush BrushDeepPurple = new(Color.FromRgb(156, 39, 176));
        private static readonly SolidColorBrush BrushGray = new(Color.FromRgb(158, 158, 158));

        /// <summary>关联的步骤动作。</summary>
        public RecordedAction Action { get; }

        /// <summary>分支标签："True"、"False" 或 null（顶层步骤）。</summary>
        public string? BranchLabel { get; }

        /// <summary>分支颜色：绿色(True)/红色(False)/null(顶层)。</summary>
        public Brush? BranchColor { get; }

        /// <summary>所属父 If 节点的 NodeId（嵌套步骤用）。</summary>
        public string? ParentIfNodeId { get; set; }

        /// <summary>是否为分支头节点（✓ True / ✗ False 标签行）。</summary>
        public bool IsBranchHeader { get; }

        /// <summary>显示编号（点分形式，如 "2.1"）。</summary>
        public string DisplayOrder { get; set; } = "";

        /// <summary>子节点集合。</summary>
        public ObservableCollection<StepTreeNode> Children { get; } = new();

        /// <summary>是否在 True 分支中。</summary>
        public bool IsInTrueBranch { get; }

        /// <summary>是否在 False 分支中。</summary>
        public bool IsInFalseBranch { get; }

        private bool _isRowSelected;

        /// <summary>行多选状态（用于批量操作）。</summary>
        public bool IsRowSelected
        {
            get => _isRowSelected;
            set { if (_isRowSelected != value) { _isRowSelected = value; OnPropertyChanged(nameof(IsRowSelected)); } }
        }

        // ── 表格列显示属性 ──

        /// <summary>动作类型显示名（中文简短名称）。</summary>
        public string ActionTypeDisplay => IsBranchHeader ? "" : Action.ActionType switch
        {
            ActionType.Click => "点击",
            ActionType.TypeText => "输入",
            ActionType.SendKeys => "按键",
            ActionType.Wait => "等待",
            ActionType.Copy => "复制",
            ActionType.Paste => "粘贴",
            ActionType.InsertText => "插入",
            ActionType.Screenshot => "截图",
            ActionType.OpenApp => "打开应用",
            ActionType.WaitForApp => "等待应用",
            ActionType.Scroll => "滚动",
            ActionType.ReadContent => "阅读",
            ActionType.ScrollRead => "滚动阅读",
            ActionType.InputParam => "输入参数",
            ActionType.RegexMatch => "正则识别",
            ActionType.If => "判断",
            ActionType.Goto => "跳转",
            ActionType.SwitchToWindow => "切窗",
            ActionType.While => "While循环",
            ActionType.Loop => "循环N次",
            ActionType.Try => "容错",
            ActionType.Break => "跳出",
            ActionType.Continue => "下一轮",
            ActionType.HttpWait => "等待HTTP",
            ActionType.HttpCall => "调用HTTP",
            _ => Action.ActionType.ToString()
        };

        /// <summary>动作类型对应的背景色（用于标签显示，静态缓存避免重复创建）。</summary>
        public Brush TypeBadgeBackground => IsBranchHeader ? Brushes.Transparent : Action.ActionType switch
        {
            ActionType.Click => BrushBlue,
            ActionType.TypeText => BrushGreen,
            ActionType.SendKeys => BrushGreen,
            ActionType.Wait => BrushYellow,
            ActionType.Screenshot => BrushPurple,
            ActionType.Scroll => BrushYellow,
            ActionType.ReadContent => BrushCyan,
            ActionType.ScrollRead => BrushCyan,
            ActionType.InputParam => BrushOrange,
            ActionType.RegexMatch => BrushCyan,
            ActionType.If => BrushAmber,
            ActionType.Goto => BrushDeepPurple,
            ActionType.SwitchToWindow => BrushBlue,
            ActionType.Copy => BrushGreen,
            ActionType.Paste => BrushGreen,
            ActionType.InsertText => BrushGreen,
            ActionType.OpenApp => BrushBlue,
            ActionType.WaitForApp => BrushYellow,
            ActionType.While => BrushOrange,
            ActionType.Loop => BrushOrange,
            ActionType.Try => BrushDeepPurple,
            ActionType.Break => BrushGray,
            ActionType.Continue => BrushGray,
            ActionType.HttpWait => BrushCyan,
            ActionType.HttpCall => BrushCyan,
            _ => BrushGray
        };

        /// <summary>摘要（表格"说明"列）。</summary>
        public string SummaryDisplay => IsBranchHeader ? "" : Action.Summary;

        /// <summary>步骤名字（表格"名字"列，可编辑，双向绑定写回 Action.DisplayName，用户自定义）。</summary>
        public string NameDisplay
        {
            get => IsBranchHeader ? "" : (Action.DisplayName ?? "");
            set
            {
                if (IsBranchHeader || Action == null) return;
                string? v = string.IsNullOrWhiteSpace(value) ? null : value;
                if (Action.DisplayName != v)
                {
                    Action.DisplayName = v;
                    OnPropertyChanged(nameof(NameDisplay));
                }
            }
        }

        /// <summary>备注（表格"备注"列）。</summary>
        public string RemarkDisplay => IsBranchHeader ? "" : (Action.Remark ?? "");

        /// <summary>延迟毫秒数显示。</summary>
        public string DelayDisplay => IsBranchHeader ? "" : $"{Action.DelayMs}ms";

        /// <summary>点击模式显示。</summary>
        public string ClickModeDisplay => IsBranchHeader ? "" : Action.ActionType == ActionType.Click
            ? Action.ClickMode switch
            {
                ClickMode.Coordinate => "坐标",
                ClickMode.UIAPath => "UIA路径",
                ClickMode.Vision => "视觉",
                _ => ""
            }
            : "";

        /// <summary>是否启用（供表格列绑定）。</summary>
        public bool IsEnabledDisplay => !IsBranchHeader && Action.IsEnabled;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 创建步骤节点。
        /// </summary>
        public StepTreeNode(RecordedAction action, string? branchLabel, Brush? branchColor,
            string? parentIfId, bool isTrueBranch = false, bool isFalseBranch = false)
        {
            Action = action;
            BranchLabel = branchLabel;
            BranchColor = branchColor;
            ParentIfNodeId = parentIfId;
            IsBranchHeader = false;
            IsInTrueBranch = isTrueBranch;
            IsInFalseBranch = isFalseBranch;
        }

        /// <summary>
        /// 创建分支头节点（✓ True / ✗ False 标签行）。
        /// </summary>
        public StepTreeNode(string branchLabel, Brush branchColor, string parentIfId, bool isTrueBranch)
        {
            Action = null!;
            BranchLabel = branchLabel;
            BranchColor = branchColor;
            ParentIfNodeId = parentIfId;
            IsBranchHeader = true;
            IsInTrueBranch = isTrueBranch;
            IsInFalseBranch = !isTrueBranch;
        }

        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
