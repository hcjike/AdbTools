namespace AdbTools
{
    /// <summary>
    /// 应用列表日志：%APPDATA%\AdbTools\logs\AppList-yyyyMMdd.log。
    /// 只有产生日志时才创建当日文件，保留最近 7 份。
    /// </summary>
    internal static class AppLog
    {
        private static readonly DailyLogger Logger = new DailyLogger("AppList");

        public static void Write(string message)
        {
            Logger.Write(message);
        }

        public static void Write(string format, params object[] args)
        {
            Logger.Write(format, args);
        }
    }
}
