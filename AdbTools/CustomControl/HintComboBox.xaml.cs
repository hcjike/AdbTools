using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AdbTools.CustomControl
{
    /// <summary>
    /// HintComboBox.xaml 的交互逻辑
    /// </summary>
    public partial class HintComboBox : UserControl
    {
        public HintComboBox()
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
            get => this.comboBox.Foreground;
            set => this.comboBox.Foreground = value;
        }

        [Description("设置或获取输入框的字体大小")]
        public double TextFontSize
        {
            get => this.comboBox.FontSize;
            set => this.comboBox.FontSize = value;
        }

        [Description("设置或获取下划线的颜色")]
        public Brush LineStroke
        {
            get => this.line.Stroke;
            set => this.line.Stroke = value;
        }

        [Description("设置或获取输入框的文本")]
        public string Text
        {
            get => this.comboBox.Text;
            set
            {
                UpdateHintVisibility(value);
                this.comboBox.Text = value;
                this.UpdateLayout();
            }
        }

        [Description("设置或获取控件是否只读")]
        public bool IsReadOnly
        {
            get => this.comboBox.IsReadOnly;
            set => this.comboBox.IsReadOnly = value;
        }

        [Description("获取控件的所有项")]
        public ItemCollection Items => this.comboBox.Items;

        [Description("设置或获取控件的内边距")]
        public Thickness ComboBoxPadding
        {
            get
            {
                Thickness t = this.comboBox.Padding;
                return new Thickness(t.Left - 10, t.Top, t.Right, t.Bottom);
            }
            set
            {
                this.comboBox.Padding = new Thickness(10 + value.Left, value.Top, value.Right, value.Bottom);
                this.hint.Margin = new Thickness(10 + value.Left, 0, 0, 0);
            }
        }

        [Description("设置或获取控件的下拉条高度")]
        public double MaxDropDownHeight
        {
            get => this.comboBox.MaxDropDownHeight;
            set => this.comboBox.MaxDropDownHeight = value;
        }

        [Description("下拉改变事件，可以获取到选择的项")]
        public event EventHandler<SelectionChangedEventArgs> SelectionChanged;

        private void comboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (this.IsLoad)
            {
                UpdateHintVisibility(this.Text);
                SelectionChanged?.Invoke(sender, e);
            }
        }

        private bool IsLoad;
        private void UserControl_Loaded(object sender, RoutedEventArgs e)
        {
            this.IsLoad = true;
            this.comboBox.Height = this.ActualHeight;
            this.comboBox.Width = this.ActualWidth;

            UpdateHintVisibility(Text);
        }

        private void comboBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (this.IsLoad)
            {
                UpdateHintVisibility(this.Text);
            }
        }

        private void comboBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (this.IsLoad)
            {
                UpdateHintVisibility(this.Text);
            }
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
