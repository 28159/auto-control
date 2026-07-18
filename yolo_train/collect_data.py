"""
数据采集脚本 — 从 C# 录制器导出的截图+标注数据中准备 YOLOv8 训练集

C# 录制器会在 captures/ 目录下保存:
  - 截图文件: {timestamp}.png
  - 标注文件: {timestamp}.txt (YOLO 格式: class_id cx cy w h)

本脚本功能:
  1. 读取 captures/ 目录下的截图和标注
  2. 自动划分训练集/验证集 (80/20)
  3. 复制到 datasets/ 目录结构
  4. 生成 dataset.yaml 配置

用法:
  python collect_data.py --captures ../captures --output ./datasets --val-ratio 0.2
"""

import os
import sys
import shutil
import random
import argparse
from pathlib import Path


CLASSES = [
    "button", "input", "checkbox", "radio", "dropdown",
    "tab", "menu_item", "icon", "link", "text_field",
    "search_box", "send_button", "close_button", "minimize_button",
    "maximize_button", "scrollbar", "slider", "toggle", "tooltip", "image"
]


def find_pairs(captures_dir):
    """查找截图-标注对"""
    captures_path = Path(captures_dir)
    pairs = []

    for img_file in sorted(captures_path.glob("*.png")):
        label_file = img_file.with_suffix(".txt")
        if label_file.exists():
            pairs.append((img_file, label_file))

    print(f"找到 {len(pairs)} 对截图-标注文件")
    return pairs


def validate_label(label_file, num_classes=len(CLASSES)):
    """验证标注文件格式是否正确"""
    valid_lines = []
    with open(label_file, "r", encoding="utf-8") as f:
        for line_no, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            parts = line.split()
            if len(parts) != 5:
                print(f"  警告: {label_file.name} 第{line_no}行格式错误 (需要5列): {line}")
                continue
            try:
                class_id = int(parts[0])
                cx, cy, w, h = float(parts[1]), float(parts[2]), float(parts[3]), float(parts[4])
                if class_id < 0 or class_id >= num_classes:
                    print(f"  警告: {label_file.name} 第{line_no}行类别ID越界: {class_id}")
                    continue
                if not (0 <= cx <= 1 and 0 <= cy <= 1 and 0 <= w <= 1 and 0 <= h <= 1):
                    print(f"  警告: {label_file.name} 第{line_no}行坐标超出[0,1]范围")
                    continue
                valid_lines.append(line)
            except ValueError:
                print(f"  警告: {label_file.name} 第{line_no}行数值解析失败: {line}")
                continue

    if not valid_lines:
        print(f"  跳过: {label_file.name} 无有效标注")
        return False
    return True


def split_dataset(pairs, val_ratio=0.2, seed=42):
    """划分训练集和验证集"""
    random.seed(seed)
    random.shuffle(pairs)
    val_count = max(1, int(len(pairs) * val_ratio))
    val_pairs = pairs[:val_count]
    train_pairs = pairs[val_count:]
    print(f"训练集: {len(train_pairs)} 张, 验证集: {len(val_pairs)} 张")
    return train_pairs, val_pairs


def copy_pairs(pairs, output_dir, split):
    """复制文件到目标目录"""
    img_dir = Path(output_dir) / "images" / split
    lbl_dir = Path(output_dir) / "labels" / split
    img_dir.mkdir(parents=True, exist_ok=True)
    lbl_dir.mkdir(parents=True, exist_ok=True)

    count = 0
    for img_file, label_file in pairs:
        shutil.copy2(img_file, img_dir / img_file.name)
        shutil.copy2(label_file, lbl_dir / label_file.name)
        count += 1

    print(f"已复制 {count} 对文件到 {split}/")
    return count


def generate_dataset_yaml(output_dir, num_classes=len(CLASSES)):
    """生成 dataset.yaml 配置文件"""
    yaml_path = Path(output_dir).parent / "dataset.yaml"
    content = f"""# YOLOv8 UI 检测训练数据集配置
path: {Path(output_dir).resolve()}
train: images/train
val: images/val

nc: {num_classes}
names:
"""
    for i, name in enumerate(CLASSES):
        content += f"  - {name}\n"

    with open(yaml_path, "w", encoding="utf-8") as f:
        f.write(content)

    print(f"已生成配置文件: {yaml_path}")


def print_stats(output_dir):
    """打印数据集统计信息"""
    for split in ["train", "val"]:
        img_dir = Path(output_dir) / "images" / split
        lbl_dir = Path(output_dir) / "labels" / split
        img_count = len(list(img_dir.glob("*.png"))) if img_dir.exists() else 0
        lbl_count = len(list(lbl_dir.glob("*.txt"))) if lbl_dir.exists() else 0
        print(f"  {split}: {img_count} 张图片, {lbl_count} 个标注")

        if lbl_dir.exists():
            class_counts = {}
            for lbl_file in lbl_dir.glob("*.txt"):
                with open(lbl_file, "r", encoding="utf-8") as f:
                    for line in f:
                        parts = line.strip().split()
                        if len(parts) >= 1:
                            cid = int(parts[0])
                            cname = CLASSES[cid] if cid < len(CLASSES) else f"class_{cid}"
                            class_counts[cname] = class_counts.get(cname, 0) + 1

            if class_counts:
                print(f"    类别分布:")
                for name, count in sorted(class_counts.items(), key=lambda x: -x[1]):
                    print(f"      {name}: {count}")


def main():
    parser = argparse.ArgumentParser(description="从 C# 录制器导出数据准备 YOLOv8 训练集")
    parser.add_argument("--captures", type=str, default="../captures",
                        help="截图和标注目录 (默认: ../captures)")
    parser.add_argument("--output", type=str, default="./datasets",
                        help="输出数据集目录 (默认: ./datasets)")
    parser.add_argument("--val-ratio", type=float, default=0.2,
                        help="验证集比例 (默认: 0.2)")
    parser.add_argument("--seed", type=int, default=42,
                        help="随机种子 (默认: 42)")
    parser.add_argument("--clean", action="store_true",
                        help="清空输出目录后重新生成")
    args = parser.parse_args()

    print("=" * 60)
    print("YOLOv8 UI 检测 — 数据采集")
    print("=" * 60)

    if args.clean and Path(args.output).exists():
        print(f"清空输出目录: {args.output}")
        shutil.rmtree(args.output)

    pairs = find_pairs(args.captures)
    if not pairs:
        print("未找到任何截图-标注对，请先使用 C# 录制器采集数据")
        print(f"截图目录: {Path(args.captures).resolve()}")
        sys.exit(1)

    valid_pairs = []
    for img_file, label_file in pairs:
        if validate_label(label_file):
            valid_pairs.append((img_file, label_file))

    if not valid_pairs:
        print("无有效数据，退出")
        sys.exit(1)

    print(f"\n有效数据: {len(valid_pairs)} 对")

    train_pairs, val_pairs = split_dataset(valid_pairs, args.val_ratio, args.seed)

    print(f"\n复制文件...")
    copy_pairs(train_pairs, args.output, "train")
    copy_pairs(val_pairs, args.output, "val")

    generate_dataset_yaml(args.output)

    print(f"\n数据集统计:")
    print_stats(args.output)

    print(f"\n完成! 下一步运行训练:")
    print(f"  python train.py --data dataset.yaml")


if __name__ == "__main__":
    main()
