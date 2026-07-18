"""
YOLOv8 标注 + 训练脚本
由 C# 程序通过 subprocess 调用

命令:
  python label_and_train.py label --images-dir <dir> --output-dir <dir>
  python label_and_train.py collect --captures-dir <dir> --output-dir <dir>
  python label_and_train.py train --data <yaml> --epochs <n> --batch <n> --device <str>
  python label_and_train.py export --model <pt> --output <onnx>
  python label_and_train.py deploy --onnx <path> --target <dir>

label 命令: 使用 OpenCV GUI 标注工具，stdout 不重定向，通过 print 输出信息
其他命令: 通过 stdout JSON 协议与 C# 通信
"""

import argparse
import json
import os
import shutil
import sys
from pathlib import Path


CLASSES = [
    "button", "input", "checkbox", "radio", "dropdown",
    "tab", "menu_item", "icon", "link", "text_field",
    "search_box", "send_button", "close_button", "minimize_button",
    "maximize_button", "scrollbar", "slider", "toggle", "tooltip", "image"
]

CLASS_COLORS = [
    "#E53935", "#1E88E5", "#43A047", "#FB8C00", "#8E24AA",
    "#00ACC1", "#F4511E", "#3949AB", "#7CB342", "#C0CA33",
    "#6D4C41", "#546E7A", "#D81B60", "#00897B", "#FFB300",
    "#5E35B1", "#039BE5", "#E91E63", "#00C853", "#FF6D00"
]


def emit(msg_type, **kwargs):
    line = json.dumps({"type": msg_type, **kwargs}, ensure_ascii=False)
    sys.stdout.write(line + "\n")
    sys.stdout.flush()


def do_label(args):
    try:
        import cv2
        import numpy as np
    except ImportError:
        print("error: missing dependencies, pip install opencv-python numpy")
        return

    images_dir = Path(args.images_dir)
    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    images = sorted(
        list(images_dir.glob("*.png")) + list(images_dir.glob("*.jpg")) + list(images_dir.glob("*.bmp"))
    )

    if not images:
        print(f"no images found: {images_dir}")
        return

    print(f"label tool started, {len(images)} images")

    current_idx = 0
    annotations = {}

    label_file = output_dir / "annotations.json"
    if label_file.exists():
        with open(label_file, "r", encoding="utf-8") as f:
            annotations = json.load(f)
        print(f"loaded existing annotations: {len(annotations)} images")

    window_name = "YOLO Labeler"
    cv2.namedWindow(window_name, cv2.WINDOW_NORMAL)
    cv2.resizeWindow(window_name, 1000, 700)

    current_boxes = []
    selected_class = 0
    drawing = False
    start_pt = None
    temp_end_pt = None
    current_img = None
    current_img_w = 0
    current_img_h = 0

    def get_boxes_for_image(img_name):
        if img_name in annotations:
            return [b for b in annotations[img_name]]
        lbl_path = images_dir / (Path(img_name).stem + ".txt")
        boxes = []
        if lbl_path.exists():
            with open(lbl_path, "r") as f:
                for line in f:
                    parts = line.strip().split()
                    if len(parts) == 5:
                        cid = int(parts[0])
                        cx, cy, w, h = float(parts[1]), float(parts[2]), float(parts[3]), float(parts[4])
                        boxes.append({"class_id": cid, "cx": cx, "cy": cy, "w": w, "h": h})
        return boxes

    def draw_image(img, boxes, img_w, img_h):
        display = img.copy()
        for b in boxes:
            cid = b["class_id"]
            cx, cy, w, h = b["cx"], b["cy"], b["w"], b["h"]
            x1 = int((cx - w / 2) * img_w)
            y1 = int((cy - h / 2) * img_h)
            x2 = int((cx + w / 2) * img_w)
            y2 = int((cy + h / 2) * img_h)
            color_hex = CLASS_COLORS[cid % len(CLASS_COLORS)]
            color = tuple(int(color_hex[i:i+2], 16) for i in (1, 3, 5))
            color_bgr = (color[2], color[1], color[0])
            cv2.rectangle(display, (x1, y1), (x2, y2), color_bgr, 2)
            label = CLASSES[cid] if cid < len(CLASSES) else f"class_{cid}"
            cv2.putText(display, label, (x1, y1 - 5), cv2.FONT_HERSHEY_SIMPLEX, 0.5, color_bgr, 1)
        return display

    def save_yolo_labels(img_name, boxes, img_w, img_h):
        lbl_path = output_dir / (Path(img_name).stem + ".txt")
        with open(lbl_path, "w") as f:
            for b in boxes:
                f.write(f"{b['class_id']} {b['cx']:.6f} {b['cy']:.6f} {b['w']:.6f} {b['h']:.6f}\n")

    def refresh_display():
        display = draw_image(current_img, current_boxes, current_img_w, current_img_h)
        cv2.imshow(window_name, display)

    def on_mouse(event, x, y, flags, param):
        nonlocal drawing, start_pt, temp_end_pt, current_boxes
        if event == cv2.EVENT_LBUTTONDOWN:
            drawing = True
            start_pt = (x, y)
            temp_end_pt = None
        elif event == cv2.EVENT_MOUSEMOVE and drawing:
            temp_end_pt = (x, y)
            display = draw_image(current_img, current_boxes, current_img_w, current_img_h)
            color_hex = CLASS_COLORS[selected_class % len(CLASS_COLORS)]
            color = tuple(int(color_hex[i:i+2], 16) for i in (1, 3, 5))
            color_bgr = (color[2], color[1], color[0])
            cv2.rectangle(display, start_pt, (x, y), color_bgr, 2)
            cv2.imshow(window_name, display)
        elif event == cv2.EVENT_LBUTTONUP:
            drawing = False
            if start_pt:
                x1, y1 = min(start_pt[0], x), min(start_pt[1], y)
                x2, y2 = max(start_pt[0], x), max(start_pt[1], y)
                bw = x2 - x1
                bh = y2 - y1
                if bw > 5 and bh > 5:
                    cx = ((x1 + x2) / 2) / current_img_w
                    cy = ((y1 + y2) / 2) / current_img_h
                    w = bw / current_img_w
                    h = bh / current_img_h
                    current_boxes.append({"class_id": selected_class, "cx": cx, "cy": cy, "w": w, "h": h})
            start_pt = None
            temp_end_pt = None
            refresh_display()

    cv2.setMouseCallback(window_name, on_mouse)

    def load_image(idx):
        nonlocal current_img, current_img_w, current_img_h, current_boxes
        img_path = images[idx]
        img_name = img_path.name
        current_img = cv2.imread(str(img_path))
        if current_img is None:
            return None
        current_img_h, current_img_w = current_img.shape[:2]
        current_boxes = get_boxes_for_image(img_name)
        return img_name

    def save_current(img_name):
        annotations[img_name] = current_boxes
        save_yolo_labels(img_name, current_boxes, current_img_w, current_img_h)

    def save_all():
        with open(label_file, "w", encoding="utf-8") as f:
            json.dump(annotations, f, ensure_ascii=False, indent=2)

    while current_idx < len(images):
        img_name = load_image(current_idx)
        if img_name is None:
            current_idx += 1
            continue

        info = f"[{current_idx+1}/{len(images)}] {img_name} | Class: {CLASSES[selected_class]} | Boxes: {len(current_boxes)} | N/P:flip S:save D:del C:class Q:quit"
        cv2.setWindowTitle(window_name, info)
        refresh_display()

        while True:
            key = cv2.waitKeyEx(30) & 0xFF

            if key == ord('q') or key == 27:
                save_current(img_name)
                save_all()
                print(f"label done, {len(annotations)} images annotated")
                cv2.destroyAllWindows()
                return

            elif key == ord('s'):
                save_current(img_name)
                save_all()
                print(f"saved {img_name}: {len(current_boxes)} boxes")

            elif key == ord('n') or key == 2555904:
                save_current(img_name)
                current_idx += 1
                break

            elif key == ord('p') or key == 2424832:
                save_current(img_name)
                current_idx = max(0, current_idx - 1)
                break

            elif key == ord('d'):
                if current_boxes:
                    current_boxes.pop()
                    refresh_display()

            elif key == ord('c'):
                selected_class = (selected_class + 1) % len(CLASSES)

            elif key >= ord('0') and key <= ord('9'):
                idx = key - ord('0')
                if idx < len(CLASSES):
                    selected_class = idx

    cv2.destroyAllWindows()
    save_all()
    print(f"label done, {len(annotations)} images annotated")


def do_collect(args):
    captures_dir = Path(args.captures_dir)
    output_dir = Path(args.output_dir)

    pairs = []
    for img_file in sorted(captures_dir.rglob("*.png")):
        label_file = img_file.with_suffix(".txt")
        if label_file.exists():
            pairs.append((img_file, label_file))

    emit("log", message=f"found {len(pairs)} image-label pairs")

    if not pairs:
        emit("error", message="no image-label pairs found")
        return

    import random
    random.seed(42)
    random.shuffle(pairs)

    val_ratio = args.val_ratio
    val_count = max(1, int(len(pairs) * val_ratio))
    val_pairs = pairs[:val_count]
    train_pairs = pairs[val_count:]

    for split, split_pairs in [("train", train_pairs), ("val", val_pairs)]:
        img_dir = output_dir / "images" / split
        lbl_dir = output_dir / "labels" / split
        img_dir.mkdir(parents=True, exist_ok=True)
        lbl_dir.mkdir(parents=True, exist_ok=True)

        for img_file, label_file in split_pairs:
            img_name = img_file.name
            # 图片用原名；标签必须用图片名去扩展名 + .txt，否则 YOLO 找不到 label
            lbl_name = img_file.stem + ".txt"
            shutil.copy2(img_file, img_dir / img_name)
            shutil.copy2(label_file, lbl_dir / lbl_name)

    yaml_path = output_dir.parent / "dataset.yaml"
    with open(yaml_path, "w", encoding="utf-8") as f:
        f.write(f"path: {output_dir.resolve()}\n")
        f.write(f"train: images/train\n")
        f.write(f"val: images/val\n\n")
        f.write(f"nc: {len(CLASSES)}\n")
        f.write(f"names:\n")
        for name in CLASSES:
            f.write(f"  - {name}\n")

    emit("log", message=f"train: {len(train_pairs)}, val: {len(val_pairs)}")
    emit("result", success=True, data={"train": len(train_pairs), "val": len(val_pairs), "yaml": str(yaml_path)})


def do_train(args):
    try:
        from ultralytics import YOLO
    except ImportError:
        emit("error", message="missing dependency: pip install ultralytics")
        return

    data_config = args.data
    if not Path(data_config).exists():
        emit("error", message=f"dataset config not found: {data_config}")
        return

    emit("log", message=f"start training: data={data_config}, epochs={args.epochs}, batch={args.batch}")

    model = YOLO(f"{args.model_variant}.pt")

    # 通过回调每轮向 C# 发送 progress，驱动向导进度条
    def _on_fit_epoch_end(trainer):
        try:
            epoch = int(getattr(trainer, "epoch", 0)) + 1
            total = int(getattr(trainer, "epochs", args.epochs)) or args.epochs
            value = min(1.0, epoch / total) if total > 0 else 0.0
            parts = [f"Epoch {epoch}/{total}"]
            loss = getattr(trainer, "loss", None)
            if loss is not None:
                try:
                    parts.append(f"loss={float(loss):.4f}")
                except Exception:
                    pass
            metrics = getattr(trainer, "metrics", None)
            if metrics is not None:
                try:
                    m = metrics.get("metrics/mAP50(B)") if hasattr(metrics, "get") else None
                    if m is None and hasattr(metrics, "get"):
                        m = metrics.get("mAP50(B)") or metrics.get("mAP50")
                    if m is not None:
                        parts.append(f"mAP50={float(m):.3f}")
                except Exception:
                    pass
            emit("progress", value=value, message=" ".join(parts))
        except Exception as e:
            emit("log", message=f"progress callback error: {e}")

    def _on_train_end(trainer):
        emit("progress", value=1.0, message="训练完成")

    model.add_callback("on_fit_epoch_end", _on_fit_epoch_end)
    model.add_callback("on_train_end", _on_train_end)

    results = model.train(
        data=data_config,
        epochs=args.epochs,
        batch=args.batch,
        imgsz=args.imgsz,
        device=args.device if args.device else "",
        workers=args.workers,
        project=args.project,
        name=args.name,
        exist_ok=True,
        patience=args.patience,
        save_period=args.save_period,
        lr0=args.lr0,
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

    best_weight = Path(args.project) / args.name / "weights" / "best.pt"
    emit("log", message=f"training done, best model: {best_weight}")
    emit("result", success=True, data={"best_model": str(best_weight)})


def do_export(args):
    try:
        from ultralytics import YOLO
    except ImportError:
        emit("error", message="missing dependency: pip install ultralytics")
        return

    model_path = args.model
    if not Path(model_path).exists():
        emit("error", message=f"model not found: {model_path}")
        return

    emit("log", message=f"exporting ONNX: {model_path}")
    model = YOLO(model_path)
    onnx_path = model.export(format="onnx", imgsz=args.imgsz, simplify=True, dynamic=False, opset=12)

    target = Path(args.output)
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(onnx_path, target)

    size_mb = target.stat().st_size / 1024 / 1024
    emit("log", message=f"ONNX exported: {target} ({size_mb:.1f} MB)")
    emit("result", success=True, data={"onnx_path": str(target), "size_mb": round(size_mb, 1)})


def do_deploy(args):
    onnx_path = Path(args.onnx)
    target_dir = Path(args.target)

    if not onnx_path.exists():
        emit("error", message=f"ONNX file not found: {onnx_path}")
        return

    target_dir.mkdir(parents=True, exist_ok=True)
    target = target_dir / onnx_path.name
    shutil.copy2(onnx_path, target)

    emit("log", message=f"deployed: {target}")
    emit("result", success=True, data={"deployed": str(target)})


def main():
    parser = argparse.ArgumentParser(description="YOLOv8 label + train")
    sub = parser.add_subparsers(dest="command")

    p_label = sub.add_parser("label")
    p_label.add_argument("--images-dir", required=True)
    p_label.add_argument("--output-dir", required=True)

    p_collect = sub.add_parser("collect")
    p_collect.add_argument("--captures-dir", required=True)
    p_collect.add_argument("--output-dir", required=True)
    p_collect.add_argument("--val-ratio", type=float, default=0.2)

    p_train = sub.add_parser("train")
    p_train.add_argument("--data", required=True)
    p_train.add_argument("--model-variant", default="yolov8n")
    p_train.add_argument("--epochs", type=int, default=100)
    p_train.add_argument("--batch", type=int, default=8)
    p_train.add_argument("--imgsz", type=int, default=640)
    p_train.add_argument("--device", default="")
    p_train.add_argument("--workers", type=int, default=4)
    p_train.add_argument("--project", default="runs")
    p_train.add_argument("--name", default="train")
    p_train.add_argument("--patience", type=int, default=20)
    p_train.add_argument("--save-period", type=int, default=10)
    p_train.add_argument("--lr0", type=float, default=0.01)

    p_export = sub.add_parser("export")
    p_export.add_argument("--model", required=True)
    p_export.add_argument("--output", required=True)
    p_export.add_argument("--imgsz", type=int, default=640)

    p_deploy = sub.add_parser("deploy")
    p_deploy.add_argument("--onnx", required=True)
    p_deploy.add_argument("--target", required=True)

    args = parser.parse_args()

    if args.command == "label":
        do_label(args)
    elif args.command == "collect":
        do_collect(args)
    elif args.command == "train":
        do_train(args)
    elif args.command == "export":
        do_export(args)
    elif args.command == "deploy":
        do_deploy(args)
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
