"""
生成模拟训练数据 — 在没有真实截图的情况下先验证训练流程
生成带有随机 UI 元素的窗口截图和对应的 YOLO 标注

用法:
  python generate_mock_data.py --count 200 --output ../captures
"""

import argparse
import random
import os
from pathlib import Path

try:
    import cv2
    import numpy as np
    HAS_CV2 = True
except ImportError:
    HAS_CV2 = False
    print("警告: 未安装 opencv-python, 将生成纯色截图")


CLASSES = [
    "button", "input", "checkbox", "radio", "dropdown",
    "tab", "menu_item", "icon", "link", "text_field",
    "search_box", "send_button", "close_button", "minimize_button",
    "maximize_button", "scrollbar", "slider", "toggle", "tooltip", "image"
]

WINDOW_WIDTH = 800
WINDOW_HEIGHT = 600

UI_COLORS = {
    0: ((70, 130, 180), (255, 255, 255)),
    1: ((255, 255, 255), (0, 0, 0)),
    2: ((240, 240, 240), (0, 0, 0)),
    3: ((240, 240, 240), (0, 0, 0)),
    4: ((255, 255, 255), (0, 0, 0)),
    5: ((230, 230, 230), (50, 50, 50)),
    6: ((250, 250, 250), (30, 30, 30)),
    7: ((200, 200, 200), (80, 80, 80)),
    8: ((0, 100, 200), (255, 255, 255)),
    9: ((255, 255, 255), (0, 0, 0)),
    10: ((255, 255, 240), (100, 100, 100)),
    11: ((7, 193, 96), (255, 255, 255)),
    12: ((232, 17, 35), (255, 255, 255)),
    13: ((240, 240, 240), (80, 80, 80)),
    14: ((240, 240, 240), (80, 80, 80)),
    15: ((200, 200, 200), (150, 150, 150)),
    16: ((200, 200, 200), (100, 100, 100)),
    17: ((76, 175, 80), (255, 255, 255)),
    18: ((255, 235, 59), (50, 50, 50)),
    19: ((180, 180, 180), (120, 120, 120)),
}

UI_SIZES = {
    0: (60, 120, 24, 36),
    1: (24, 32, 150, 300),
    2: (14, 18, 14, 18),
    3: (14, 18, 14, 18),
    4: (24, 32, 100, 200),
    5: (24, 32, 60, 100),
    6: (22, 30, 80, 160),
    7: (20, 40, 20, 40),
    8: (18, 24, 40, 80),
    9: (20, 28, 120, 250),
    10: (24, 32, 150, 250),
    11: (24, 36, 50, 100),
    12: (14, 16, 14, 16),
    13: (14, 16, 14, 16),
    14: (14, 16, 14, 16),
    15: (8, 12, 100, 400),
    16: (8, 12, 80, 200),
    17: (20, 28, 36, 50),
    18: (18, 24, 40, 80),
    19: (20, 50, 20, 50),
}


def generate_element(img, class_id, x, y, w, h):
    if not HAS_CV2:
        return

    bg_color, text_color = UI_COLORS.get(class_id, ((200, 200, 200), (0, 0, 0)))
    bg_color = tuple(max(0, min(255, c + random.randint(-15, 15))) for c in bg_color)

    cv2.rectangle(img, (x, y), (x + w, y + h), bg_color, -1)
    cv2.rectangle(img, (x, y), (x + w, y + h), (100, 100, 100), 1)

    if class_id == 0:
        label = random.choice(["确定", "取消", "发送", "OK", "Submit", "Save", "登录", "搜索"])
        font = cv2.FONT_HERSHEY_SIMPLEX
        (tw, th), _ = cv2.getTextSize(label if label.isascii() else "Btn", font, 0.4, 1)
        tx = x + (w - tw) // 2
        ty = y + (h + th) // 2
        cv2.putText(img, label if label.isascii() else "Btn", (tx, ty), font, 0.4, text_color, 1)
    elif class_id == 1:
        cv2.line(img, (x + 4, y + h - 4), (x + w - 4, y + h - 4), (180, 180, 180), 1)
    elif class_id == 2:
        cv2.rectangle(img, (x + 2, y + 2), (x + h - 2, y + h - 2), (50, 50, 50), 1)
    elif class_id == 11:
        cv2.putText(img, "Send", (x + 4, y + h - 6), cv2.FONT_HERSHEY_SIMPLEX, 0.35, text_color, 1)
    elif class_id == 12:
        cv2.line(img, (x + 3, y + 3), (x + h - 3, y + h - 3), (255, 255, 255), 2)
        cv2.line(img, (x + h - 3, y + 3), (x + 3, y + h - 3), (255, 255, 255), 2)


def generate_mock_image(seed=None):
    if seed is not None:
        random.seed(seed)
        np.random.seed(seed)

    bg_r = random.randint(235, 255)
    bg_g = random.randint(235, 255)
    bg_b = random.randint(235, 255)

    if HAS_CV2:
        img = np.full((WINDOW_HEIGHT, WINDOW_WIDTH, 3), (bg_b, bg_g, bg_r), dtype=np.uint8)
        cv2.rectangle(img, (0, 0), (WINDOW_WIDTH, 30), (40, 40, 40), -1)
        cv2.rectangle(img, (WINDOW_WIDTH - 90, 2), (WINDOW_WIDTH - 2, 28), (60, 60, 60), -1)
    else:
        img = None

    annotations = []

    num_elements = random.randint(5, 15)
    placed = []

    for _ in range(num_elements):
        class_id = random.choices(range(len(CLASSES)), weights=[
            15, 12, 3, 2, 4, 3, 4, 5, 3, 10,
            5, 8, 3, 2, 2, 2, 1, 3, 2, 5
        ])[0]

        min_h, max_h, min_w, max_w = UI_SIZES[class_id]
        h = random.randint(min_h, max_h)
        w = random.randint(min_w, max_w)
        x = random.randint(10, WINDOW_WIDTH - w - 10)
        y = random.randint(35, WINDOW_HEIGHT - h - 10)

        overlap = False
        for px, py, pw, ph in placed:
            if not (x + w < px or x > px + pw or y + h < py or y > py + ph):
                overlap = True
                break
        if overlap:
            continue

        placed.append((x, y, w, h))

        if HAS_CV2:
            generate_element(img, class_id, x, y, w, h)

        cx = (x + w / 2) / WINDOW_WIDTH
        cy = (y + h / 2) / WINDOW_HEIGHT
        nw = w / WINDOW_WIDTH
        nh = h / WINDOW_HEIGHT
        annotations.append(f"{class_id} {cx:.6f} {cy:.6f} {nw:.6f} {nh:.6f}")

    return img, annotations


def main():
    parser = argparse.ArgumentParser(description="生成模拟训练数据")
    parser.add_argument("--count", type=int, default=200, help="生成图片数量")
    parser.add_argument("--output", type=str, default="../captures/mock", help="输出目录")
    args = parser.parse_args()

    if not HAS_CV2:
        print("需要 opencv-python 生成模拟图片，请运行: pip install opencv-python")
        return

    output_dir = Path(args.output)
    output_dir.mkdir(parents=True, exist_ok=True)

    print(f"生成 {args.count} 张模拟 UI 截图到 {output_dir}")

    for i in range(args.count):
        img, annotations = generate_mock_image(seed=i)

        filename = f"mock_{i:04d}"
        img_path = output_dir / f"{filename}.png"
        cv2.imwrite(str(img_path), img)

        if annotations:
            label_path = output_dir / f"{filename}.txt"
            with open(label_path, "w", encoding="utf-8") as f:
                f.write("\n".join(annotations))

        if (i + 1) % 50 == 0:
            print(f"  已生成 {i + 1}/{args.count}")

    label_count = len(list(output_dir.glob("*.txt")))
    img_count = len(list(output_dir.glob("*.png")))
    print(f"\n完成! 生成 {img_count} 张图片, {label_count} 个标注文件")
    print(f"\n下一步:")
    print(f"  python collect_data.py --captures {output_dir} --output ./datasets --clean")
    print(f"  python train.py --epochs 50 --batch 8 --device cpu --workers 4")


if __name__ == "__main__":
    main()
