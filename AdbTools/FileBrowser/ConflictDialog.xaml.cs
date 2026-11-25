using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AdbTools.FileBrowser
{
    /// <summary>
    /// 同名文件冲突询问（参照 Windows 的「替换或跳过文件」）。
    /// 默认不勾选「为所有冲突执行此操作」，每遇到一个冲突都会询问。
    /// </summary>
    internal partial class ConflictDialog : Window
    {
        // 端标识配色：设备端=紫，电脑端=蓝
        private static readonly Color DeviceChipBackground = Color.FromRgb(0xF3, 0xF0, 0xFF);
        private static readonly Color DeviceChipForeground = Color.FromRgb(0x6D, 0x28, 0xD9);
        private static readonly Color PcChipBackground = Color.FromRgb(0xEF, 0xF6, 0xFF);
        private static readonly Color PcChipForeground = Color.FromRgb(0x1D, 0x4E, 0xD8);

        /// <summary>用户选择的处理方式。</summary>
        public ConflictAction Action { get; private set; }

        /// <summary>是否把本次选择应用到本批全部冲突。</summary>
        public bool ApplyToAll { get; private set; }

        /// <summary>用户点了取消（中止整批传输）。</summary>
        public bool Cancelled { get; private set; }

        public ConflictDialog(ConflictInfo info)
        {
            InitializeComponent();

            Action = ConflictAction.Skip;

            bool upload = info != null && info.Upload;
            bool targetIsDirectory = info != null && info.TargetIsDirectory;

            titleText.Text = targetIsDirectory
                ? "目标位置已存在同名文件夹"
                : "目标中已存在同名文件";

            // 明确方向，避免分不清「目标」是电脑还是设备
            directionText.Text = upload ? "上传　电脑端 → 设备端" : "下载　设备端 → 电脑端";
            subtitleText.Text = targetIsDirectory
                ? "类型不一致，不能直接覆盖"
                : "目标已有同名文件，请选择处理方式";

            SetSideChip(sourceChip, sourceChipText, !upload);   // 上传时源在电脑端
            SetSideChip(targetChip, targetChipText, upload);    // 上传时目标在设备端

            sourceName.Text = Path.GetFileName(info != null ? info.SourcePath : string.Empty);
            sourceName.ToolTip = info != null ? info.SourcePath : null;
            sourceMeta.Text = FormatMeta(info != null ? info.SourceSize : 0,
                info != null ? info.SourceModified : DateTime.MinValue);

            targetName.Text = Path.GetFileName(info != null ? info.TargetPath : string.Empty);
            targetName.ToolTip = info != null ? info.TargetPath : null;
            targetMeta.Text = targetIsDirectory
                ? "文件夹"
                : FormatMeta(info != null ? info.TargetSize : 0,
                    info != null ? info.TargetModified : DateTime.MinValue);

            // 类型不一致（目标处是文件夹）时不提供「替换」：删除整个目录风险太大
            overwriteButton.Visibility = targetIsDirectory ? Visibility.Collapsed : Visibility.Visible;
        }

        private static void SetSideChip(Border chip, TextBlock text, bool isDevice)
        {
            chip.Background = new SolidColorBrush(isDevice ? DeviceChipBackground : PcChipBackground);
            text.Foreground = new SolidColorBrush(isDevice ? DeviceChipForeground : PcChipForeground);
            text.Text = isDevice ? "设备端" : "电脑端";
        }

        private static string FormatMeta(long size, DateTime modified)
        {
            string sizeText = DeviceFileEntry.FormatSize(size);
            string timeText = modified == DateTime.MinValue
                ? "时间未知"
                : modified.ToString("yyyy-MM-dd HH:mm");
            return sizeText + "  ·  " + timeText;
        }

        private void overwrite_Click(object sender, RoutedEventArgs e)
        {
            Close(ConflictAction.Overwrite);
        }

        private void skip_Click(object sender, RoutedEventArgs e)
        {
            Close(ConflictAction.Skip);
        }

        private void keepBoth_Click(object sender, RoutedEventArgs e)
        {
            Close(ConflictAction.KeepBoth);
        }

        private void cancel_Click(object sender, RoutedEventArgs e)
        {
            Cancelled = true;
            DialogResult = false;
        }

        private void Close(ConflictAction action)
        {
            Action = action;
            ApplyToAll = applyAllCheck.IsChecked == true;
            DialogResult = true;
        }
    }
}
