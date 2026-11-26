using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AdbTools;

namespace AdbTools.Common
{
    /// <summary>
    /// APK 拖放共用逻辑：按文件数上限扫描并收集 APK、统计重复、生成提示。
    /// 不接触任何窗口控件，提示通过委托交给调用方渲染到自己的 UI。
    /// </summary>
    internal static class ApkDropHelper
    {
        /// <summary>
        /// 拖入内容的缓存键：同一次拖拽里 WPF 会为每个子元素反复触发 DragEnter，
        /// 调用方用它判断「这批路径是不是刚分析过」，避免重复枚举目录。
        /// </summary>
        internal static string BuildCacheKey(string[] files)
        {
            return files == null || files.Length == 0 ? string.Empty : string.Join("\u0001", files);
        }

        /// <summary>拖放分析结果，供窗口决定能否放入及如何呈现。</summary>
        internal sealed class DropResult
        {
            public List<string> ApkFiles { get; set; }
            public int NewCount { get; set; }
            public int DuplicateCount { get; set; }
            public bool ExceededMax { get; set; }
            public bool Valid { get; set; }
            public string HintText { get; set; }
        }

        /// <summary>是否为可安装的 APK 文件（含 xxx.apk.1 等分改装包名）。</summary>
        internal static bool IsApkFile(string path)
        {
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) return false;

            return name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf(".apk.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 从拖放数据中收集 APK，最多枚举 maxCount 个文件；超限时提前停止并告知。
        /// 仅做路径枚举和文件名判断，不打开 APK，开销很低。
        /// </summary>
        internal static List<string> CollectApkFiles(string[] files, int maxCount, out bool exceededMax)
        {
            var apkFiles = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            exceededMax = false;
            if (files == null || files.Length == 0) return apkFiles;

            int count = 0;
            foreach (string path in files)
            {
                if (File.Exists(path))
                {
                    count++;
                    if (IsApkFile(path) && seen.Add(path)) apkFiles.Add(path);
                    if (count >= maxCount) { exceededMax = true; return apkFiles; }
                }
                else if (Directory.Exists(path))
                {
                    var stack = new Stack<string>();
                    stack.Push(path);
                    while (stack.Count > 0)
                    {
                        string current = stack.Pop();
                        try
                        {
                            foreach (string found in Directory.EnumerateFiles(current))
                            {
                                count++;
                                if (IsApkFile(found) && seen.Add(found)) apkFiles.Add(found);
                                if (count >= maxCount) { exceededMax = true; return apkFiles; }
                            }
                            foreach (string sub in Directory.EnumerateDirectories(current))
                            {
                                stack.Push(sub);
                            }
                        }
                        catch (Exception)
                        {
                            // 跳过无法访问的目录
                        }
                    }
                }
            }
            return apkFiles;
        }

        /// <summary>
        /// 分析拖放内容并生成提示。
        /// knownPaths 传 null 表示不统计重复；listLimit &lt;= 0 表示不检查列表已满。
        /// showHint 由窗口提供，负责把提示文本与是否有效渲染到自己的控件。
        /// </summary>
        internal static DropResult Analyze(
            string[] files,
            int fileLimit,
            HashSet<string> knownPaths,
            int currentItemCount,
            int listLimit,
            bool locked,
            string targetName,
            Action<string, bool> showHint)
        {
            var result = new DropResult();

            if (locked)
            {
                result.HintText = "正在安装，暂不能添加 APK";
                showHint?.Invoke(result.HintText, false);
                return result;
            }

            result.ApkFiles = CollectApkFiles(files, fileLimit, out bool exceededMax);
            result.ExceededMax = exceededMax;
            result.DuplicateCount = knownPaths == null
                ? 0
                : result.ApkFiles.Count(p => knownPaths.Contains(p));
            result.NewCount = result.ApkFiles.Count - result.DuplicateCount;

            bool listFull = listLimit > 0 && currentItemCount >= listLimit;
            result.Valid = !listFull && result.NewCount > 0;

            string duplicateHint = result.DuplicateCount > 0 ? "，" + result.DuplicateCount + " 个已添加过" : string.Empty;
            string scannedHint = exceededMax ? "（仅扫描前 " + fileLimit + " 个文件）" : string.Empty;
            result.HintText = listFull
                ? "列表已满（" + listLimit + " 个 APK），请先移除部分"
                : result.NewCount > 0
                    ? "松手添加 " + result.NewCount + " 个 APK 到" + targetName + duplicateHint + scannedHint
                    : result.DuplicateCount > 0
                        ? result.DuplicateCount + " 个 APK 已添加过" + scannedHint
                        : exceededMax
                            ? "仅扫描前 " + fileLimit + " 个文件，未发现 APK"
                            : "未检测到有效的 APK 文件!";

            showHint?.Invoke(result.HintText, result.Valid);
            return result;
        }
    }
}
