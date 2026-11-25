using System;
using System.IO;
using System.IO.Compression;

namespace AdbTools
{
    /// <summary>
    /// 安装前在本机读取 APK 基本信息（应用名、包名），不经过设备、不联网。
    /// 只解出 AndroidManifest.xml 与 resources.arsc 两个条目，正常 APK 在几十毫秒内完成。
    /// 任何环节失败都返回 null，由调用方回退到文件名。
    /// </summary>
    internal static class ApkInfoReader
    {
        /// <summary>manifest 上限：正常 1MB 以内，超过视为异常文件直接放弃。</summary>
        private const long MaxManifestBytes = 4 * 1024 * 1024;

        /// <summary>资源表上限：超过则不解析名称，避免极端文件拖慢弹窗。</summary>
        private const long MaxArscBytes = 48 * 1024 * 1024;

        /// <summary>读取应用显示名、包名与最低 SDK 版本；取不到时字符串为 null、SDK 为 0。</summary>
        public static void Read(string apkPath, out string label, out string packageName, out int minSdkVersion)
        {
            string versionName;
            Read(apkPath, out label, out packageName, out minSdkVersion, out versionName);
        }

        /// <summary>读取应用显示名与包名；取不到时对应值为 null。</summary>
        public static void Read(string apkPath, out string label, out string packageName)
        {
            int minSdkVersion;
            string versionName;
            Read(apkPath, out label, out packageName, out minSdkVersion, out versionName);
        }

        /// <summary>读取应用显示名、包名、最低 SDK 与版本名；取不到时字符串为 null、SDK 为 0。</summary>
        public static void Read(string apkPath, out string label, out string packageName,
            out int minSdkVersion, out string versionName)
        {
            label = null;
            packageName = null;
            minSdkVersion = 0;
            versionName = null;

            try
            {
                using (ZipArchive zip = ZipFile.OpenRead(apkPath))
                {
                    byte[] manifest = ReadEntry(zip, "AndroidManifest.xml", MaxManifestBytes);
                    if (manifest == null)
                    {
                        return;
                    }

                    byte[] arsc = ReadEntry(zip, "resources.arsc", MaxArscBytes);

                    packageName = ApkLabelReader.ReadPackageName(manifest);
                    minSdkVersion = ApkLabelReader.ReadMinSdkVersion(manifest);
                    versionName = ApkLabelReader.ReadVersionName(manifest, arsc);
                    label = ApkLabelReader.ReadLabel(manifest, arsc);
                }
            }
            catch (Exception)
            {
                // 损坏、被加密或非标准打包：保持默认值，弹窗只显示文件信息
            }
        }

        private static byte[] ReadEntry(ZipArchive zip, string entryName, long maxBytes)
        {
            ZipArchiveEntry entry = zip.GetEntry(entryName);
            if (entry == null || entry.Length > maxBytes)
            {
                return null;
            }

            using (Stream stream = entry.Open())
            using (MemoryStream buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }
    }
}
