using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows;

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
        public static void ExecuteCommandAndReturnAsync(string[] commands, ExecuteResult executeResult, ExecuteErrResult errorResult, int timeout = 60)
        {
            new Thread(() =>
            {
                string ret = string.Empty;
                string err = string.Empty;
                try
                {
                    using (Process process = new Process())
                    {
                        process.StartInfo.FileName = "cmd.exe";
                        process.StartInfo.RedirectStandardInput = true;
                        process.StartInfo.RedirectStandardOutput = true;
                        process.StartInfo.RedirectStandardError = true;
                        process.StartInfo.UseShellExecute = false;
                        process.StartInfo.CreateNoWindow = true;

                        process.Start();

                        foreach (string command in commands)
                        {
                            process.StandardInput.WriteLine(command);
                            Thread.Sleep(COMMAND_INTERVAL_MS);
                        }

                        process.StandardInput.WriteLine("exit");
                        process.StandardInput.Close();

                        // 先等待进程结束（超时则强杀），再读取输出，
                        // 避免命令挂起时 ReadToEnd 永久阻塞导致加载动画无法结束
                        if (!process.WaitForExit(timeout * 1000))
                        {
                            process.Kill();
                        }
                        ret = process.StandardOutput.ReadToEnd();
                        err = process.StandardError.ReadToEnd();
                    }
                }
                catch (Exception ex)
                {
                    err = $"命令执行异常：{ex.Message}";
                }

                Application.Current.Dispatcher.Invoke(new Action(() =>
                {
                    // 错误回调返回true表示已处理，不再触发成功回调
                    if (!string.IsNullOrWhiteSpace(err) && errorResult?.Invoke(err) == true)
                    {
                        return;
                    }
                    executeResult?.Invoke(ret);
                }));
            }).Start();
        }

        /// <summary>
        /// 通过系统shell执行命令（会显示命令行窗口）
        /// </summary>
        public static void ExecuteCommandByShell(string command)
        {
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
