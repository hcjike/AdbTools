using System;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;

namespace AdbTools.Notify
{
    /// <summary>
    /// Windows 10/11 的 Toast 通知。
    /// 未打包的桌面程序需要进程设置过 AUMID（见 <see cref="App"/> 启动逻辑），
    /// 否则通知可能不显示；发不出去时由调用方回退到托盘气泡。
    /// </summary>
    internal static class ToastService
    {
        /// <summary>系统版本检测只做一次。</summary>
        private static bool? _supported;

        /// <summary>
        /// Toast 在未打包应用里从 Win10 1809（17763）开始才稳定。
        /// 不能直接用 <see cref="Environment.OSVersion"/>：程序清单没有声明 Win10 兼容性时
        /// 它会返回 6.2，所以这里读注册表拿真实版本。
        /// </summary>
        public static bool IsSupported
        {
            get
            {
                if (_supported.HasValue) return _supported.Value;

                bool value = false;
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        int major = 0;
                        int build = 0;

                        object majorValue = key?.GetValue("CurrentMajorVersionNumber");
                        if (majorValue != null) major = Convert.ToInt32(majorValue);

                        object buildValue = key?.GetValue("CurrentBuildNumber");
                        if (buildValue != null) int.TryParse(buildValue.ToString(), out build);

                        // 老系统没有 CurrentMajorVersionNumber，退回 Environment.OSVersion
                        if (major == 0) major = Environment.OSVersion.Version.Major;
                        if (build == 0) build = Environment.OSVersion.Version.Build;

                        value = major >= 10 && build >= 17763;
                    }
                }
                catch
                {
                    value = false;
                }

                _supported = value;
                return value;
            }
        }

        /// <summary>是否已经记录过“Toast 不可用”的日志，只记一次，避免刷日志。</summary>
        private static bool _logged;

        /// <summary>
        /// 写入/修正通知身份：Windows 通知中心里显示的应用名取自该 AUMID 的 DisplayName。
        /// Toolkit 首次注册时用的是进程名（AdbTools.exe → "AdbTools"），这里统一改成中文名；
        /// 注册表写失败只影响显示名，不影响通知本身。
        /// </summary>
        public static void EnsureIdentity()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Classes\AppUserModelId\" + App.AppUserModelId))
                {
                    if (key == null) return;
                    key.SetValue("DisplayName", App.NotificationTitle, RegistryValueKind.String);
                }
            }
            catch
            {
                // 忽略：仅影响通知里的应用名
            }
        }

        /// <summary>
        /// 发一条通知；返回 false 表示调用方应改用气泡提示。
        /// 这里不做任何“要不要发”的判断——是否提醒只由程序的三个通知开关和
        /// Windows 的通知设置决定（系统关闭时 Show 不报错，通知自然不出现）。
        /// 失败也没有粘性：某次异常只影响这一条，下一条仍会重新尝试 Toast。
        /// </summary>
        public static bool TryShow(string title, string body, string tag, string group)
        {
            if (!IsSupported) return false;

            try
            {
                var builder = new ToastContentBuilder();
                builder.AddText(string.IsNullOrWhiteSpace(title) ? App.NotificationTitle : title.Trim());

                if (!string.IsNullOrWhiteSpace(body))
                {
                    builder.AddText(body.Trim());
                }

                // 时长保持系统默认（short，约 5 秒，受“辅助功能 → 显示通知的时长”影响）；
                // 没有用 reminder 场景，所以不需要用户手动关闭，系统会自动收起。
                builder.Show();

                // Toolkit 首次发送时会用进程名注册显示名，这里补一次，保证通知里是中文名
                EnsureIdentity();
                return true;
            }
            catch (Exception ex)
            {
                if (!_logged)
                {
                    _logged = true;
                    AppLog.Write("Toast 通知不可用，改用气泡提示：{0}", ex.Message);
                }
                return false;
            }
        }
    }
}
