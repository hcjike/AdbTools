using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AdbTools.CustomControl
{
    /// <summary>
    /// ProgressBarControl.xaml 的交互逻辑
    /// </summary>
    public partial class ProgressBarControl : UserControl
    {
        public ProgressBarControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 获取或设置进度条的背景色
        /// </summary>
        [Description("获取或设置进度条的背景色")]
        public Brush ProgressBackground
        {
            get => progressBG.Fill;
            set => progressBG.Fill = value;
        }

        /// <summary>
        /// 获取或设置进度条的边框色
        /// </summary>
        [Description("获取或设置进度条的边框色")]
        public Brush ProgressStroke
        {
            get => progressBG.Stroke;
            set => progressBG.Stroke = value;
        }

        /// <summary>
        /// 获取或设置进度条的边框粗细
        /// </summary>
        [Description("获取或设置进度条的边框粗细")]
        public double ProgressStrokeThickness
        {
            get => progressBG.StrokeThickness;
            set => progressBG.StrokeThickness = value;
        }

        /// <summary>
        /// 获取或设置进度条的进度色
        /// </summary>
        [Description("获取或设置进度条的进度色")]
        public Brush ProgressColor
        {
            get => progressValue.Fill;
            set => progressValue.Fill = value;
        }

        /// <summary>
        /// 获取或设置进度条提示信息
        /// </summary>
        [Description("获取或设置进度条提示信息")]
        public string HintProgress
        {
            get => progressTip.Content?.ToString();
            set => progressTip.Content = value;
        }

        /// <summary>
        /// 获取或设置进度条提示信息文字大小
        /// </summary>
        [Description("获取或设置进度条提示信息文字大小")]
        public double HintFontSize
        {
            get => progressTip.FontSize;
            set => progressTip.FontSize = value;
        }

        /// <summary>
        /// 获取或设置进度条提示信息字体颜色
        /// </summary>
        [Description("获取或设置进度条提示信息字体颜色")]
        public Brush HintForeground
        {
            get => progressTip.Foreground;
            set => progressTip.Foreground = value;
        }

        private double _Maximum = 100.0;

        /// <summary>
        /// 获取或设置进度条的最大进度值,默认值为100
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">进度值小于等于零时抛出</exception>
        [Description("获取或设置进度条的最大进度值")]
        public double Maximum
        {
            get => _Maximum;
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value), "进度值不可小于等于零");
                _Maximum = value;
            }
        }

        private double _Value;

        /// <summary>
        /// 获取或设置进度条的进度值（自动限制在 0 ~ Maximum 之间）
        /// </summary>
        [Description("获取或设置进度条的进度值")]
        public double Value
        {
            get => _Value;
            set
            {
                _Value = Math.Max(0, Math.Min(_Maximum, value));

                if (progressBG.ActualWidth > 0)
                {
                    progressValue.Width = (_Value / _Maximum) * progressBG.ActualWidth;
                }
            }
        }

    }
}
