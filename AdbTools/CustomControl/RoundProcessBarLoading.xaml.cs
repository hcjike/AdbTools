using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace AdbTools.CustomControl
{
    /// <summary>
    /// RoundProcessBarLoading.xaml 的交互逻辑
    /// </summary>
    public partial class RoundProcessBarLoading : UserControl
    {
        public RoundProcessBarLoading()
        {
            InitializeComponent();
        }

        [Description("获取或设置是否显示动画")]
        public bool IsShowAnimation
        {
            set
            {
                this.defaultGrid.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                this.animationGrid.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
            get => Visibility.Visible == this.animationGrid.Visibility;
        }
    }
}
