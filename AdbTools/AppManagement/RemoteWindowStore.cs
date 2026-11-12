using System;
using System.Data.SQLite;

namespace AdbTools
{
    /// <summary>
    /// 远程控制窗口的用户尺寸（横屏、竖屏各一份），按设备键保存。
    /// 只记录用户拖出来的大小，不记录位置：打开时按屏幕居中显示，
    /// 尺寸也限制在工作区内，避免窗口跑到屏幕外。
    /// 设备键与应用缓存一致，同一台手机 USB / IP / 无线连接共用一套。
    /// </summary>
    internal static class RemoteWindowStore
    {
        /// <summary>保存某个方向的窗口尺寸，另一个方向保持不变。</summary>
        public static void SaveSize(string deviceKey, bool landscape, double width, double height)
        {
            if (string.IsNullOrEmpty(deviceKey) || width <= 0 || height <= 0)
            {
                return;
            }

            try
            {
                using (SQLiteConnection connection = AppStore.OpenConnection())
                {
                    if (connection == null) return;

                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        string insertColumns = landscape
                            ? "landscape_width, landscape_height"
                            : "portrait_width, portrait_height";
                        string updateColumns = landscape
                            ? "landscape_width = @w, landscape_height = @h"
                            : "portrait_width = @w, portrait_height = @h";

                        // 只更新当前方向：另一个方向可能还是 NULL（没调整过），不能一起覆盖
                        command.CommandText =
                            "INSERT INTO remote_window(device_key, " + insertColumns + ", updated_at) " +
                            "VALUES(@d, @w, @h, @t) " +
                            "ON CONFLICT(device_key) DO UPDATE SET " + updateColumns +
                            ", updated_at = @t;";
                        command.Parameters.AddWithValue("@d", AppDeviceKey.Normalize(deviceKey));
                        command.Parameters.AddWithValue("@w", width);
                        command.Parameters.AddWithValue("@h", height);
                        command.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("保存远程控制窗口尺寸失败：{0}", ex.Message);
            }
        }

        /// <summary>读取该设备保存的尺寸；对应方向没有记录时该组数值为 0。</summary>
        public static void Load(string deviceKey, out double landscapeWidth, out double landscapeHeight,
            out double portraitWidth, out double portraitHeight)
        {
            landscapeWidth = 0;
            landscapeHeight = 0;
            portraitWidth = 0;
            portraitHeight = 0;

            if (string.IsNullOrEmpty(deviceKey))
            {
                return;
            }

            try
            {
                using (SQLiteConnection connection = AppStore.OpenConnection())
                {
                    if (connection == null) return;

                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT landscape_width, landscape_height, portrait_width, portrait_height " +
                            "FROM remote_window WHERE device_key = @d LIMIT 1;";
                        command.Parameters.AddWithValue("@d", AppDeviceKey.Normalize(deviceKey));

                        using (SQLiteDataReader reader = command.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                return;
                            }

                            landscapeWidth = ReadDouble(reader, 0);
                            landscapeHeight = ReadDouble(reader, 1);
                            portraitWidth = ReadDouble(reader, 2);
                            portraitHeight = ReadDouble(reader, 3);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("读取远程控制窗口尺寸失败：{0}", ex.Message);
            }
        }

        private static double ReadDouble(SQLiteDataReader reader, int index)
        {
            return reader.IsDBNull(index) ? 0 : reader.GetDouble(index);
        }
    }
}
