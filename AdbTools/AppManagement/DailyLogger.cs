using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AdbTools
{
    /// <summary>
    /// 按天分文件的后台日志器：日志入队后由单个线程写入，只有真正有日志时才创建当日文件。
    /// 文件位于 %APPDATA%\AdbTools\logs\&lt;Prefix&gt;-yyyyMMdd.log，仅保留最近 7 份。
    /// </summary>
    internal sealed class DailyLogger
    {
        private const int RetentionFiles = 7;

        private readonly ConcurrentQueue<string> _queue = new ConcurrentQueue<string>();
        private readonly string _prefix;
        private readonly string _threadName;
        private int _workerStarted;

        public DailyLogger(string prefix)
        {
            _prefix = prefix;
            _threadName = prefix + "-log";
        }

        public void Write(string message)
        {
            try
            {
                _queue.Enqueue(DateTime.Now.ToString("HH:mm:ss.fff ") + message);
                if (Interlocked.CompareExchange(ref _workerStarted, 1, 0) == 0)
                {
                    Thread worker = new Thread(Worker)
                    {
                        IsBackground = true,
                        Name = _threadName
                    };
                    worker.Start();
                }
            }
            catch
            {
                // 写日志失败不影响功能
            }
        }

        public void Write(string format, params object[] args)
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

        private void Worker()
        {
            try
            {
                CleanupOldLogs();

                StreamWriter writer = null;
                string currentDate = null;
                DateTime lastFlush = DateTime.UtcNow;

                while (true)
                {
                    string line;
                    if (!_queue.TryDequeue(out line))
                    {
                        Thread.Sleep(20);
                        continue;
                    }

                    string date = DateTime.Now.ToString("yyyyMMdd");
                    if (date != currentDate)
                    {
                        if (writer != null)
                        {
                            try { writer.Flush(); } catch { }
                            try { writer.Dispose(); } catch { }
                            writer = null;
                        }
                        writer = OpenWriter(date);
                        currentDate = date;
                    }

                    if (writer != null)
                    {
                        writer.WriteLine(line);
                        while (_queue.TryDequeue(out line))
                        {
                            writer.WriteLine(line);
                        }
                        DateTime now = DateTime.UtcNow;
                        if ((now - lastFlush).TotalMilliseconds >= 200)
                        {
                            writer.Flush();
                            lastFlush = now;
                        }
                    }
                }
            }
            catch
            {
                // 日志线程异常退出后不再重启，避免反复失败
            }
        }

        private StreamWriter OpenWriter(string date)
        {
            try
            {
                return new StreamWriter(
                    Globals.GetLogFilePath(_prefix + "-" + date + ".log"), true, Encoding.UTF8);
            }
            catch
            {
                // 日志目录不可写时退回程序缓存目录，仍在 %APPDATA%\AdbTools 下
                try
                {
                    return new StreamWriter(
                        Globals.GetCacheFilePath(_prefix + ".log"), true, Encoding.UTF8);
                }
                catch
                {
                    return null;
                }
            }
        }

        private void CleanupOldLogs()
        {
            try
            {
                string[] files = Directory.GetFiles(Globals.LogDirectory, _prefix + "-*.log");
                if (files.Length <= RetentionFiles) return;

                foreach (string file in files
                    .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .Skip(RetentionFiles))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch
            {
                // 清理失败不影响写日志
            }
        }
    }
}
