"""
ONNX 模型导出脚本 — 将训练好的 YOLOv8 模型导出为 ONNX 格式供 C# 推理使用

用法:
  # 导出最佳模型
  python export_onnx.py

  # 指定模型路径
  python export_onnx.py --model runs/detect/train/weights/best.pt

  # 指定输出路径
  python export_onnx.py --output ./models/yolov8n-ui.onnx

  # INT8 量化 (更小更快，精度略降)
  python export_onnx.py --quantize int8
"""

import argparse
import shutil
import sys
from pathlib import Path


def export_onnx(args):
    """导出 ONNX 模型"""
    from ultralytics import YOLO

    model_path = args.model
    if not Path(model_path).exists():
        print(f"错误: 模型文件不存在: {model_path}")
        sys.exit(1)

    print(f"加载模型: {model_path}")
    model = YOLO(model_path)

    print(f"导出 ONNX (imgsz={args.imgsz}, opset={args.opset})...")
    onnx_path = model.export(
        format="onnx",
        imgsz=args.imgsz,
        simplify=True,
        dynamic=False,
        opset=args.opset,
    )

    print(f"ONNX 模型已导出: {onnx_path}")

    models_dir = Path(args.output).parent
    models_dir.mkdir(parents=True, exist_ok=True)

    shutil.copy2(onnx_path, args.output)
    print(f"已复制到: {args.output}")

    file_size = Path(args.output).stat().st_size / 1024 / 1024
    print(f"模型大小: {file_size:.1f} MB")

    if args.quantize:
        quantize_onnx(args.output, args.quantize)

    csharp_dir = Path(__file__).parent.parent / "src" / "WeChatAutomation.App" / "bin" / "Debug" / "net9.0-windows" / "models"
    if csharp_dir.exists():
        csharp_dir.mkdir(parents=True, exist_ok=True)
        target = csharp_dir / Path(args.output).name
        shutil.copy2(args.output, target)
        print(f"已复制到 C# 输出目录: {target}")

    return args.output


def quantize_onnx(onnx_path, quantize_type):
    """ONNX 模型量化"""
    try:
        from onnxruntime.quantization import quantize_dynamic, QuantType

        quantized_path = onnx_path.replace(".onnx", f"-{quantize_type}.onnx")

        print(f"量化模型 ({quantize_type})...")

        if quantize_type == "int8":
            quantize_dynamic(
                model_input=onnx_path,
                model_output=quantized_path,
                weight_type=QuantType.QUInt8,
            )
        elif quantize_type == "fp16":
            import onnx
            from onnxconverter_common import float16

            model = onnx.load(onnx_path)
            model_fp16 = float16.convert_float_to_float16(model)
            onnx.save(model_fp16, quantized_path)

        q_size = Path(quantized_path).stat().st_size / 1024 / 1024
        print(f"量化模型大小: {q_size:.1f} MB")
        print(f"量化模型已保存: {quantized_path}")

    except ImportError as e:
        print(f"跳过量化: 缺少依赖 ({e})")
        print("安装: pip install onnxruntime onnx onnxconverter-common")


def validate_onnx(onnx_path, data_config):
    """验证 ONNX 模型"""
    try:
        import onnxruntime as ort
        import numpy as np
        from PIL import Image

        print(f"\n验证 ONNX 模型: {onnx_path}")

        session = ort.InferenceSession(onnx_path)
        input_info = session.get_inputs()[0]
        print(f"  输入: {input_info.name}, shape={input_info.shape}, type={input_info.type}")

        for output in session.get_outputs():
            print(f"  输出: {output.name}, shape={output.shape}, type={output.type}")

        dummy = np.random.rand(1, 3, 640, 640).astype(np.float32)
        results = session.run(None, {input_info.name: dummy})
        print(f"  推理测试: 输出形状={[r.shape for r in results]}")
        print("  验证通过!")

    except ImportError:
        print("跳过验证: 缺少 onnxruntime")
    except Exception as e:
        print(f"验证失败: {e}")


def main():
    parser = argparse.ArgumentParser(description="导出 YOLOv8 ONNX 模型")
    parser.add_argument("--model", type=str, default="runs/detect/train/weights/best.pt",
                        help="PyTorch 模型路径")
    parser.add_argument("--output", type=str, default="./models/yolov8n-ui.onnx",
                        help="ONNX 输出路径")
    parser.add_argument("--imgsz", type=int, default=640,
                        help="输入图像尺寸 (默认: 640)")
    parser.add_argument("--opset", type=int, default=12,
                        help="ONNX opset 版本 (默认: 12)")
    parser.add_argument("--quantize", type=str, choices=["int8", "fp16"], default="",
                        help="量化类型 (int8/fp16)")
    parser.add_argument("--validate", action="store_true",
                        help="验证导出的 ONNX 模型")
    args = parser.parse_args()

    export_onnx(args)

    if args.validate:
        validate_onnx(args.output, None)

    print("\n导出完成!")


if __name__ == "__main__":
    main()
