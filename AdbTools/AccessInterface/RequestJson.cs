using AdbTools.bean;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace AdbTools.AccessInterface
{

    public delegate void UpdateInfo(GithubReleases githubReleases);

    /// <summary>
    /// 网络请求
    /// </summary>
    public static class RequestJson
    {
        public const string ApiUrl = @"https://api.github.com/repos/hcjike/AdbTools/releases";
        public const string ApiUrlMirror = @"https://code.hcjike.com/api/v1/repos/hcjike/AdbTools/releases";

        // 升级改为下载 Releases 中的安装程序附件（单文件 exe 安装器）
        public const string UpdateFileName = "AdbToolsInstaller.exe";
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        /// <summary>
        /// 检查更新，发现新版本时显示更新图标
        /// </summary>
        public static void UpdateCheck(MainWindow mainWindow, Image updateVersion)
        {
            UpdateCheck(mainWindow, githubReleases =>
            {
                updateVersion.Visibility = Visibility.Visible;
                updateVersion.Tag = githubReleases;
            });
        }

        /// <summary>
        /// 检查更新（异步），发现新版本时在主窗口线程回调action
        /// </summary>
        public static void UpdateCheck(MainWindow mainWindow, UpdateInfo action)
        {
            Thread thread = new Thread(() =>
            {
                GithubReleases githubReleases = Request();
                if (githubReleases == null || IsLatestVersion(githubReleases.tag_name))
                {
                    return;
                }
                if (githubReleases.Assets == null
                    || !githubReleases.Assets.Any(a => UpdateFileName.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    // 没有可供更新的安装包
                    return;
                }

                mainWindow.Dispatcher.Invoke(new Action(() =>
                {
                    action?.Invoke(githubReleases);
                }));
            })
            {
                IsBackground = true
            };
            thread.Start();
        }

        /// <summary>
        /// 判断版本号是否已是当前版本（兼容 “V1.2.3” 与 “1.2.3” 写法）
        /// </summary>
        private static bool IsLatestVersion(string tagName)
        {
            string latest = tagName?.Trim().TrimStart('v', 'V');
            return string.IsNullOrEmpty(latest)
                || App.Version.Equals(latest, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 请求发布列表，返回最新的发布信息（失败返回null）
        /// </summary>
        public static GithubReleases Request()
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ApiUrlMirror);
                request.UserAgent = "User-Agent:Mozilla/5.0 (Windows NT 5.1) AppleWebKit/535.1 (KHTML, like Gecko) Chrome/14.0.835.202 Safari/535.1";
                request.ContentType = "application/x-www-form-urlencoded";
                request.Credentials = CredentialCache.DefaultCredentials;
                request.Method = "GET";
                request.Proxy = null;
                request.Timeout = 60000;

                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    List<GithubReleases> releases = Serializer.Deserialize<List<GithubReleases>>(reader.ReadToEnd());
                    return releases?.OrderByDescending(r => r.Id).FirstOrDefault();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

    }
}
