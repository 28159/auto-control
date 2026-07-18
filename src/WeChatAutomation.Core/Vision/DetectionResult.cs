using System;

namespace WeChatAutomation.Core.Vision
{
    public class Detection
    {
        public string Label { get; set; } = "";
        public float Confidence { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public int CenterX => X + Width / 2;
        public int CenterY => Y + Height / 2;

        public Detection() { }

        public Detection(string label, float confidence, int x, int y, int width, int height)
        {
            Label = label;
            Confidence = confidence;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public Detection ToScreenCoords(int offsetX, int offsetY)
        {
            return new Detection
            {
                Label = Label,
                Confidence = Confidence,
                X = X + offsetX,
                Y = Y + offsetY,
                Width = Width,
                Height = Height
            };
        }

        public double IoU(Detection other)
        {
            int x1 = Math.Max(X, other.X);
            int y1 = Math.Max(Y, other.Y);
            int x2 = Math.Min(X + Width, other.X + other.Width);
            int y2 = Math.Min(Y + Height, other.Y + other.Height);

            int interArea = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
            int unionArea = Width * Height + other.Width * other.Height - interArea;

            return unionArea > 0 ? (double)interArea / unionArea : 0;
        }

        public override string ToString() => $"{Label} ({Confidence:P0}) @ ({X},{Y}) {Width}x{Height}";
    }
}
