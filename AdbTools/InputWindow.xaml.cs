using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace AdbTools
{
    /// <summary>
    /// InputWindow.xaml 的交互逻辑
    /// </summary>
    public partial class InputWindow : Window
    {
        private static readonly Brush HintBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
        private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        private static readonly Brush HighlightBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));

        public InputWindow()
        {
            InitializeComponent();
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

        /// <summary>无边框窗口的自绘标题栏：按住空白处拖动窗口。</summary>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

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
            // 用户重新输入时把红字错误提示恢复成普通说明
            UpdateHint(Hint);
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

        private void ShowError(string text)
        {
            hintText.Inlines.Clear();
            hintText.ToolTip = text;
            hintText.Inlines.Add(new Run(text) { Foreground = ErrorBrush });
            hintText.Visibility = Visibility.Visible;
        }

        /// <summary>标题栏上的按钮（关闭）不参与拖动。</summary>
        private static bool IsInsideButton(DependencyObject source)
        {
            while (source != null)
            {
                if (source is Button) return true;

                try
                {
                    source = VisualTreeHelper.GetParent(source);
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }
    }
}
