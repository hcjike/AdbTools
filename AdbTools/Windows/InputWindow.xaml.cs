using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

using AdbTools;

namespace AdbTools.Windows
{
    /// <summary>
    /// InputWindow.xaml 的交互逻辑
    /// </summary>
    public partial class InputWindow : Window
    {
        private static readonly Brush HintBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
        private static readonly Brush HighlightBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));

        public InputWindow()
        {
            InitializeComponent();

            // 首次布局前就定好输入框高度范围，避免窗口先按单行高度显示再跳变
            ApplyContentHeightLimits();
        }

        public string Result { get; private set; }

        /// <summary>输入框上方的说明文字；为空时不显示，也不占高度。</summary>
        public string Hint { get; set; }

        /// <summary>
        /// <see cref="Hint"/> 中需要高亮显示的片段（路径、原文件名等）。
        /// 找不到时整行按普通提示显示。
        /// </summary>
        public string HintHighlight { get; set; }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            titleText.Text = Title;
            UpdateHint(Hint);
            content.Focus();
        }

        /// <summary>
        /// 输入框高度随内容自动增长：最少 3 行、最多 10 行。
        /// 行高按当前字体实测，避免写死像素后换字号或缩放比例时行数不准；
        /// 超过 10 行后不再长高，改由输入框内部滚动。
        /// </summary>
        private void ApplyContentHeightLimits()
        {
            var probe = new TextBlock
            {
                FontFamily = content.FontFamily,
                FontSize = content.FontSize,
                FontStyle = content.FontStyle,
                FontWeight = content.FontWeight,
                FontStretch = content.FontStretch,
                Text = "国Ag"
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // 内边距与边框不属于文本行，单独加上
            double chrome = content.Padding.Top + content.Padding.Bottom
                + content.BorderThickness.Top + content.BorderThickness.Bottom;

            content.MinHeight = Math.Ceiling(probe.DesiredSize.Height * 3 + chrome);
            content.MaxHeight = Math.Ceiling(probe.DesiredSize.Height * 10 + chrome);
        }

        /// <summary>无边框窗口的自绘标题栏：按住空白处拖动窗口。</summary>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;

            try
            {
                DragMove();
            }
            catch
            {
                // 鼠标已经松开时 DragMove 会抛异常，忽略即可
            }
        }

        private void content_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 用户重新输入时收起输入框下方的错误提示，上方固定说明保持不变
            HideError();
        }

        private void enter_Click(object sender, RoutedEventArgs e)
        {
            string value = content.Text;

            // 空名字不关窗，直接在说明行给红字提示
            if (string.IsNullOrWhiteSpace(value))
            {
                ShowError("名称不能为空");
                content.Focus();
                return;
            }

            Result = value;
            DialogResult = true;
            Close();
        }

        private void cancle_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// 说明行：普通文字用灰色，路径 / 原文件名这类关键信息用主题蓝加粗，
        /// 让用户一眼看到操作对象。
        /// </summary>
        private void UpdateHint(string text)
        {
            if (hintText == null) return;

            hintText.Inlines.Clear();
            hintText.ToolTip = text;

            if (string.IsNullOrEmpty(text))
            {
                hintText.Visibility = Visibility.Collapsed;
                return;
            }

            string highlight = HintHighlight;
            int index = string.IsNullOrEmpty(highlight)
                ? -1
                : text.IndexOf(highlight, StringComparison.Ordinal);

            if (index < 0)
            {
                hintText.Inlines.Add(new Run(text) { Foreground = HintBrush });
            }
            else
            {
                if (index > 0)
                {
                    hintText.Inlines.Add(new Run(text.Substring(0, index)) { Foreground = HintBrush });
                }

                hintText.Inlines.Add(new Run(highlight)
                {
                    Foreground = HighlightBrush,
                    FontWeight = FontWeights.SemiBold
                });

                int end = index + highlight.Length;
                if (end < text.Length)
                {
                    hintText.Inlines.Add(new Run(text.Substring(end)) { Foreground = HintBrush });
                }
            }

            hintText.Visibility = Visibility.Visible;
        }

        /// <summary>在输入框下方显示红字错误提示，不覆盖上方的固定说明。</summary>
        private void ShowError(string text)
        {
            if (errorText == null) return;

            errorText.Text = text ?? string.Empty;
            errorText.ToolTip = text;
            errorText.Visibility = Visibility.Visible;
        }

        /// <summary>收起输入框下方的错误提示。</summary>
        private void HideError()
        {
            if (errorText == null) return;

            errorText.Text = string.Empty;
            errorText.ToolTip = null;
            errorText.Visibility = Visibility.Collapsed;
        }

    }
}
