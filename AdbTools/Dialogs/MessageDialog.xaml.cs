using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace AdbTools
{
    /// <summary>
    /// 统一样式的提示 / 确认对话框：无系统边框，标题栏可拖动，高度自适应，
    /// 正文超长时限高滚动并支持复制。业务代码通过 <see cref="Dialogs"/> 调用，不要直接 new。
    /// </summary>
    public partial class MessageDialog : Window
    {
        private static readonly Brush InfoBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x9F, 0xFF));
        private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        private static readonly Brush PrimaryButtonBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x9F, 0xFF));
        private static readonly Brush DangerButtonBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

        /// <summary>纯文本快照：复制时优先用它，避免富文本拼装后取回的文字不一致。</summary>
        private string _plainText;

        internal MessageDialog(Action<InlineCollection> buildContent, string plainText,
            string caption, MessageBoxImage icon,
            bool confirm, string confirmText, string cancelText, bool danger)
        {
            InitializeComponent();

            Title = caption ?? string.Empty;
            titleText.Text = caption ?? string.Empty;
            SetContent(buildContent, plainText);

            ApplyIcon(icon);

            confirmButton.Content = string.IsNullOrWhiteSpace(confirmText) ? "确定" : confirmText;
            cancelButton.Content = string.IsNullOrWhiteSpace(cancelText) ? "取消" : cancelText;
            cancelButton.Visibility = confirm ? Visibility.Visible : Visibility.Collapsed;
            confirmButton.Background = danger ? DangerButtonBrush : PrimaryButtonBrush;

            // 提示框只有一个按钮：Esc 也直接关闭（先解绑隐藏的取消按钮，避免两个 IsCancel）
            if (!confirm)
            {
                cancelButton.IsCancel = false;
                confirmButton.IsCancel = true;
            }

            ApplySizeLimits();
        }

        /// <summary>
        /// 正文内容：富文本由调用方拼装 Inlines，纯文本则作为一个 Run 放入
        /// （TextBlock 会保留 Run 里的换行）。
        /// </summary>
        private void SetContent(Action<InlineCollection> buildContent, string plainText)
        {
            messageText.Inlines.Clear();
            _plainText = plainText;

            if (buildContent != null)
            {
                buildContent(messageText.Inlines);
                return;
            }

            AddPlainText(plainText);
        }

        /// <summary>
        /// 纯文本按行拆成 Run + LineBreak，确保多行清单在 TextBlock 里严格按原样换行。
        /// </summary>
        private void AddPlainText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) messageText.Inlines.Add(new LineBreak());
                messageText.Inlines.Add(new Run(lines[i]));
            }
        }

        /// <summary>Ctrl+C 复制全部内容，与系统 MessageBox 的习惯保持一致。</summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                CopyContent();
                e.Handled = true;
            }
        }

        private void CopyContent_Click(object sender, RoutedEventArgs e)
        {
            CopyContent();
        }

        private void CopyContent()
        {
            try
            {
                Clipboard.SetText(_plainText ?? messageText.Text ?? string.Empty);
            }
            catch
            {
                // 剪贴板被占用时忽略，不影响对话框本身
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Activate();
            confirmButton.Focus();
        }

        /// <summary>
        /// 正文自动高度，但最多占工作区一半左右，超出后内部滚动；
        /// 在首次布局前设置，避免超长内容先把窗口撑到屏幕外再缩回来。
        /// </summary>
        private void ApplySizeLimits()
        {
            double workHeight = SystemParameters.WorkArea.Height;
            messageScroll.MaxHeight = Math.Max(120, workHeight * 0.55);

            // 宽度随内容自适应（XAML 里 300~620），小屏时再收窄上限，避免超出屏幕
            double workWidth = SystemParameters.WorkArea.Width;
            if (workWidth > 0)
            {
                double maxWidth = Math.Min(620, Math.Max(300, workWidth - 60));
                MaxWidth = maxWidth;
                MinWidth = Math.Min(300, maxWidth);
            }
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

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        /// <summary>按图标类型设置左侧徽标；未指定类型时整个图标不显示，也不占位。</summary>
        private void ApplyIcon(MessageBoxImage icon)
        {
            string glyph;
            Brush brush;

            // Hand/Stop/Exclamation/Asterisk 是同一枚举值的别名，这里只取主值
            switch (icon)
            {
                case MessageBoxImage.Error:
                    glyph = "×";
                    brush = ErrorBrush;
                    break;

                case MessageBoxImage.Warning:
                    glyph = "!";
                    brush = WarningBrush;
                    break;

                case MessageBoxImage.Question:
                    glyph = "?";
                    brush = InfoBrush;
                    break;

                case MessageBoxImage.Information:
                    glyph = "i";
                    brush = InfoBrush;
                    break;

                default:
                    titleIconBadge.Visibility = Visibility.Collapsed;
                    return;
            }

            titleIconBadge.Background = brush;
            titleIconGlyph.Text = glyph;
            titleIconBadge.Visibility = Visibility.Visible;
        }

    }
}
