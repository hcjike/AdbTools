using AdbTools.AccessInterface;
using AdbTools.bean;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AdbTools
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        public static RoutedCommand CommandF5 = new RoutedCommand();
        public static RoutedCommand CommandF8 = new RoutedCommand();


        public static string adbPath = "";
        private ObservableCollection<string> deviceAddressHistory;
        private ObservableCollection<Device> deviceAddressList;

        public MainWindow()
        {
            InitializeComponent();

            deviceAddressHistory = new ObservableCollection<string>();
            deviceAddressList = new ObservableCollection<Device>();

            // 创建 CommandBinding 并绑定到处理程序
            CommandBinding commandF5Binding = new CommandBinding(
                CommandF5,
                CommandF5_Executed,
                CommandF5_CanExecute);

            CommandBinding commandF8Binding = new CommandBinding(
                CommandF8,
                CommandF8_Executed,
                CommandF8_CanExecute);


            this.CommandBindings.Add(commandF5Binding);
            this.CommandBindings.Add(commandF8Binding);

        }

        private void CommandF5_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            Timer_Tick(null, null);
        }

        private void CommandF5_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true; // 始终可以执行
        }

        private void CommandF8_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            pairDevice_Click(null, null);
        }

        private void CommandF8_CanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = true; // 始终可以执行
        }

        private void refreshDeviceAddressHistory()
        {
            deviceAddressHistory.Clear();
            foreach (string address in Globals.AppSettings.DEVICE_ADDRESS_HISTORY)
            {
                deviceAddressHistory.Add(address);
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            this.Title = $"{this.Title} V{App.Version}";

            // adb相关资源文件
            string adbFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe");
            string adbDll1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AdbWinApi.dll");
            string adbDll2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AdbWinUsbApi.dll");
            if (!File.Exists(adbFilePath) || !File.Exists(adbDll1) || !File.Exists(adbDll2))
            {
                MessageBox.Show("adb工具集丢失，无法运行！", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(0);
                return;
            }

            // 配置文件中未设置代理时，将默认代理写入配置
            if (!Globals.AppSettings.IS_GITHUB_PROXY)
            {
                Globals.AppSettings.GITHUB_PROXY = Globals.AppSettings.GITHUB_PROXY;
            }

            RequestJson.UpdateCheck(this, updateVersion);

            adbPath = $"\"{adbFilePath}\"";

            deviceAddress.Text = Globals.AppSettings.LAST_DEVICE_ADDRESS;
            deviceAddress.ItemsSource = deviceAddressHistory;
            refreshDeviceAddressHistory();

            deviceList.ItemsSource = deviceAddressList;

            Timer_Tick(null, null);
        }

        private void Window_Unloaded(object sender, RoutedEventArgs e)
        {
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            waitAnimation(true);
            CmdExecutor.ExecuteCommandAndReturnAsync($"{adbPath} devices", content =>
            {
                RefreshDeviceList(content);
                waitAnimation(false);
            });
        }

        /// <summary>
        /// 根据 adb devices 输出刷新设备列表（保持adb返回顺序，新设备插入到对应位置）
        /// </summary>
        private void RefreshDeviceList(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                // 命令执行失败或被中断时保留当前列表，避免误清空
                // （正常执行时至少有cmd横幅与"List of devices attached"输出，不会为空）
                return;
            }

            int index = 0;
            List<string> deviceMarks = new List<string>();

            foreach (string line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.EndsWith("device"))
                {
                    // 跳过 unauthorized、offline 等非就绪状态及cmd回显行
                    continue;
                }

                string address = line.Replace("device", "").Trim();
                deviceMarks.Add(address);

                Device exist = deviceAddressList.FirstOrDefault(o => o.DeviceMark == address);
                if (exist == null)
                {
                    deviceAddressList.Insert(index, new Device
                    {
                        IsWifiConnect = Globals.IsValidIPEndPoint(address),
                        DeviceMark = address,
                        ShowDeviceName = address
                    });
                }
                else
                {
                    int oldIndex = deviceAddressList.IndexOf(exist);
                    if (oldIndex != index)
                    {
                        deviceAddressList.Move(oldIndex, index);
                    }
                }
                index++;
            }

            // 移除已不存在的设备
            foreach (Device device in deviceAddressList.Where(o => !deviceMarks.Contains(o.DeviceMark)).ToList())
            {
                deviceAddressList.Remove(device);
            }

            foreach (Device device in deviceAddressList)
            {
                device.Refresh();
            }

            // 空列表时显示提示（HasItems不发送更改通知，需手动同步）
            emptyHint.Visibility = deviceAddressList.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void connectDevice_Click(object sender, RoutedEventArgs e)
        {
            string address = deviceAddress.Text.Replace("：", ":");
            deviceAddress.Text = address;
            if (!Globals.IsValidIPEndPoint(address))
            {
                MessageBox.Show("设备连接地址输入错误！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            waitAnimation(true);
            CmdExecutor.ExecuteCommandAndReturnAsync($"{adbPath} connect {address}", result =>
            {
                if (string.IsNullOrWhiteSpace(result) || Globals.ContainsAny(result, "(10060)", "(10061)", "failed to connect"))
                {
                    waitAnimation(false);
                    MessageBox.Show("设备连接失败！\r\n1.请检查IP和端口输入是否正确；\r\n2.设备与电脑是否完成配对。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Globals.AppSettings.LAST_DEVICE_ADDRESS = address;

                string ip = address.Split(':')[0];
                List<string> history = Globals.AppSettings.DEVICE_ADDRESS_HISTORY.Where(item => !item.StartsWith(ip)).ToList();
                history.Insert(0, address);
                Globals.AppSettings.DEVICE_ADDRESS_HISTORY = history;
                refreshDeviceAddressHistory();
                deviceAddress.Text = Globals.AppSettings.LAST_DEVICE_ADDRESS;

                Timer_Tick(null, null);
                waitAnimation(false);

            }, err =>
            {
                waitAnimation(false);
                MessageBox.Show("设备连接失败！\r\n1.请检查IP和端口输入是否正确；\r\n2.设备与电脑是否完成配对。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return true;
            });
        }

        private void pairDevice_Click(object sender, RoutedEventArgs e)
        {
            PairWindow pairWindow = new PairWindow()
            {
                AdbPath = adbPath,
                Address = deviceAddress.Text,
            };
            pairWindow.Owner = this;
            pairWindow.ShowDialog();
        }

        private string[] SelectApkFile()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "APK Files (*.apk)|*.apk",
                FilterIndex = 1,
                Multiselect = true,
                RestoreDirectory = true
            };

            if (openFileDialog.ShowDialog() == true)
            {
                // 获取选中的文件路径
                return openFileDialog.FileNames;
            }
            return null;
        }

        private void installApkItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            string[] paths = SelectApkFile();
            if (paths == null || paths.Length == 0)
            {
                return;
            }
            HandleApkInstallation(new List<string>(paths), 0, device);
        }

        private void disconnectDeviceItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }

            if (MessageBoxResult.OK != MessageBox.Show($"确定要断开设备【{device.ShowDeviceName}】的连接吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                return;
            }
            waitAnimation(true);
            CmdExecutor.ExecuteCommandAndReturnAsync($"{adbPath} disconnect {device.DeviceMark}", result =>
            {
                Timer_Tick(null, null);
                waitAnimation(false);
            });
        }

        private void disconnectAllDeviceItem_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBoxResult.OK == MessageBox.Show("确定要【断开所有设备】吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                waitAnimation(true);
                CmdExecutor.ExecuteCommandAndReturnAsync($"{adbPath} disconnect", result =>
                {
                    Timer_Tick(null, null);
                    waitAnimation(false);
                });
            }
        }

        private void deleteHistory_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = (MenuItem)sender;
            var contextMenu = (ContextMenu)menuItem.Parent;
            var textBlock = (TextBlock)contextMenu.PlacementTarget;
            if (MessageBoxResult.OK != MessageBox.Show($"确定要删除记录【{textBlock.Text}】吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                return;
            }
            Globals.AppSettings.DEVICE_ADDRESS_HISTORY.Remove(textBlock.Text);
            Globals.AppSettings.DEVICE_ADDRESS_HISTORY = Globals.AppSettings.DEVICE_ADDRESS_HISTORY;
            refreshDeviceAddressHistory();
        }

        private void deleteAllHistory_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBoxResult.OK == MessageBox.Show("确定要【清空所有历史纪录】吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                Globals.AppSettings.DEVICE_ADDRESS_HISTORY.Clear();
                Globals.AppSettings.DEVICE_ADDRESS_HISTORY = Globals.AppSettings.DEVICE_ADDRESS_HISTORY;
                refreshDeviceAddressHistory();
            }
        }

        private void waitAnimation(bool show)
        {
            processBarGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            waitProcessBar.IsShowAnimation = show;
        }

        private void refreshList_Click(object sender, RoutedEventArgs e)
        {
            Timer_Tick(null, null);
        }

        /// <summary>
        /// 获取当前选中的设备，未选中时提示并返回null
        /// </summary>
        private Device GetSelectedDevice()
        {
            int index = deviceList.SelectedIndex;
            if (index < 0 || index >= deviceAddressList.Count)
            {
                MessageBox.Show("请先选择一个设备！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            return deviceAddressList[index];
        }

        /// <summary>
        /// 对选中设备执行设置类命令（统一确认、执行、成功/失败提示流程）
        /// </summary>
        /// <param name="actionName">操作名称，如“恢复默认DPI”</param>
        /// <param name="commandFactory">根据设备生成要执行的命令</param>
        /// <param name="failTip">失败提示的补充说明</param>
        private void ExecuteDeviceSetting(string actionName, Func<Device, string> commandFactory, string failTip)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            if (MessageBoxResult.OK != MessageBox.Show($"确定要对设备【{device.ShowDeviceName}】执行【{actionName}】吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                return;
            }
            waitAnimation(true);
            CmdExecutor.ExecuteCommandAndReturnAsync(new[] { commandFactory(device) },
                result =>
                {
                    waitAnimation(false);
                    MessageBox.Show($"设备【{device.ShowDeviceName}】{actionName}成功", "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                },
                err =>
                {
                    waitAnimation(false);
                    MessageBox.Show($"设备【{device.ShowDeviceName}】{actionName}失败！\r\n{failTip}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                });
        }

        private void resetPortItem_Click(object sender, RoutedEventArgs e)
        {
            ExecuteDeviceSetting("恢复默认TCP端口(5555)", d => $"{adbPath} -s {d.DeviceMark} tcpip 5555", "检查设备是否已离线。");
        }

        private void resetPXItem_Click(object sender, RoutedEventArgs e)
        {
            ExecuteDeviceSetting("恢复默认分辨率", d => $"{adbPath} -s {d.DeviceMark} shell wm size reset", "检查设备是否已离线。");
        }

        private void resetDPIItem_Click(object sender, RoutedEventArgs e)
        {
            ExecuteDeviceSetting("恢复默认DPI", d => $"{adbPath} -s {d.DeviceMark} shell wm density reset", "检查设备是否已离线。");
        }

        private void logcatItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            CmdExecutor.ExecuteCommandByShell($"{adbPath} -s {device.DeviceMark} logcat");
        }

        private void logcatDebugItem_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            CmdExecutor.ExecuteCommandByShell($"{adbPath} -s {device.DeviceMark} logcat -s DEBUG");
        }

        private void openShell_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            CmdExecutor.ExecuteCommandByShell($"{adbPath} -s {device.DeviceMark} shell ");
        }

        #region 《版本更新》

        private void updateVersion_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                GithubReleases githubReleases = (GithubReleases)updateVersion.Tag;
                UpdateVersion(githubReleases);
            }
        }

        private void UpdateVersion(GithubReleases githubReleases)
        {
            if (githubReleases == null)
            {
                return;
            }
            GithubReleasesAssets asset = githubReleases.Assets?
                .FirstOrDefault(a => RequestJson.UpdateFileName.Equals(a.Name, StringComparison.OrdinalIgnoreCase));
            if (asset == null)
            {
                return;
            }

            updateVersion.Visibility = Visibility.Visible;
            updateVersion.Tag = githubReleases;

            if (MessageBoxResult.OK != MessageBox.Show($"发现新版本【V{githubReleases.tag_name}】是否更新？", "新版本", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                return;
            }

            List<string> cmdArges = new List<string>
            {
                asset.browser_download_url,
                AppDomain.CurrentDomain.BaseDirectory,
                $"{AppDomain.CurrentDomain.BaseDirectory}{AppDomain.CurrentDomain.FriendlyName}"
            };

            if (CmdExecutor.StartExe($"{AppDomain.CurrentDomain.BaseDirectory}Update.exe", cmdArges))
            {
                Application.Current.Shutdown();
            }
        }

        #endregion

        #region 《关于菜单及控制》

        private void about_Click(object sender, RoutedEventArgs e)
        {
            about_popup.IsOpen = true;
        }

        private void projectAddress_Click(object sender, RoutedEventArgs e)
        {
            about_popup.IsOpen = false;
            CmdExecutor.StartExe("https://github.com/hcjike/AdbTools");
        }


        private void projectAddressMirror_Click(object sender, RoutedEventArgs e)
        {
            about_popup.IsOpen = false;
            CmdExecutor.StartExe("https://code.hcjike.com/hcjike/adbtools");
        }

        private void cheackUpdate_Click(object sender, RoutedEventArgs e)
        {
            about_popup.IsOpen = false;
            RequestJson.UpdateCheck(this, githubReleases =>
            {
                UpdateVersion(githubReleases);
            });
        }

        #endregion

        #region 《设置菜单及控制》

        private void setup_Click(object sender, RoutedEventArgs e)
        {
            setup_popup.IsOpen = true;
        }

        private void topmastCB_Click(object sender, RoutedEventArgs e)
        {
            this.Topmost = (bool)topmastCB.IsChecked;
            setup_popup.IsOpen = false;
        }
        #endregion

        #region 《应用包管理》

        private void customkEnable_Click(object sender, RoutedEventArgs e)
        {
            CustomPackage_Click("启用", "pm enable");
        }

        private void customDisable_Click(object sender, RoutedEventArgs e)
        {
            CustomPackage_Click("禁用", "pm disable-user");
        }

        /// <summary>
        /// 输入包名并执行启用/禁用
        /// </summary>
        private void CustomPackage_Click(string action, string pmCommand)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            InputWindow inputWindow = new InputWindow()
            {
                Title = "请输入包名",
                Owner = this
            };
            if (inputWindow.ShowDialog() != true)
            {
                return;
            }
            string package = inputWindow.Result;
            if (MessageBoxResult.OK != MessageBox.Show($"确定要对设备【{device.ShowDeviceName}】执行【{action}包名{package}】吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                return;
            }
            waitAnimation(true);
            CmdExecutor.ExecuteCommandAndReturnAsync(new[] { $"{adbPath} -s {device.DeviceMark} shell {pmCommand} {package}" },
                result =>
                {
                    waitAnimation(false);
                    MessageBox.Show($"设备【{device.ShowDeviceName}】{action}包名{package}成功", "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                },
                err =>
                {
                    waitAnimation(false);
                    MessageBox.Show($"设备【{device.ShowDeviceName}】{action}包名{package}失败！\r\n1.检查设备是否已离线；\r\n2.应用包名未找到！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                });
        }

        private void systemAppPackagep_Click(object sender, RoutedEventArgs e)
        {
            ShowPackageList("-s", "系统安装应用包名");
        }

        private void thirdPartyAppPackage_Click(object sender, RoutedEventArgs e)
        {
            ShowPackageList("-3", "第三方安装应用包名");
        }

        private void allAppPackage_Click(object sender, RoutedEventArgs e)
        {
            ShowPackageList("", "所有安装应用包名");
        }

        /// <summary>
        /// 获取设备已安装应用包名列表并展示
        /// </summary>
        /// <param name="arguments">pm list packages 的参数，如 -s / -3</param>
        /// <param name="title">展示窗口标题</param>
        private void ShowPackageList(string arguments, string title)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            waitAnimation(true);
            CmdExecutor.ExecuteCommandAndReturnAsync(new[] { $"{adbPath} -s {device.DeviceMark} shell pm list packages {arguments}".TrimEnd() }, result =>
            {
                List<string> packages = ParsePackageList(result);
                waitAnimation(false);
                InfoShowWindow infoShowWindow = new InfoShowWindow()
                {
                    Title = title,
                    DescriptionContent = string.Join("\r\n", packages)
                };
                infoShowWindow.Show();
            },
            err =>
            {
                waitAnimation(false);
                MessageBox.Show($"设备【{device.ShowDeviceName}】获取{title}失败！\r\n检查设备是否已离线。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return true;
            });
        }

        /// <summary>
        /// 解析 “pm list packages” 输出（兼容cmd回显与提示符行，仅提取 package: 开头的行）
        /// </summary>
        private static List<string> ParsePackageList(string result)
        {
            if (string.IsNullOrEmpty(result))
            {
                return new List<string>();
            }
            return result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("package:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line.Substring("package:".Length).Trim())
                .ToList();
        }

        private void disableAllAppPackage_Click(object sender, RoutedEventArgs e)
        {
            Device device = GetSelectedDevice();
            if (device == null)
            {
                return;
            }
            if (!"alps k71v1_64_bsp".Equals(device.DeviceInfo, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("仅支持操作设备【alps k71v1_64_bsp】", "提示", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AlpsK71v1Window alpsK71V1Window = new AlpsK71v1Window()
            {
                Title = $"一键处理【{device.ShowDeviceName}】多余应用",
                AdbPath = adbPath,
                DeviceInfo = device,
            };
            alpsK71V1Window.Owner = this;
            alpsK71V1Window.ShowDialog();
        }

        #endregion

        #region 《拖放安装》

        private bool dragFilevalid = false;
        private bool exceededMax = false;
        private int maxFileCount = 1000;

        private void listBoxItem_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                Globals.FileCounter.CountFiles(files, out exceededMax, maxFileCount);

                List<string> apkList = exceededMax ? new List<string>() : GetAllApkFilesFromDrop(files);
                dragFilevalid = !exceededMax && apkList.Count > 0;
                e.Effects = dragFilevalid ? DragDropEffects.Copy : DragDropEffects.None;

                if (sender is Grid grid)
                {
                    // 半透明深色蒙层提示拖放目标（直接设置卡片背景，
                    // 本地值会覆盖样式中的悬停/选中效果，拖放结束后清除）
                    if (grid.FindName("itemCard") is Border itemCard)
                    {
                        itemCard.Background = new SolidColorBrush(Color.FromArgb(0xa0, 0x1f, 0x29, 0x3b));
                    }
                    if (grid.FindName("dragLabel") is Label dragLabel)
                    {
                        dragLabel.Visibility = Visibility.Visible;
                        dragLabel.Content = exceededMax
                            ? $"文件过多（超过 {maxFileCount} 个）"
                            : dragFilevalid
                                ? $"松手{(apkList.Count <= 1 ? "" : "依次")}安装 {apkList.Count} 个文件"
                                : "未检测到有效的“.apk”文件!";
                        dragLabel.Foreground = dragFilevalid ? Brushes.LightGreen : Brushes.LightCoral;
                        dragLabel.FontWeight = FontWeights.Bold;
                    }
                }
            }
            e.Handled = true;
        }

        private void listBoxItem_DragLeave(object sender, DragEventArgs e)
        {
            if (sender is Grid grid)
            {
                ResetDragOverlay(grid);
            }
            e.Handled = true;
        }

        private void listBoxItem_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !exceededMax && dragFilevalid ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void listBoxItem_Drop(object sender, DragEventArgs e)
        {
            // 先恢复背景和标签
            if (sender is Grid grid)
            {
                ResetDragOverlay(grid);
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop) && !exceededMax
                && ((FrameworkElement)sender).DataContext is Device targetDevice)
            {
                List<string> apkList = GetAllApkFilesFromDrop((string[])e.Data.GetData(DataFormats.FileDrop));
                if (apkList.Count > 0)
                {
                    // 执行安装逻辑
                    HandleApkInstallation(apkList, 0, targetDevice);
                }
            }
            e.Handled = true;
        }

        /// <summary>
        /// 恢复拖放提示：清除卡片背景的本地值（恢复样式悬停/选中效果）并隐藏标签
        /// </summary>
        private static void ResetDragOverlay(Grid grid)
        {
            if (grid.FindName("itemCard") is Border itemCard)
            {
                itemCard.ClearValue(Border.BackgroundProperty);
            }
            if (grid.FindName("dragLabel") is Label dragLabel)
            {
                dragLabel.Visibility = Visibility.Collapsed;
                dragLabel.Content = "";
            }
        }

        /// <summary>
        /// 从拖放的数据中提取所有.apk文件（支持目录递归，无权限目录自动跳过）
        /// </summary>
        private static List<string> GetAllApkFilesFromDrop(string[] files)
        {
            var apkFiles = new List<string>();
            foreach (string path in files)
            {
                if (File.Exists(path))
                {
                    if (Path.GetExtension(path).Equals(".apk", StringComparison.OrdinalIgnoreCase))
                    {
                        apkFiles.Add(path);
                    }
                }
                else if (Directory.Exists(path))
                {
                    // 使用 Directory.EnumerateFiles 递归搜索所有.apk文件
                    try
                    {
                        apkFiles.AddRange(Directory.EnumerateFiles(path, "*.apk", SearchOption.AllDirectories));
                    }
                    catch (Exception)
                    {
                        // 跳过无法访问的目录
                    }
                }
            }
            return apkFiles;
        }

        /// <summary>
        /// 依次安装apk列表（每个文件安装前都会提醒）
        /// </summary>
        private void HandleApkInstallation(List<string> apkList, int index, Device targetDevice)
        {
            if (index >= apkList.Count)
            {
                return;
            }
            string apkFilePath = apkList[index];
            if (MessageBoxResult.OK != MessageBox.Show($"确定要安装应用【{apkFilePath}】到设备【{targetDevice.ShowDeviceName}】吗？", "提示", MessageBoxButton.OKCancel, MessageBoxImage.Question))
            {
                HandleApkInstallation(apkList, index + 1, targetDevice);
                return;
            }
            waitAnimation(true);
            CmdExecutor.ExecuteCommandAndReturnAsync(new[] { $"{adbPath} -s {targetDevice.DeviceMark} install \"{apkFilePath}\"" },
                result =>
                {
                    waitAnimation(false);
                    if (Globals.ContainsAny(result, "Success"))
                    {
                        MessageBox.Show($"设备【{targetDevice.ShowDeviceName}】安装应用成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                    }
                    // 无论成功与否都继续下一个，避免批量安装中断
                    HandleApkInstallation(apkList, index + 1, targetDevice);
                },
                err =>
                {
                    waitAnimation(false);
                    if (Globals.ContainsAny(err, "adb: failed to install", "device offline", "Error: closed"))
                    {
                        MessageBox.Show($"设备【{targetDevice.ShowDeviceName}】安装应用失败！\r\n1.请确认安卓设备是否允许安装应用；\r\n2.应用签名是否不符；\r\n3.检查设备是否已离线。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        if (!Globals.ContainsAny(err, "device offline", "Error: closed"))
                        {
                            HandleApkInstallation(apkList, index + 1, targetDevice);
                        }
                        return true;
                    }
                    return false;
                }, 600);
        }

        #endregion
    }
}
