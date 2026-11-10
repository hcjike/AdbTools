using System;
using System.Text;

namespace AdbTools
{
    /// <summary>
    /// 应用缓存（名称、图标、服务判定）按设备隔离时使用的设备键。
    /// 取值顺序：ro.serialno → ro.boot.serialno → android_id → 当前连接序列号。
    /// 前三个由设备自身提供，同一台手机用 USB / IP / 无线调试连接时结果相同，
    /// 因此多种连接方式能共用一份缓存；都取不到时才退回连接序列号（此时各连接会各存一份）。
    /// </summary>
    internal static class AppDeviceKey
    {
        public const string Unknown = "unknown";

        /// <summary>依次尝试的取值命令，前一个不可用才执行下一个。</summary>
        private static readonly string[] KeyCommands =
        {
            "getprop ro.serialno",
            "getprop ro.boot.serialno",
            "settings get secure android_id"
        };

        /// <summary>异步取设备键，回调在 UI 线程；任何失败都用 fallback 兜底。</summary>
        public static void ResolveAsync(string adbPath, string serialArg, string fallback, Action<string> done)
        {
            if (done == null)
            {
                return;
            }

            string fallbackKey = Normalize(fallback);
            if (string.IsNullOrWhiteSpace(serialArg))
            {
                done(fallbackKey);
                return;
            }

            string adb = string.IsNullOrWhiteSpace(adbPath) ? "adb" : adbPath.Trim();
            TryNext(adb, serialArg, 0, done, fallbackKey);
        }

        private static void TryNext(string adb, string serialArg, int index, Action<string> done, string fallbackKey)
        {
            if (index >= KeyCommands.Length)
            {
                // 记一条日志：这种情况缓存会按连接方式分裂，排查时能对上
                AppLog.Write("未能读取设备硬件序列号，缓存按连接序列号区分：{0}", fallbackKey);
                done(fallbackKey);
                return;
            }

            CmdExecutor.ExecuteCommandAndReturnAsync(
                new[] { adb + " -s " + serialArg + " shell " + KeyCommands[index] },
                result =>
                {
                    string value = ParseValue(result);
                    if (IsUsable(value))
                    {
                        done(Normalize(value));
                    }
                    else
                    {
                        TryNext(adb, serialArg, index + 1, done, fallbackKey);
                    }
                },
                err =>
                {
                    // 命令不支持（老系统没有 settings 等）同样往下试
                    TryNext(adb, serialArg, index + 1, done, fallbackKey);
                    return true;
                });
        }

        /// <summary>只保留字母、数字与 - _ .，限制长度，避免异常字符进入 SQL 参数与日志。</summary>
        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Unknown;
            }

            StringBuilder builder = new StringBuilder(64);
            foreach (char c in value.Trim())
            {
                if (builder.Length >= 64) break;
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                {
                    builder.Append(c);
                }
            }

            return builder.Length == 0 ? Unknown : builder.ToString();
        }

        /// <summary>值是否可用：排除空值、占位值（unknown/null）与全 0。</summary>
        private static bool IsUsable(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string text = value.Trim();
            if (text.Length < 4 || text.Length > 64)
            {
                return false;
            }

            string lower = text.ToLowerInvariant();
            if (lower == "unknown" || lower == "null" || lower == "none" || lower == "n/a" || lower == "0")
            {
                return false;
            }

            // adb 偶发把错误写到 stdout（如 "error: device offline"），不能当成设备键
            if (lower.Contains("error") || lower.Contains("offline")
                || lower.Contains("unauthorized") || lower.Contains("failure"))
            {
                return false;
            }

            // 个别设备返回 00000000000000 之类的占位值
            foreach (char c in text)
            {
                if (c != '0' && c != '-' && c != ':' && c != '_')
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>从 cmd 回显里取出真正的值：跳过命令回显、提示符与空行，取最后一个有效行。</summary>
        private static string ParseValue(string output)
        {
            if (string.IsNullOrEmpty(output))
            {
                return null;
            }

            string value = null;
            foreach (string raw in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (LooksLikeEcho(line)) continue;
                value = line;
            }

            return value;
        }

        /// <summary>cmd 会把命令本身也回显出来，判断一行是否属于回显/提示符。</summary>
        private static bool LooksLikeEcho(string line)
        {
            if (line.EndsWith(">", StringComparison.Ordinal))
            {
                return true;
            }

            return line.IndexOf("getprop", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("settings get", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
