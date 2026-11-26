using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace AdbTools
{
    /// <summary>
    /// 对话框富文本片段工厂：统一颜色与字重，便于在 <see cref="Dialogs"/> 中拼装层次清晰的内容。
    /// 用法示例：inlines.Add(DialogText.Success("成功 8")); inlines.Add(DialogText.Link("失败 2", ShowDetail));
    /// </summary>
    internal static class DialogText
    {
        private static readonly Brush NormalBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
        private static readonly Brush StrongBrush = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
        private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
        private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(0x04, 0x78, 0x57));
        private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        private static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));

        /// <summary>正文默认文字。</summary>
        public static Run Normal(string text)
        {
            return new Run(text ?? string.Empty) { Foreground = NormalBrush };
        }

        /// <summary>重点文字：深色 + 半粗。</summary>
        public static Run Strong(string text)
        {
            return new Run(text ?? string.Empty)
            {
                Foreground = StrongBrush,
                FontWeight = FontWeights.SemiBold
            };
        }

        /// <summary>次要说明：例如风险提示、补充信息。</summary>
        public static Run Muted(string text)
        {
            return new Run(text ?? string.Empty) { Foreground = MutedBrush };
        }

        /// <summary>成功类信息（绿色）。</summary>
        public static Run Success(string text)
        {
            return new Run(text ?? string.Empty) { Foreground = SuccessBrush };
        }

        /// <summary>失败 / 危险类信息（红色）。</summary>
        public static Run Danger(string text)
        {
            return new Run(text ?? string.Empty) { Foreground = DangerBrush };
        }

        /// <summary>换行。</summary>
        public static LineBreak Line()
        {
            return new LineBreak();
        }

        /// <summary>可点击的链接片段，颜色与项目主题一致。</summary>
        public static Hyperlink Link(string text, Action click)
        {
            var link = new Hyperlink(new Run(text ?? string.Empty))
            {
                Foreground = LinkBrush,
                TextDecorations = null
            };

            if (click != null)
            {
                link.Click += (sender, e) => click();
            }
            return link;
        }
    }
}
