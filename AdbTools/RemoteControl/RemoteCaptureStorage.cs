using System;
using System.Globalization;
using System.IO;

namespace AdbTools.RemoteControl
{
    /// <summary>
    /// 远程控制窗口的截屏/录屏产物目录：
    /// %APPDATA%\AdbTools\RemoteCaptures\&lt;设备名&gt;\Screenshots|Recordings\
    /// 与设备日志（DeviceLogs）保持同一套组织方式：按设备分目录，再按图片/视频分开放，
    /// 文件名带时间戳；用户可以直接把整个设备目录打包反馈，不必到系统里翻找。
    /// </summary>
    internal static class RemoteCaptureStorage
    {
        /// <summary>配置文件夹下的产物目录名（与 logs / DeviceLogs / cache 并列）。</summary>
        private const string RootFolderName = "RemoteCaptures";

        /// <summary>截图子目录：与录像分开，避免两类文件混在一起不好区分。</summary>
        private const string ScreenshotFolderName = "Screenshots";

        /// <summary>录像子目录（多段录像 _p2/_p3 也在这里）。</summary>
        private const string RecordingFolderName = "Recordings";

        /// <summary>
        /// 取某设备的产物根目录，不存在时创建。deviceName 传设备名称（品牌 + 型号），
        /// 与 DeviceLogs 一致按设备名分目录，不用序列号或连接串。
        /// </summary>
        public static string GetDeviceDirectory(string deviceName)
        {
            return EnsureDirectory(Path.Combine(
                Path.Combine(Common.Globals.AppDataDirectory, RootFolderName), Sanitize(deviceName)));
        }

        /// <summary>截图目录：%APPDATA%\AdbTools\RemoteCaptures\&lt;设备名&gt;\Screenshots。</summary>
        public static string GetScreenshotDirectory(string deviceName)
        {
            return EnsureDirectory(Path.Combine(GetDeviceDirectory(deviceName), ScreenshotFolderName));
        }

        /// <summary>录像目录：%APPDATA%\AdbTools\RemoteCaptures\&lt;设备名&gt;\Recordings。</summary>
        public static string GetRecordingDirectory(string deviceName)
        {
            return EnsureDirectory(Path.Combine(GetDeviceDirectory(deviceName), RecordingFolderName));
        }

        /// <summary>截图文件路径：Screenshots\Shot_yyyyMMdd-HHmmss_宽x高.png，重名时追加序号。</summary>
        public static string BuildScreenshotPath(string deviceName, int width, int height)
        {
            string dir = GetScreenshotDirectory(deviceName);
            string name = "Shot_" + Timestamp() + "_" + width.ToString(CultureInfo.InvariantCulture)
                + "x" + height.ToString(CultureInfo.InvariantCulture);
            return UniquePath(dir, name, ".png");
        }

        /// <summary>录像文件名前缀：Rec_yyyyMMdd-HHmmss（多段时由录制器追加 _p2、_p3）。</summary>
        public static string BuildRecordingBaseName()
        {
            return "Rec_" + Timestamp();
        }

        /// <summary>时间戳格式与设备日志一致的紧凑形式。</summary>
        public static string Timestamp()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        private static string EnsureDirectory(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            catch
            {
                // 目录不可写时由后续文件写入报错，这里不抛异常
            }
            return dir;
        }

        private static string UniquePath(string directory, string name, string extension)
        {
            string path = Path.Combine(directory, name + extension);
            int index = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(directory, name + "_" + index.ToString(CultureInfo.InvariantCulture) + extension);
                index++;
            }
            return path;
        }

        /// <summary>设备名/序列号里可能有冒号（无线调试）等非法字符，统一替换成下划线。</summary>
        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";

            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            value = value.Trim().TrimEnd('.', ' ');
            if (value.Length == 0) return "unknown";
            if (value.Length > 60) value = value.Substring(0, 60).TrimEnd('.', ' ');
            return value.Length == 0 ? "unknown" : value;
        }
    }
}
