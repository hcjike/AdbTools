using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AdbTools
{
    /// <summary>
    /// 程序级异常日志：全局未处理异常在弹框前先写到这里，便于事后排查。
    /// 位置：%APPDATA%\AdbTools\logs\AppException-yyyyMMdd.log
    /// 与其他日志不同，这里同步写盘并立即 flush：异常可能导致进程马上退出，
    /// 不能依赖后台线程慢慢落盘。
    /// </summary>
    internal static class AppExceptionLog
    {
        private static readonly object Sync = new object();

        /// <summary>
        /// 记录一条程序异常。<paramref name="source"/> 说明异常来源
        /// （如「UI 线程未处理异常」），<paramref name="exception"/> 可空。
        /// 写日志本身失败时静默忽略，绝不因此再抛异常。
        /// </summary>
        public static void Write(string source, Exception exception)
        {
            WriteCore(source, exception, null);
        }

        /// <summary>异常对象不是 <see cref="Exception"/> 时，记录它的文本描述。</summary>
        public static void WriteMessage(string source, string detail)
        {
            WriteCore(source, null, detail);
        }

        private static void WriteCore(string source, Exception exception, string detail)
        {
            try
            {
                string text = BuildText(source, exception, detail);
                lock (Sync)
                {
                    CleanupOldLogs();
                    string path = Globals.GetLogFilePath(
                        "AppException-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    using (var writer = new StreamWriter(path, true, Encoding.UTF8))
                    {
                        writer.Write(text);
                        writer.Flush();
                    }
                }
            }
            catch
            {
                // 异常日志失败不能影响原有提示流程
            }
        }

        private static string BuildText(string source, Exception exception, string detail)
        {
            var builder = new StringBuilder();
            builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" [").Append(source ?? "程序异常").Append("] ")
                .Append("线程 ").Append(Thread.CurrentThread.ManagedThreadId)
                .AppendLine();

            if (exception != null)
            {
                // ToString() 已包含类型、消息与完整堆栈
                builder.AppendLine(exception.ToString());
            }
            else if (!string.IsNullOrEmpty(detail))
            {
                builder.AppendLine(detail);
            }
            else
            {
                builder.AppendLine("（无异常对象）");
            }

            builder.AppendLine(new string('-', 60));
            return builder.ToString();
        }

        /// <summary>只保留最近 7 天的异常日志。</summary>
        private static void CleanupOldLogs()
        {
            try
            {
                DateTime limit = DateTime.Now.AddDays(-7);
                foreach (string file in Directory.GetFiles(Globals.LogDirectory, "AppException-*.log"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < limit) File.Delete(file);
                    }
                    catch { }
                }
            }
            catch
            {
                // 清理失败不影响写日志
            }
        }
    }
}
