using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AdbTools.CustomControl
{
    /// <summary>
    /// HintPasswordBox.xaml 的交互逻辑
    /// </summary>
    public partial class HintPasswordBox : UserControl
    {
        public HintPasswordBox()
        {
            InitializeComponent();
        }

        [Description("设置或获取提示信息")]
        public object Hint
        {
            get => this.hint.Content;
            set => this.hint.Content = value;
        }

        [Description("设置或获取提示信息的颜色")]
        public Brush HintForeground
        {
            get => this.hint.Foreground;
            set => this.hint.Foreground = value;
        }

        [Description("设置或获取提示信息的字体大小")]
        public double HintFontSize
        {
            get => this.hint.FontSize;
            set => this.hint.FontSize = value;
        }

        [Description("设置或获取输入框的字体颜色")]
        public Brush TextForeground
        {
            get => this.passwordBox.Foreground;
            set => this.passwordBox.Foreground = value;
        }

        [Description("设置或获取输入框的字体大小")]
        public double TextFontSize
        {
            get => this.passwordBox.FontSize;
            set => this.passwordBox.FontSize = value;
        }

        [Description("设置或获取输入框的文字")]
        public string Text
        {
            get => this.passwordBox.Password;
            set
            {
                this.passwordBox.Password = value;
                this.UpdateLayout();
            }
        }

        [Description("设置或获取输入框的文字的最大字符数")]
        public int MaxLength
        {
            get => this.passwordBox.MaxLength;
            set => this.passwordBox.MaxLength = value;
        }

        [Description("设置或获取控件的内边距")]
        public Thickness BoxPadding
        {
            get
            {
                Thickness t = this.passwordBox.Padding;
                return new Thickness(t.Left - 10, t.Top, t.Right, t.Bottom);
            }
            set
            {
                this.passwordBox.Padding = new Thickness(10 + value.Left, value.Top, value.Right, value.Bottom);
                this.hint.Margin = new Thickness(10 + value.Left, 0, 0, 0);
            }
        }

        [Description("设置或获取PasswordBox水平对齐方式")]
        public HorizontalAlignment BoxHorizontalContentAlignment
        {
            get => this.passwordBox.HorizontalContentAlignment;
            set => this.passwordBox.HorizontalContentAlignment = value;
        }

        [Description("设置控件BoxPasting事件")]
        public event EventHandler<TextBoxPastingEventArgs> BoxPasting;
        private void passwordBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            BoxPasting?.Invoke(sender, new TextBoxPastingEventArgs(e));
        }

        [Description("设置控件BoxPreviewKeyDown事件")]
        public event KeyEventHandler BoxPreviewKeyDown;

        private void passwordBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            BoxPreviewKeyDown?.Invoke(sender, e);
        }

        [Description("设置控件BoxPreviewTextInput事件")]
        public event TextCompositionEventHandler BoxPreviewTextInput;
        private void passwordBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            BoxPreviewTextInput?.Invoke(sender, e);
        }

        [Description("设置控件PasswordBoxPasswordChanged事件")]
        public event RoutedEventHandler PasswordBoxPasswordChanged;

        private void passwordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (this.IsLoad)
            {
                UpdateHintVisibility(this.Text);
            }

            PasswordBoxPasswordChanged?.Invoke(sender, e);
        }

        private bool IsLoad;

        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            this.IsLoad = true;

            UpdateHintVisibility(this.Text);
        }

        public void TextFocus()
        {
            this.passwordBox.Focus();
        }

        /// <summary>
        /// 根据输入框内容切换提示信息的显隐
        /// </summary>
        private void UpdateHintVisibility(string text)
        {
            this.hint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
        }

    }
}
