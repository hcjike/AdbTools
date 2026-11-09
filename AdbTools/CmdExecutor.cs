using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AdbTools
{
    public delegate void ExecuteResult(string result);
    public delegate bool ExecuteErrResult(string err);

    public class CmdExecutor
    {
        /// <summary>
        /// 连续写入多条命令时的间隔，确保前一条命令已启动后再喂入下一条输入
        /// （如 adb pair 需先进入配对码输入状态）
        /// </summary>
        private const int COMMAND_INTERVAL_MS = 50;

        /// <summary>默认超时时间（秒）。adb 冷启动、WiFi 唤醒与重连都可能耗时数秒，仅作兜底。</summary>
        private const int DefaultTimeoutSeconds = 60;

        /// <summary>单次命令最多保留的输出字符数，防止异常命令（如误用 logcat）把内存吃满。</summary>
        private const int MaxOutputChars = 8 * 1024 * 1024;

        /// <summary>结束进程树/等待输出时的兜底等待时间。</summary>
        private const int KillWaitMs = 3000;

        public static void ExecuteCommandAndReturnAsync(string command)
        {
            ExecuteCommandAndReturnAsync(new[] { command }, null, null);
        }

        public static void ExecuteCommandAndReturnAsync(string command, ExecuteResult executeResult)
        {
            ExecuteCommandAndReturnAsync(new[] { command }, executeResult, null);
        }

        public static void ExecuteCommandAndReturnAsync(string command, ExecuteResult executeResult, ExecuteErrResult errorResult)
        {
            ExecuteCommandAndReturnAsync(new[] { command }, executeResult, errorResult);
        }

        /// <summary>
        /// 异步执行命令并通过回调返回结果（回调在UI线程执行）
        /// </summary>
        /// <param name="commands">命令列表，逐行写入cmd执行</param>
        /// <param name="executeResult">成功回调</param>
        /// <param name="errorResult">错误回调，返回true表示已自行处理，不再触发成功回调</param>
        /// <param name="timeout">超时时间（秒），超时后强制结束进程。
        /// 注意：adb冷启动、WiFi设备唤醒与重连均可能耗时数秒，默认值需留足余量，仅用于兜底防止无限挂起</param>
        public static void ExecuteCommandAndReturnAsync(string[] commands, ExecuteResult executeResult,
            ExecuteErrResult errorResult, int timeout = DefaultTimeoutSeconds)
        {
            if (commands == null || commands.Length == 0)
            {
                NotifyUi(executeResult, string.Empty);
                return;
            }
            if (timeout <= 0) timeout = DefaultTimeoutSeconds;

            // 命令最长可能阻塞到超时，用后台线程：既不会拖住进程退出，也不占用线程池
            var worker = new Thread(() => RunCommands(commands, executeResult, errorResult, timeout))
            {
                IsBackground = true,
                Name = "cmd-executor"
            };
            worker.Start();
        }

        /// <summary>在后台线程执行命令并回传结果。</summary>
        private static void RunCommands(string[] commands, ExecuteResult executeResult,
            ExecuteErrResult errorResult, int timeout)
        {
            string ret = string.Empty;
            string err = string.Empty;
            bool timedOut = false;

            EnsureStdinEncodingWithoutBom();

            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    // 边执行边异步读取输出：
                    // 先 WaitForExit 再 ReadToEnd 会在输出超过管道缓冲区（约 4KB）时死锁，
                    // 例如 pm list packages / dumpsys 这类输出较大的命令会一直卡到超时。
                    var stdout = new StringBuilder();
                    var stderr = new StringBuilder();
                    process.OutputDataReceived += (s, e) => AppendLine(stdout, e.Data);
                    process.ErrorDataReceived += (s, e) => AppendLine(stderr, e.Data);

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    try
                    {
                        bool wroteAny = false;
                        foreach (string command in commands)
                        {
                            if (string.IsNullOrWhiteSpace(command)) continue;

                            // 只在多条命令之间等待（如 adb pair 需要先进入配对码输入状态）；
                            // 单条命令与最后一条后面不再 sleep，省掉固定的 50ms 延迟
                            if (wroteAny) Thread.Sleep(COMMAND_INTERVAL_MS);
                            process.StandardInput.WriteLine(command);
                            wroteAny = true;
                        }

                        process.StandardInput.WriteLine("exit");
                        // 写完 exit 立刻关闭输入流：cmd 读到 EOF 后会退出，
                        // 单条命令调用依赖这一点结束进程，不要改成只依赖 exit。
                        process.StandardInput.Close();
                    }
                    catch (Exception)
                    {
                        // 命令自身提前退出（例如命令里带了 exit）时写入会失败，不影响结果读取
                    }

                    if (!process.WaitForExit(timeout * 1000))
                    {
                        timedOut = true;
                        KillProcessTree(process);
                    }

                    WaitForOutputDrain(process);

                    ret = stdout.ToString();
                    err = stderr.ToString();
                }
            }
            catch (Exception ex)
            {
                err = "命令执行异常：" + ex.Message;
            }

            if (timedOut)
            {
                // 超时会让调用方拿到半截输出，必须显式告知，避免被当成成功
                string timeoutMessage = "命令执行超时（" + timeout + " 秒），已终止。";
                err = string.IsNullOrWhiteSpace(err)
                    ? timeoutMessage
                    : err.TrimEnd() + Environment.NewLine + timeoutMessage;
            }

            NotifyUi(executeResult, errorResult, ret, err);
        }

        /// <summary>
        /// cmd.exe 从管道读取命令时，如果标准输入编码带 BOM，第一条命令会被 BOM 污染，
        /// 表现为"系统找不到指定的路径"（实际是 "\uFEFF\"adb.exe\" ..."）。
        /// 控制台输入编码为 UTF-8 时改成不带 BOM 的默认编码；GUI 进程没有控制台，改动会失败，忽略即可。
        /// </summary>
        private static void EnsureStdinEncodingWithoutBom()
        {
            try
            {
                if (Console.InputEncoding.GetPreamble().Length > 0)
                {
                    Console.InputEncoding = Encoding.Default;
                }
            }
            catch
            {
                // 无控制台或不允许修改时忽略，后续写入保持原行为
            }
        }

        /// <summary>追加一行输出，超过上限后停止累积，避免异常命令把内存吃满。</summary>
        private static void AppendLine(StringBuilder builder, string line)
        {
            if (line == null) return;
            if (builder.Length >= MaxOutputChars) return;
            builder.AppendLine(line);
        }

        /// <summary>
        /// 进程结束后再等一次，确保 OutputDataReceived 把管道里的剩余数据读完；
        /// 直接取 StringBuilder 可能拿到半截输出。
        /// </summary>
        private static void WaitForOutputDrain(Process process)
        {
            try
            {
                if (process.HasExited)
                {
                    // 无参 WaitForExit 会等待异步输出事件全部处理完
                    process.WaitForExit();
                }
                else
                {
                    process.WaitForExit(KillWaitMs);
                }
            }
            catch
            {
                // 进程句柄已释放等异常，忽略
            }
        }

        /// <summary>
        /// 结束进程树：adb.exe 是 cmd.exe 的子进程，只 Kill 父进程会留下孤儿 adb 进程。
        /// 优先用 taskkill /T 一并结束；失败时退回 Kill。
        /// </summary>
        private static void KillProcessTree(Process process)
        {
            try
            {
                if (process.HasExited) return;
            }
            catch
            {
                return;
            }

            try
            {
                var psi = new ProcessStartInfo("taskkill", "/T /F /PID " + process.Id)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process killer = Process.Start(psi))
                {
                    if (killer != null) killer.WaitForExit(KillWaitMs);
                }
            }
            catch
            {
                try { process.Kill(); } catch { }
            }
        }

        /// <summary>把回调切回 UI 线程；应用正在退出时直接丢弃，避免抛异常。</summary>
        private static void NotifyUi(ExecuteResult executeResult, string result)
        {
            NotifyUi(executeResult, null, result, string.Empty);
        }

        private static void NotifyUi(ExecuteResult executeResult, ExecuteErrResult errorResult,
            string result, string err)
        {
            Action callback = () =>
            {
                // 错误回调返回 true 表示已自行处理，不再触发成功回调
                if (!string.IsNullOrWhiteSpace(err) && errorResult != null && errorResult(err))
                {
                    return;
                }
                if (executeResult != null) executeResult(result);
            };

            Application app = Application.Current;
            if (app == null) return;

            Dispatcher dispatcher = app.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

            try
            {
                // BeginInvoke：调用线程不等待 UI，退出/阻塞时不会互相死等
                dispatcher.BeginInvoke(callback);
            }
            catch (Exception)
            {
                // 应用正在退出，忽略
            }
        }

        /// <summary>
        /// 通过系统shell执行命令（会显示命令行窗口）
        /// </summary>
        public static void ExecuteCommandByShell(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return;

            try
            {
                using (Process process = new Process())
                {
                    process.StartInfo.FileName = "cmd.exe";
                    process.StartInfo.UseShellExecute = true;
                    process.StartInfo.CreateNoWindow = false;
                    process.StartInfo.Arguments = $"/c \"{command}\"";
                    process.Start();
                }
            }
            catch (Exception)
            {
                MessageBox.Show("启动命令行窗口失败！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public static bool StartExe(string exePath)
        {
            return StartExe(exePath, new List<string>());
        }

        /// <summary>
        /// 启动外部程序（支持URL，将调用默认浏览器打开）
        /// </summary>
        public static bool StartExe(string exePath, List<string> cmdArges)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = cmdArges == null || cmdArges.Count == 0 ? "" : string.Join(" ", cmdArges),
                UseShellExecute = true
            };

            try
            {
                Process.Start(startInfo);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
