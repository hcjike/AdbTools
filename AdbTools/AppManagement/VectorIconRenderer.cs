using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbTools
{
    /// <summary>从 APK 里的 VectorDrawable(XML) 解析出来的图标描述。</summary>
    internal sealed class VectorIcon
    {
        public VectorIcon()
        {
            Paths = new List<VectorIconPath>();
        }

        public double ViewportWidth { get; set; }

        public double ViewportHeight { get; set; }

        public List<VectorIconPath> Paths { get; private set; }
    }

    /// <summary>VectorDrawable 里的一个 &lt;path&gt;。</summary>
    internal sealed class VectorIconPath
    {
        public string Data { get; set; }

        public uint FillColor { get; set; }

        public double FillAlpha { get; set; }

        public bool HasFill { get; set; }

        public uint StrokeColor { get; set; }

        public double StrokeWidth { get; set; }

        public bool EvenOdd { get; set; }

        /// <summary>所在 group 的累计变换（父级 × 本级），已按 Android 的 pivot/scale/rotate 语义合成。</summary>
        public Matrix Transform { get; set; }
    }

    /// <summary>
    /// 把解析好的矢量图标画成 PNG。RenderTargetBitmap 必须在 STA 线程使用，
    /// 而图标解析跑在后台 MTA 线程上，所以这里用一个常驻的 STA 工作线程串行渲染。
    /// </summary>
    internal static class VectorIconRenderer
    {
        /// <summary>渲染尺寸，与 AppIconStore.Load 的 DecodePixelWidth 保持一致。</summary>
        private const int RenderSize = 64;

        private static readonly BlockingCollection<RenderJob> Queue = new BlockingCollection<RenderJob>();
        private static readonly object ThreadLock = new object();
        private static Thread _thread;

        private sealed class RenderJob
        {
            public VectorIcon Icon;
            public int Size;
            public bool HasBackground;
            public uint BackgroundColor;
            public byte[] Png;
            public ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        /// <summary>
        /// 渲染为 PNG 字节；失败返回 null（调用方按“没有图标”处理）。
        /// backgroundColor 为空时不铺底色，适合普通透明图标。
        /// </summary>
        public static byte[] RenderPng(VectorIcon icon, uint? backgroundColor)
        {
            if (icon == null || icon.Paths.Count == 0)
            {
                return null;
            }

            RenderJob job = new RenderJob
            {
                Icon = icon,
                Size = RenderSize,
                HasBackground = backgroundColor.HasValue,
                BackgroundColor = backgroundColor.HasValue ? backgroundColor.Value : 0
            };

            try
            {
                EnsureThread();
                Queue.Add(job);
                // 解析线程可能被用户停止，这里给足上限，不让异常流程永久挂住
                if (!job.Done.Wait(8000))
                {
                    return null;
                }
            }
            catch (Exception)
            {
                return null;
            }

            return job.Png;
        }

        private static void EnsureThread()
        {
            lock (ThreadLock)
            {
                if (_thread != null)
                {
                    return;
                }

                _thread = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "vector-icon-render"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }

        private static void Loop()
        {
            foreach (RenderJob job in Queue.GetConsumingEnumerable())
            {
                try
                {
                    job.Png = Render(job.Icon, job.Size, job.HasBackground ? (uint?)job.BackgroundColor : null);
                }
                catch (Exception)
                {
                    job.Png = null;
                }
                finally
                {
                    job.Done.Set();
                }
            }
        }

        private static byte[] Render(VectorIcon icon, int size, uint? backgroundColor)
        {
            double viewportWidth = icon.ViewportWidth > 0 ? icon.ViewportWidth : 24;
            double viewportHeight = icon.ViewportHeight > 0 ? icon.ViewportHeight : 24;
            double scale = Math.Min(size / viewportWidth, size / viewportHeight);
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            {
                return null;
            }

            // 视口按比例居中铺满，避免非正方形图标被拉变形
            double offsetX = (size - viewportWidth * scale) / 2;
            double offsetY = (size - viewportHeight * scale) / 2;
            Matrix root = new Matrix(scale, 0, 0, scale, offsetX, offsetY);

            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                if (backgroundColor.HasValue && (backgroundColor.Value >> 24) != 0)
                {
                    context.DrawRectangle(ToBrush(backgroundColor.Value, 1), null, new Rect(0, 0, size, size));
                }

                foreach (VectorIconPath path in icon.Paths)
                {
                    Geometry geometry = ParseGeometry(path);
                    if (geometry == null)
                    {
                        continue;
                    }

                    Brush fill = path.HasFill ? ToBrush(path.FillColor, path.FillAlpha) : null;
                    Pen pen = path.StrokeWidth > 0 ? ToPen(path.StrokeColor, path.StrokeWidth) : null;
                    if (fill == null && pen == null)
                    {
                        continue;
                    }

                    Matrix transform = Matrix.Multiply(root, path.Transform);
                    context.PushTransform(new MatrixTransform(transform));
                    context.DrawGeometry(fill, pen, geometry);
                    context.Pop();
                }
            }

            RenderTargetBitmap target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);

            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using (MemoryStream stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        /// <summary>Android 的 pathData 与 WPF 的 StreamGeometry 都用 SVG 路径语法，可直接解析。</summary>
        private static Geometry ParseGeometry(VectorIconPath path)
        {
            if (string.IsNullOrEmpty(path.Data))
            {
                return null;
            }

            try
            {
                Geometry parsed = Geometry.Parse(path.Data);
                StreamGeometry stream = parsed as StreamGeometry;
                if (stream == null)
                {
                    return parsed;
                }

                // Geometry.Parse 返回的是已冻结实例，Clone 一份才能改填充规则
                StreamGeometry geometry = stream.Clone();
                geometry.FillRule = path.EvenOdd ? FillRule.EvenOdd : FillRule.Nonzero;
                geometry.Freeze();
                return geometry;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static Brush ToBrush(uint argb, double alpha)
        {
            byte a = (byte)(argb >> 24);
            // 颜色本身全透明时按不透明处理：宁可显示成黑色，也不要整块看不见
            if (a == 0)
            {
                a = 0xFF;
            }

            double factor = alpha <= 0 ? 1 : alpha;
            byte scaled = (byte)Math.Max(0, Math.Min(255, a * factor));

            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(scaled,
                (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            return brush;
        }

        private static Pen ToPen(uint argb, double width)
        {
            Pen pen = new Pen(ToBrush(argb, 1), width);
            pen.Freeze();
            return pen;
        }
    }
}
