"""
YOLOv8 UI 元素检测 — 可视化训练界面

用法:
  python train_gui.py

功能:
  - 数据采集：从 captures/ 整理数据集
  - 训练配置：可视化设置参数，CPU/GPU 选择
  - 一键训练：实时显示训练进度和 loss 曲线
  - 模型导出：ONNX 导出 + 量化
  - 模型部署：复制到 C# 项目
"""

import sys
import os
import threading
import subprocess
import json
import shutil
from pathlib import Path

import tkinter as tk
from tkinter import ttk, filedialog, messagebox, scrolledtext

CLASSES = [
    "button", "input", "checkbox", "radio", "dropdown",
    "tab", "menu_item", "icon", "link", "text_field",
    "search_box", "send_button", "close_button", "minimize_button",
    "maximize_button", "scrollbar", "slider", "toggle", "tooltip", "image"
]


class TrainGUI:
    def __init__(self, root):
        self.root = root
        self.root.title("YOLOv8 UI 检测训练工具")
        self.root.geometry("960x720")
        self.root.minsize(800, 600)

        self.process = None
        self.training = False
        self.base_dir = Path(__file__).parent
        self.project_dir = self.base_dir.parent

        self._build_ui()
        self._check_env()

    def _build_ui(self):
        style = ttk.Style()
        style.configure("Title.TLabel", font=("Microsoft YaHei UI", 12, "bold"))
        style.configure("Status.TLabel", font=("Microsoft YaHei UI", 10))
        style.configure("Big.TButton", font=("Microsoft YaHei UI", 10))

        notebook = ttk.Notebook(self.root)
        notebook.pack(fill=tk.BOTH, expand=True, padx=8, pady=8)

        self._build_data_tab(notebook)
        self._build_train_tab(notebook)
        self._build_export_tab(notebook)
        self._build_deploy_tab(notebook)

        self.status_var = tk.StringVar(value="就绪")
        status_bar = ttk.Label(self.root, textvariable=self.status_var, style="Status.TLabel", relief=tk.SUNKEN, anchor=tk.W)
        status_bar.pack(fill=tk.X, padx=8, pady=(0, 8))

    # ═══ 数据采集 Tab ═══
    def _build_data_tab(self, notebook):
        frame = ttk.Frame(notebook, padding=12)
        notebook.add(frame, text="  📊 数据采集  ")

        ttk.Label(frame, text="数据采集与整理", style="Title.TLabel").grid(row=0, column=0, columnspan=3, sticky=tk.W, pady=(0, 10))

        ttk.Label(frame, text="截图目录:").grid(row=1, column=0, sticky=tk.W, pady=4)
        self.captures_var = tk.StringVar(value=str(self.base_dir / ".." / "captures"))
        ttk.Entry(frame, textvariable=self.captures_var, width=50).grid(row=1, column=1, sticky=tk.EW, padx=4, pady=4)
        ttk.Button(frame, text="浏览", command=self._browse_captures).grid(row=1, column=2, padx=4)

        ttk.Label(frame, text="输出目录:").grid(row=2, column=0, sticky=tk.W, pady=4)
        self.dataset_var = tk.StringVar(value=str(self.base_dir / "datasets"))
        ttk.Entry(frame, textvariable=self.dataset_var, width=50).grid(row=2, column=1, sticky=tk.EW, padx=4, pady=4)

        ttk.Label(frame, text="验证集比例:").grid(row=3, column=0, sticky=tk.W, pady=4)
        self.val_ratio_var = tk.StringVar(value="0.2")
        ttk.Entry(frame, textvariable=self.val_ratio_var, width=10).grid(row=3, column=1, sticky=tk.W, padx=4, pady=4)

        ttk.Label(frame, text="随机种子:").grid(row=4, column=0, sticky=tk.W, pady=4)
        self.seed_var = tk.StringVar(value="42")
        ttk.Entry(frame, textvariable=self.seed_var, width=10).grid(row=4, column=1, sticky=tk.W, padx=4, pady=4)

        btn_frame = ttk.Frame(frame)
        btn_frame.grid(row=5, column=0, columnspan=3, pady=10)
        ttk.Button(btn_frame, text="🔄 整理数据集", command=self._collect_data, style="Big.TButton").pack(side=tk.LEFT, padx=4)
        ttk.Button(btn_frame, text="🧹 清空重建", command=self._clean_collect, style="Big.TButton").pack(side=tk.LEFT, padx=4)
        ttk.Button(btn_frame, text="🎲 生成模拟数据", command=self._generate_mock, style="Big.TButton").pack(side=tk.LEFT, padx=4)

        ttk.Label(frame, text="数据统计:", font=("Microsoft YaHei UI", 10, "bold")).grid(row=6, column=0, sticky=tk.W, pady=(10, 4))
        self.data_stats = scrolledtext.ScrolledText(frame, height=15, font=("Consolas", 9))
        self.data_stats.grid(row=7, column=0, columnspan=3, sticky=tk.NSEW, pady=4)

        frame.columnconfigure(1, weight=1)
        frame.rowconfigure(7, weight=1)

    # ═══ 训练 Tab ═══
    def _build_train_tab(self, notebook):
        frame = ttk.Frame(notebook, padding=12)
        notebook.add(frame, text="  🎯 模型训练  ")

        params = ttk.LabelFrame(frame, text="训练参数", padding=8)
        params.pack(fill=tk.X, pady=(0, 8))

        row = 0
        ttk.Label(params, text="模型变体:").grid(row=row, column=0, sticky=tk.W, pady=3)
        self.model_var = tk.StringVar(value="yolov8n")
        model_combo = ttk.Combobox(params, textvariable=self.model_var, values=["yolov8n", "yolov8s", "yolov8m", "yolov8l", "yolov8x"], width=12, state="readonly")
        model_combo.grid(row=row, column=1, sticky=tk.W, padx=4, pady=3)

        ttk.Label(params, text="训练轮数:").grid(row=row, column=2, sticky=tk.W, padx=(20, 0), pady=3)
        self.epochs_var = tk.StringVar(value="50")
        ttk.Entry(params, textvariable=self.epochs_var, width=8).grid(row=row, column=3, sticky=tk.W, padx=4, pady=3)

        row = 1
        ttk.Label(params, text="批大小:").grid(row=row, column=0, sticky=tk.W, pady=3)
        self.batch_var = tk.StringVar(value="8")
        ttk.Entry(params, textvariable=self.batch_var, width=8).grid(row=row, column=1, sticky=tk.W, padx=4, pady=3)

        ttk.Label(params, text="图像尺寸:").grid(row=row, column=2, sticky=tk.W, padx=(20, 0), pady=3)
        self.imgsz_var = tk.StringVar(value="640")
        ttk.Entry(params, textvariable=self.imgsz_var, width=8).grid(row=row, column=3, sticky=tk.W, padx=4, pady=3)

        row = 2
        ttk.Label(params, text="训练设备:").grid(row=row, column=0, sticky=tk.W, pady=3)
        self.device_var = tk.StringVar(value="cpu")
        device_combo = ttk.Combobox(params, textvariable=self.device_var, values=["cpu", "0", "1"], width=12, state="readonly")
        device_combo.grid(row=row, column=1, sticky=tk.W, padx=4, pady=3)

        ttk.Label(params, text="工作线程:").grid(row=row, column=2, sticky=tk.W, padx=(20, 0), pady=3)
        self.workers_var = tk.StringVar(value="4")
        ttk.Entry(params, textvariable=self.workers_var, width=8).grid(row=row, column=3, sticky=tk.W, padx=4, pady=3)

        row = 3
        ttk.Label(params, text="初始学习率:").grid(row=row, column=0, sticky=tk.W, pady=3)
        self.lr0_var = tk.StringVar(value="0.01")
        ttk.Entry(params, textvariable=self.lr0_var, width=8).grid(row=row, column=1, sticky=tk.W, padx=4, pady=3)

        ttk.Label(params, text="早停耐心值:").grid(row=row, column=2, sticky=tk.W, padx=(20, 0), pady=3)
        self.patience_var = tk.StringVar(value="20")
        ttk.Entry(params, textvariable=self.patience_var, width=8).grid(row=row, column=3, sticky=tk.W, padx=4, pady=3)

        row = 4
        ttk.Label(params, text="数据集配置:").grid(row=row, column=0, sticky=tk.W, pady=3)
        self.data_yaml_var = tk.StringVar(value=str(self.base_dir / "dataset.yaml"))
        ttk.Entry(params, textvariable=self.data_yaml_var, width=40).grid(row=row, column=1, columnspan=2, sticky=tk.EW, padx=4, pady=3)
        ttk.Button(params, text="浏览", command=self._browse_yaml).grid(row=row, column=3, padx=4)

        row = 5
        ttk.Label(params, text="恢复训练:").grid(row=row, column=0, sticky=tk.W, pady=3)
        self.resume_var = tk.StringVar(value="")
        ttk.Entry(params, textvariable=self.resume_var, width=40).grid(row=row, column=1, columnspan=2, sticky=tk.EW, padx=4, pady=3)
        ttk.Button(params, text="浏览", command=self._browse_resume).grid(row=row, column=3, padx=4)

        params.columnconfigure(1, weight=1)
        params.columnconfigure(2, weight=1)

        btn_frame = ttk.Frame(frame)
        btn_frame.pack(fill=tk.X, pady=8)
        self.train_btn = ttk.Button(btn_frame, text="▶ 开始训练", command=self._start_train, style="Big.TButton")
        self.train_btn.pack(side=tk.LEFT, padx=4)
        self.stop_btn = ttk.Button(btn_frame, text="⏹ 停止训练", command=self._stop_train, style="Big.TButton", state=tk.DISABLED)
        self.stop_btn.pack(side=tk.LEFT, padx=4)

        self.progress_var = tk.DoubleVar(value=0)
        self.progress = ttk.Progressbar(frame, variable=self.progress_var, maximum=100)
        self.progress.pack(fill=tk.X, pady=4)

        ttk.Label(frame, text="训练日志:", font=("Microsoft YaHei UI", 10, "bold")).pack(anchor=tk.W)
        self.train_log = scrolledtext.ScrolledText(frame, height=12, font=("Consolas", 9))
        self.train_log.pack(fill=tk.BOTH, expand=True, pady=4)

    # ═══ 导出 Tab ═══
    def _build_export_tab(self, notebook):
        frame = ttk.Frame(notebook, padding=12)
        notebook.add(frame, text="  📦 模型导出  ")

        ttk.Label(frame, text="ONNX 模型导出", style="Title.TLabel").grid(row=0, column=0, columnspan=3, sticky=tk.W, pady=(0, 10))

        ttk.Label(frame, text="PyTorch 模型:").grid(row=1, column=0, sticky=tk.W, pady=4)
        self.pt_model_var = tk.StringVar(value=str(self.base_dir / "runs" / "detect" / "runs" / "train" / "weights" / "best.pt"))
        ttk.Entry(frame, textvariable=self.pt_model_var, width=50).grid(row=1, column=1, sticky=tk.EW, padx=4, pady=4)
        ttk.Button(frame, text="浏览", command=self._browse_pt).grid(row=1, column=2, padx=4)

        ttk.Label(frame, text="ONNX 输出路径:").grid(row=2, column=0, sticky=tk.W, pady=4)
        self.onnx_output_var = tk.StringVar(value=str(self.base_dir / "models" / "yolov8n-ui.onnx"))
        ttk.Entry(frame, textvariable=self.onnx_output_var, width=50).grid(row=2, column=1, sticky=tk.EW, padx=4, pady=4)
        ttk.Button(frame, text="浏览", command=self._browse_onnx_output).grid(row=2, column=2, padx=4)

        ttk.Label(frame, text="ONNX opset:").grid(row=3, column=0, sticky=tk.W, pady=4)
        self.opset_var = tk.StringVar(value="12")
        ttk.Entry(frame, textvariable=self.opset_var, width=8).grid(row=3, column=1, sticky=tk.W, padx=4, pady=4)

        ttk.Label(frame, text="量化方式:").grid(row=4, column=0, sticky=tk.W, pady=4)
        self.quantize_var = tk.StringVar(value="不量化")
        quant_combo = ttk.Combobox(frame, textvariable=self.quantize_var, values=["不量化", "int8", "fp16"], width=12, state="readonly")
        quant_combo.grid(row=4, column=1, sticky=tk.W, padx=4, pady=4)

        btn_frame = ttk.Frame(frame)
        btn_frame.grid(row=5, column=0, columnspan=3, pady=10)
        ttk.Button(btn_frame, text="📦 导出 ONNX", command=self._export_onnx, style="Big.TButton").pack(side=tk.LEFT, padx=4)
        ttk.Button(btn_frame, text="✅ 验证模型", command=self._validate_onnx, style="Big.TButton").pack(side=tk.LEFT, padx=4)

        ttk.Label(frame, text="导出日志:", font=("Microsoft YaHei UI", 10, "bold")).grid(row=6, column=0, columnspan=3, sticky=tk.W, pady=(10, 4))
        self.export_log = scrolledtext.ScrolledText(frame, height=12, font=("Consolas", 9))
        self.export_log.grid(row=7, column=0, columnspan=3, sticky=tk.NSEW, pady=4)

        frame.columnconfigure(1, weight=1)
        frame.rowconfigure(7, weight=1)

    # ═══ 部署 Tab ═══
    def _build_deploy_tab(self, notebook):
        frame = ttk.Frame(notebook, padding=12)
        notebook.add(frame, text="  🚀 部署到项目  ")

        ttk.Label(frame, text="部署 ONNX 模型到 C# 项目", style="Title.TLabel").grid(row=0, column=0, columnspan=3, sticky=tk.W, pady=(0, 10))

        ttk.Label(frame, text="ONNX 模型文件:").grid(row=1, column=0, sticky=tk.W, pady=4)
        self.deploy_onnx_var = tk.StringVar(value=str(self.base_dir / "models" / "yolov8n-ui.onnx"))
        ttk.Entry(frame, textvariable=self.deploy_onnx_var, width=50).grid(row=1, column=1, sticky=tk.EW, padx=4, pady=4)
        ttk.Button(frame, text="浏览", command=self._browse_deploy_onnx).grid(row=1, column=2, padx=4)

        ttk.Label(frame, text="C# 输出目录:").grid(row=2, column=0, sticky=tk.W, pady=4)
        self.csharp_dir_var = tk.StringVar(value=str(self.project_dir / "src" / "WeChatAutomation.App" / "bin" / "Debug" / "net9.0-windows"))
        ttk.Entry(frame, textvariable=self.csharp_dir_var, width=50).grid(row=2, column=1, sticky=tk.EW, padx=4, pady=4)
        ttk.Button(frame, text="浏览", command=self._browse_csharp_dir).grid(row=2, column=2, padx=4)

        btn_frame = ttk.Frame(frame)
        btn_frame.grid(row=3, column=0, columnspan=3, pady=10)
        ttk.Button(btn_frame, text="🚀 部署模型", command=self._deploy_model, style="Big.TButton").pack(side=tk.LEFT, padx=4)
        ttk.Button(btn_frame, text="📂 打开模型目录", command=self._open_models_dir, style="Big.TButton").pack(side=tk.LEFT, padx=4)

        ttk.Label(frame, text="部署说明:", font=("Microsoft YaHei UI", 10, "bold")).grid(row=4, column=0, columnspan=3, sticky=tk.W, pady=(15, 4))

        info = scrolledtext.ScrolledText(frame, height=14, font=("Microsoft YaHei UI", 9), wrap=tk.WORD)
        info.grid(row=5, column=0, columnspan=3, sticky=tk.NSEW, pady=4)
        info.insert(tk.END, """完整使用流程：

1️⃣  数据采集
  • 在 C# 录制器中勾选「采集训练数据」
  • 切换到「视觉模式」录制操作
  • 或点击「生成模拟数据」快速验证

2️⃣  整理数据集
  • 点击「整理数据集」将截图+标注划分训练/验证集
  • 查看数据统计，确认各类别数量充足

3️⃣  训练模型
  • 设置训练参数（CPU 建议: batch=8, epochs=50）
  • 点击「开始训练」，实时查看训练进度
  • 训练完成后 best.pt 自动保存

4️⃣  导出 ONNX
  • 选择训练产出的 best.pt
  • 设置 ONNX 输出路径
  • 可选 int8 量化减小模型体积
  • 点击「导出 ONNX」

5️⃣  部署到 C# 项目
  • 点击「部署模型」将 ONNX 复制到 C# 输出目录
  • C# 录制器使用「视觉模式」时自动加载模型
  • 步骤编辑中设置 VisionLabel（如 button, send_button）

提示：
  • 每类 UI 元素建议至少 50 张标注
  • CPU 训练约 30-60 分钟/50 轮
  • 有 GPU 时训练速度可提升 10-50 倍
""")
        info.config(state=tk.DISABLED)

        frame.columnconfigure(1, weight=1)
        frame.rowconfigure(5, weight=1)

    # ═══ 环境检查 ═══
    def _check_env(self):
        try:
            import ultralytics
            import torch
            cuda = torch.cuda.is_available()
            if cuda:
                gpu_name = torch.cuda.get_device_name(0)
                vram = torch.cuda.get_device_properties(0).total_mem / 1024**3
                self.status_var.set(f"环境就绪 | ultralytics {ultralytics.__version__} | torch {torch.__version__} | GPU: {gpu_name} ({vram:.1f}GB)")
                self.device_var.set("0")
            else:
                self.status_var.set(f"环境就绪 | ultralytics {ultralytics.__version__} | torch {torch.__version__} | CPU 模式")
        except ImportError as e:
            self.status_var.set(f"环境不完整: {e}，请运行 pip install ultralytics")

    # ═══ 数据采集方法 ═══
    def _browse_captures(self):
        d = filedialog.askdirectory(title="选择截图目录")
        if d:
            self.captures_var.set(d)

    def _collect_data(self):
        self._run_script(f'python collect_data.py --captures "{self.captures_var.get()}" --output "{self.dataset_var.get()}" --val-ratio {self.val_ratio_var.get()} --seed {self.seed_var.get()}', self.data_stats)

    def _clean_collect(self):
        self._run_script(f'python collect_data.py --captures "{self.captures_var.get()}" --output "{self.dataset_var.get()}" --val-ratio {self.val_ratio_var.get()} --seed {self.seed_var.get()} --clean', self.data_stats)

    def _generate_mock(self):
        mock_dir = str(self.base_dir / ".." / "captures" / "mock")
        self.captures_var.set(mock_dir)
        self._run_script(f'python generate_mock_data.py --count 200 --output "{mock_dir}"', self.data_stats)

    # ═══ 训练方法 ═══
    def _browse_yaml(self):
        f = filedialog.askopenfilename(title="选择数据集配置", filetypes=[("YAML", "*.yaml *.yml")])
        if f:
            self.data_yaml_var.set(f)

    def _browse_resume(self):
        f = filedialog.askopenfilename(title="选择恢复检查点", filetypes=[("PyTorch", "*.pt")])
        if f:
            self.resume_var.set(f)

    def _start_train(self):
        if self.training:
            return

        cmd = [
            sys.executable, "train.py",
            "--data", self.data_yaml_var.get(),
            "--model", self.model_var.get(),
            "--epochs", self.epochs_var.get(),
            "--batch", self.batch_var.get(),
            "--imgsz", self.imgsz_var.get(),
            "--device", self.device_var.get(),
            "--workers", self.workers_var.get(),
            "--lr0", self.lr0_var.get(),
            "--patience", self.patience_var.get(),
            "--exist-ok",
        ]

        resume = self.resume_var.get().strip()
        if resume and Path(resume).exists():
            cmd.extend(["--resume", resume])

        self.training = True
        self.train_btn.config(state=tk.DISABLED)
        self.stop_btn.config(state=tk.NORMAL)
        self.train_log.delete("1.0", tk.END)
        self.progress_var.set(0)

        self.status_var.set("训练中...")

        threading.Thread(target=self._run_training, args=(cmd,), daemon=True).start()

    def _run_training(self, cmd):
        try:
            self.process = subprocess.Popen(
                cmd, cwd=str(self.base_dir),
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                text=True, encoding="utf-8", errors="replace",
                bufsize=1
            )

            total_epochs = int(self.epochs_var.get())
            for line in self.process.stdout:
                line = line.rstrip()
                if not line:
                    continue

                clean = self._strip_ansi(line)
                self.root.after(0, self._append_train_log, clean + "\n")

                if "Epoch" in clean and "/50" in clean:
                    try:
                        parts = clean.split()
                        for p in parts:
                            if "/" in p and p[0].isdigit():
                                current = int(p.split("/")[0])
                                self.root.after(0, self.progress_var.set, current / total_epochs * 100)
                                break
                    except (ValueError, IndexError):
                        pass

                if "mAP50" in clean:
                    self.root.after(0, self._append_train_log, "---\n")

            self.process.wait()

        except Exception as e:
            self.root.after(0, self._append_train_log, f"训练异常: {e}\n")
        finally:
            self.training = False
            self.process = None
            self.root.after(0, self._finish_train)

    def _stop_train(self):
        if self.process:
            self.process.terminate()
            self._append_train_log("\n训练已手动停止\n")
            self.training = False
            self.train_btn.config(state=tk.NORMAL)
            self.stop_btn.config(state=tk.DISABLED)

    def _finish_train(self):
        self.train_btn.config(state=tk.NORMAL)
        self.stop_btn.config(state=tk.DISABLED)
        self.progress_var.set(100)

        best_pt = self.base_dir / "runs" / "detect" / "runs" / "train" / "weights" / "best.pt"
        if best_pt.exists():
            self.pt_model_var.set(str(best_pt))
            self.status_var.set(f"训练完成! 最佳模型: {best_pt}")
        else:
            self.status_var.set("训练已结束")

    def _append_train_log(self, text):
        self.train_log.insert(tk.END, text)
        self.train_log.see(tk.END)

    # ═══ 导出方法 ═══
    def _browse_pt(self):
        f = filedialog.askopenfilename(title="选择 PyTorch 模型", filetypes=[("PyTorch", "*.pt")])
        if f:
            self.pt_model_var.set(f)

    def _browse_onnx_output(self):
        f = filedialog.asksaveasfilename(title="ONNX 输出路径", defaultextension=".onnx", filetypes=[("ONNX", "*.onnx")])
        if f:
            self.onnx_output_var.set(f)

    def _export_onnx(self):
        cmd = [
            sys.executable, "export_onnx.py",
            "--model", self.pt_model_var.get(),
            "--output", self.onnx_output_var.get(),
            "--opset", self.opset_var.get(),
            "--validate",
        ]
        if self.quantize_var.get() != "不量化":
            cmd.extend(["--quantize", self.quantize_var.get()])

        self._run_script_cmd(cmd, self.export_log, "导出 ONNX")

    def _validate_onnx(self):
        cmd = [
            sys.executable, "export_onnx.py",
            "--model", self.pt_model_var.get(),
            "--output", self.onnx_output_var.get(),
            "--skip-train",
            "--validate",
        ]
        self._run_script_cmd(cmd, self.export_log, "验证模型")

    # ═══ 部署方法 ═══
    def _browse_deploy_onnx(self):
        f = filedialog.askopenfilename(title="选择 ONNX 模型", filetypes=[("ONNX", "*.onnx")])
        if f:
            self.deploy_onnx_var.set(f)

    def _browse_csharp_dir(self):
        d = filedialog.askdirectory(title="选择 C# 输出目录")
        if d:
            self.csharp_dir_var.set(d)

    def _deploy_model(self):
        src = Path(self.deploy_onnx_var.get())
        dst_dir = Path(self.csharp_dir_var.get()) / "models"

        if not src.exists():
            messagebox.showerror("错误", f"模型文件不存在: {src}")
            return

        dst_dir.mkdir(parents=True, exist_ok=True)
        dst = dst_dir / src.name
        shutil.copy2(src, dst)

        size_mb = dst.stat().st_size / 1024 / 1024
        messagebox.showinfo("部署成功", f"模型已部署到:\n{dst}\n大小: {size_mb:.1f} MB\n\nC# 项目使用视觉模式时将自动加载此模型。")
        self.status_var.set(f"部署完成: {dst} ({size_mb:.1f} MB)")

    def _open_models_dir(self):
        models_dir = Path(self.deploy_onnx_var.get()).parent
        if models_dir.exists():
            os.startfile(str(models_dir))

    # ═══ 通用方法 ═══
    def _run_script(self, cmd_str, output_widget):
        output_widget.delete("1.0", tk.END)
        self.status_var.set("执行中...")

        def worker():
            try:
                result = subprocess.run(
                    cmd_str, cwd=str(self.base_dir),
                    shell=True, capture_output=True, text=True,
                    encoding="utf-8", errors="replace"
                )
                output = result.stdout or ""
                if result.stderr:
                    output += "\n" + result.stderr
                self.root.after(0, lambda: output_widget.insert(tk.END, output))
                self.root.after(0, lambda: self.status_var.set("执行完成"))
            except Exception as e:
                self.root.after(0, lambda: output_widget.insert(tk.END, f"错误: {e}"))
                self.root.after(0, lambda: self.status_var.set("执行失败"))

        threading.Thread(target=worker, daemon=True).start()

    def _run_script_cmd(self, cmd, output_widget, label=""):
        output_widget.delete("1.0", tk.END)
        self.status_var.set(f"{label}中...")

        def worker():
            try:
                proc = subprocess.Popen(
                    cmd, cwd=str(self.base_dir),
                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                    text=True, encoding="utf-8", errors="replace", bufsize=1
                )
                for line in proc.stdout:
                    clean = self._strip_ansi(line.rstrip())
                    self.root.after(0, lambda t=clean: output_widget.insert(tk.END, t + "\n"))
                proc.wait()
                self.root.after(0, lambda: output_widget.insert(tk.END, f"\n{label}完成!\n"))
                self.root.after(0, lambda: self.status_var.set(f"{label}完成"))
            except Exception as e:
                self.root.after(0, lambda: output_widget.insert(tk.END, f"错误: {e}\n"))
                self.root.after(0, lambda: self.status_var.set(f"{label}失败"))

        threading.Thread(target=worker, daemon=True).start()

    @staticmethod
    def _strip_ansi(text):
        import re
        return re.sub(r'\x1b\[[0-9;]*[a-zA-Z]', '', text)


if __name__ == "__main__":
    root = tk.Tk()
    app = TrainGUI(root)
    root.mainloop()
