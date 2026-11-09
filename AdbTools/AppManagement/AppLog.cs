using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace AdbTools
{
    /// <summary>
    /// 应用列表的日志：先入队，由单个后台线程按天落盘，
    /// 解析线程与命令回调都不会因为写日志而阻塞。
    /// 位置：%APPDATA%\AdbTools\logs\AppList-yyyyMMdd.log
    /// </summary>
    internal static class AppLog
    {
        private static readonly ConcurrentQueue<string> Queue = new ConcurrentQueue<string>();
        private static int _workerStarted;

        public static void Write(string message)
        {
            try
            {
                Queue.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff ") + message);
                if (Interlocked.CompareExchange(ref _workerStarted, 1, 0) == 0)
                {
                    Thread worker = new Thread(Worker)
                    {
                        IsBackground = true,
                        Name = "app-list-log"
                    };
                    worker.Start();
                }
            }
            catch
            {
                // 写日志失败不影响功能
            }
        }

        public static void Write(string format, params object[] args)
        {
            if (args == null || args.Length == 0)
            {
                Write(format);
                return;
            }

            try
            {
                Write(string.Format(format, args));
            }
            catch
            {
                // 格式化出错时忽略这条日志
            }
        }

        /// <summary>只保留最近 7 天的应用列表日志，避免日志目录无限增长。</summary>
        private static void CleanupOldLogs()
        {
            try
            {
                string dir = Globals.LogDirectory;
                DateTime limit = DateTime.Now.AddDays(-7);
                foreach (string file in Directory.GetFiles(dir, "AppList-*.log"))
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

        private static void Worker()
        {
            try
            {
                CleanupOldLogs();

                StreamWriter writer = null;
                string currentDate = null;
                DateTime lastFlush = DateTime.UtcNow;

                while (true)
                {
                    string date = DateTime.Now.ToString("yyyyMMdd");
                    if (date != currentDate)
                    {
                        if (writer != null)
                        {
                            try { writer.Dispose(); } catch { }
                        }

                        try
                        {
                            writer = new StreamWriter(
                                Globals.GetLogFilePath("AppList-" + date + ".log"), true, Encoding.UTF8);
                        }
                        catch
                        {
                            // 日志目录不可写时退回程序自己的缓存目录（仍在 %APPDATA%\AdbTools 下，启动时会被清理），
                            // 绝不写到系统临时目录
                            writer = new StreamWriter(
                                Globals.GetCacheFilePath("AppList.log"), true, Encoding.UTF8);
                        }
                        currentDate = date;
                    }

                    bool wrote = false;
                    string line;
                    while (Queue.TryDequeue(out line))
                    {
                        writer.WriteLine(line);
                        wrote = true;
                    }

                    DateTime now = DateTime.UtcNow;
                    if (wrote && (now - lastFlush).TotalMilliseconds >= 200)
                    {
                        writer.Flush();
                        lastFlush = now;
                    }

                    Thread.Sleep(20);
                }
            }
            catch
            {
                // 日志线程异常退出后不再重启，避免反复失败
            }
        }
    }
}
