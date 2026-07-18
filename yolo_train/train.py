"""
YOLOv8 UI 元素检测模型训练脚本

用法:
  # 使用默认配置训练
  python train.py

  # 自定义参数训练
  python train.py --data dataset.yaml --epochs 100 --batch 16 --model yolov8n

  # 从已有模型继续训练
  python train.py --resume runs/detect/train/weights/last.pt

  # 使用 GPU 训练 (自动检测)
  python train.py --device 0

训练完成后模型保存在 runs/detect/train/weights/
  - best.pt: 最佳模型
  - last.pt: 最后一轮模型
"""

import argparse
import sys
from pathlib import Path


def check_environment():
    """检查训练环境"""
    print("检查训练环境...")

    try:
        import ultralytics
        print(f"  ultralytics: {ultralytics.__version__}")
    except ImportError:
        print("错误: 未安装 ultralytics, 请运行: pip install ultralytics")
        sys.exit(1)

    try:
        import torch
        print(f"  torch: {torch.__version__}")
        cuda_available = torch.cuda.is_available()
        print(f"  CUDA: {'可用' if cuda_available else '不可用'}")
        if cuda_available:
            print(f"  GPU: {torch.cuda.get_device_name(0)}")
            print(f"  VRAM: {torch.cuda.get_device_properties(0).total_mem / 1024**3:.1f} GB")
    except ImportError:
        print("  警告: 未安装 torch, 请运行: pip install torch")

    try:
        import onnx
        print(f"  onnx: {onnx.__version__}")
    except ImportError:
        print("  警告: 未安装 onnx, 导出 ONNX 时需要: pip install onnx")


def train(args):
    """执行训练"""
    from ultralytics import YOLO

    data_config = args.data
    if not Path(data_config).exists():
        print(f"错误: 数据集配置文件不存在: {data_config}")
        print("请先运行 collect_data.py 生成数据集")
        sys.exit(1)

    print("\n" + "=" * 60)
    print("YOLOv8 UI 元素检测 — 开始训练")
    print("=" * 60)

    model_variant = args.model
    pretrained = f"{model_variant}.pt"

    if args.resume:
        print(f"从检查点恢复: {args.resume}")
        model = YOLO(args.resume)
    else:
        print(f"模型变体: {model_variant}")
        print(f"预训练权重: {pretrained}")
        model = YOLO(pretrained)

    results = model.train(
        data=data_config,
        epochs=args.epochs,
        batch=args.batch,
        imgsz=args.imgsz,
        device=args.device,
        workers=args.workers,
        project=args.project,
        name=args.name,
        exist_ok=args.exist_ok,
        patience=args.patience,
        save_period=args.save_period,
        lr0=args.lr0,
        lrf=args.lrf,
        augment=True,
        mosaic=1.0,
        mixup=0.1,
        copy_paste=0.1,
        degrees=5.0,
        translate=0.1,
        scale=0.5,
        fliplr=0.5,
        hsv_h=0.015,
        hsv_s=0.7,
        hsv_v=0.4,
        overlap_mask=True,
        verbose=True,
    )

    print("\n" + "=" * 60)
    print("训练完成!")
    print("=" * 60)

    best_weight = Path(args.project) / args.name / "weights" / "best.pt"
    if best_weight.exists():
        print(f"最佳模型: {best_weight}")

    return results


def validate(args):
    """验证模型"""
    from ultralytics import YOLO

    best_weight = Path(args.project) / args.name / "weights" / "best.pt"
    if not best_weight.exists():
        print(f"错误: 模型文件不存在: {best_weight}")
        sys.exit(1)

    print("\n验证模型...")
    model = YOLO(str(best_weight))
    metrics = model.val(data=args.data, imgsz=args.imgsz, device=args.device)

    print(f"\n验证结果:")
    print(f"  mAP50:    {metrics.box.map50:.4f}")
    print(f"  mAP50-95: {metrics.box.map:.4f}")

    return metrics


def export_onnx(args):
    """导出 ONNX 模型"""
    from ultralytics import YOLO

    best_weight = Path(args.project) / args.name / "weights" / "best.pt"
    if not best_weight.exists():
        print(f"错误: 模型文件不存在: {best_weight}")
        sys.exit(1)

    print("\n导出 ONNX 模型...")
    model = YOLO(str(best_weight))

    onnx_path = model.export(
        format="onnx",
        imgsz=args.imgsz,
        simplify=True,
        dynamic=False,
        opset=12,
    )

    print(f"ONNX 模型已导出: {onnx_path}")

    models_dir = Path(__file__).parent / "models"
    models_dir.mkdir(exist_ok=True)

    target_name = f"yolov8n-ui.onnx"
    if args.model != "yolov8n":
        target_name = f"{args.model}-ui.onnx"

    import shutil
    target_path = models_dir / target_name
    shutil.copy2(onnx_path, target_path)
    print(f"已复制到: {target_path}")

    csharp_models = Path(__file__).parent.parent / "src" / "WeChatAutomation.App" / "bin" / "Debug" / "net9.0-windows" / "models"
    if csharp_models.exists():
        shutil.copy2(target_path, csharp_models / target_name)
        print(f"已复制到 C# 输出目录: {csharp_models / target_name}")

    return onnx_path


def main():
    parser = argparse.ArgumentParser(description="YOLOv8 UI 元素检测训练")

    parser.add_argument("--data", type=str, default="dataset.yaml",
                        help="数据集配置文件")
    parser.add_argument("--model", type=str, default="yolov8n",
                        choices=["yolov8n", "yolov8s", "yolov8m", "yolov8l", "yolov8x"],
                        help="YOLOv8 模型变体 (默认: yolov8n)")
    parser.add_argument("--epochs", type=int, default=100,
                        help="训练轮数 (默认: 100)")
    parser.add_argument("--batch", type=int, default=16,
                        help="批大小 (默认: 16, 显存不足可减小)")
    parser.add_argument("--imgsz", type=int, default=640,
                        help="输入图像尺寸 (默认: 640)")
    parser.add_argument("--device", type=str, default="",
                        help="训练设备 (空=自动, 0=GPU0, cpu=CPU)")
    parser.add_argument("--workers", type=int, default=8,
                        help="数据加载线程数 (默认: 8)")
    parser.add_argument("--project", type=str, default="runs",
                        help="训练输出目录 (默认: runs)")
    parser.add_argument("--name", type=str, default="train",
                        help="实验名称 (默认: train)")
    parser.add_argument("--exist-ok", action="store_true",
                        help="允许覆盖已有实验目录")
    parser.add_argument("--resume", type=str, default="",
                        help="从检查点恢复训练路径")
    parser.add_argument("--patience", type=int, default=20,
                        help="早停耐心值 (默认: 20)")
    parser.add_argument("--save-period", type=int, default=10,
                        help="每N轮保存一次模型 (默认: 10)")
    parser.add_argument("--lr0", type=float, default=0.01,
                        help="初始学习率 (默认: 0.01)")
    parser.add_argument("--lrf", type=float, default=0.01,
                        help="最终学习率因子 (默认: 0.01)")
    parser.add_argument("--skip-train", action="store_true",
                        help="跳过训练，只验证/导出")
    parser.add_argument("--skip-val", action="store_true",
                        help="跳过验证")
    parser.add_argument("--skip-export", action="store_true",
                        help="跳过 ONNX 导出")

    args = parser.parse_args()

    check_environment()

    if not args.skip_train:
        train(args)

    if not args.skip_val:
        validate(args)

    if not args.skip_export:
        export_onnx(args)

    print("\n全部完成!")


if __name__ == "__main__":
    main()
