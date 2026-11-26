using AdbTools.bean;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;

using AdbTools;

namespace AdbTools.Windows
{
    /// <summary>集中显示安装失败原因，支持复制原因和定位到安装列表项。</summary>
    public partial class InstallErrorWindow : Window
    {
        private readonly ApkInstallItem[] _items;

        public InstallErrorWindow(IEnumerable<ApkInstallItem> items)
        {
            InitializeComponent();

            _items = items == null ? new ApkInstallItem[0] : items.ToArray();
            errorList.ItemsSource = _items;
            headerText.Text = "共 " + _items.Length + " 项失败，原因已按应用整理";
            countText.Text = "共 " + _items.Length + " 项失败";
        }

        private void CopyReason_Click(object sender, RoutedEventArgs e)
        {
            ApkInstallItem item = GetItem(sender);
            if (item == null) return;

            try
            {
                Clipboard.SetText(item.ErrorText ?? string.Empty);
                countText.Text = "已复制「" + item.DisplayName + "」的失败原因";
            }
            catch (Exception)
            {
                countText.Text = "复制失败，请手动选择原因文本";
            }
        }

        private void CopyAll_Click(object sender, RoutedEventArgs e)
        {
            var text = new StringBuilder();
            for (int i = 0; i < _items.Length; i++)
            {
                ApkInstallItem item = _items[i];
                if (i > 0) text.AppendLine().AppendLine();

                text.Append(i + 1).Append(". ").Append(item.DisplayName);
                if (!string.IsNullOrWhiteSpace(item.PackageName))
                    text.Append("（").Append(item.PackageName).Append("）");
                text.AppendLine();
                text.Append(item.ErrorText ?? "未知错误");
            }

            try
            {
                Clipboard.SetText(text.ToString());
                countText.Text = "已复制全部 " + _items.Length + " 项失败原因";
            }
            catch (Exception)
            {
                countText.Text = "复制失败，请逐项复制原因";
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private static ApkInstallItem GetItem(object sender)
        {
            FrameworkElement element = sender as FrameworkElement;
            return element == null ? null : element.DataContext as ApkInstallItem;
        }
    }
}
