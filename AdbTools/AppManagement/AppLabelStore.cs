using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Text;

namespace AdbTools
{
    /// <summary>
    /// app_label 表（设备 + 包名 → 展示信息）的读写。
    /// 一个包在不同设备上可能名称/图标/服务判定都不同，所以所有查询都带 device_key。
    /// 名称、名称是否已定论、系统服务判定、图标哈希、图标失败标记都放在同一行：
    /// 它们的粒度都是（设备, 包名），拆表只会多一次 JOIN。
    /// </summary>
    internal static class AppLabelStore
    {
        /// <summary>SQLite 单条语句参数上限 999，留出 device_key 后按 400 分批。</summary>
        private const int MaxParametersPerQuery = 400;

        public static string GetLabel(string deviceKey, string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
            {
                return null;
            }

            try
            {
                using (SQLiteConnection connection = AppStore.OpenConnection())
                {
                    if (connection == null) return null;

                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT label FROM app_label WHERE device_key = @d AND package = @p " +
                            "AND label IS NOT NULL LIMIT 1;";
                        command.Parameters.AddWithValue("@d", Key(deviceKey));
                        command.Parameters.AddWithValue("@p", packageName);
                        return command.ExecuteScalar() as string;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("读取应用名失败：{0}", ex.Message);
                return null;
            }
        }

        /// <summary>批量取名称：列表一次加载几百个包，用 IN 查询避免逐条往返。</summary>
        public static Dictionary<string, string> GetLabels(string deviceKey, IEnumerable<string> packages)
        {
            Dictionary<string, string> labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (packages == null)
            {
                return labels;
            }

            List<string> names = Collect(packages);
            if (names.Count == 0)
            {
                return labels;
            }

            try
            {
                using (SQLiteConnection connection = AppStore.OpenConnection())
                {
                    if (connection == null) return labels;

                    for (int offset = 0; offset < names.Count; offset += MaxParametersPerQuery)
                    {
                        int count = Math.Min(MaxParametersPerQuery, names.Count - offset);
                        using (SQLiteCommand command = connection.CreateCommand())
                        {
                            StringBuilder sql = new StringBuilder(
                                "SELECT package, label FROM app_label " +
                                "WHERE device_key = @d AND label IS NOT NULL AND package IN (");
                            command.Parameters.AddWithValue("@d", Key(deviceKey));
                            AppendPackages(sql, command, names, offset, count);
                            sql.Append(");");
                            command.CommandText = sql.ToString();

                            using (SQLiteDataReader reader = command.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    labels[reader.GetString(0)] = reader.GetString(1);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("批量读取应用名失败：{0}", ex.Message);
            }

            return labels;
        }

        public static void SetLabel(string deviceKey, string packageName, string label)
        {
            if (string.IsNullOrEmpty(packageName) || string.IsNullOrWhiteSpace(label))
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
                            "INSERT INTO app_label(device_key, package, label, label_resolved, updated_at) " +
                            "VALUES(@d, @p, @l, 1, @t) " +
                            "ON CONFLICT(device_key, package) DO UPDATE SET " +
                            "label = excluded.label, label_resolved = 1, updated_at = excluded.updated_at;";
                        command.Parameters.AddWithValue("@d", Key(deviceKey));
                        command.Parameters.AddWithValue("@p", packageName);
                        command.Parameters.AddWithValue("@l", label.Trim());
                        command.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("写入应用名失败：{0}", ex.Message);
            }
        }

        /// <summary>批量取系统服务标记：只返回已判定的包（NULL 表示还没判定，不出现）。</summary>
        public static Dictionary<string, bool> GetServices(string deviceKey, IEnumerable<string> packages)
        {
            Dictionary<string, bool> services = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (packages == null)
            {
                return services;
            }

            List<string> names = Collect(packages);
            if (names.Count == 0)
            {
                return services;
            }

            try
            {
                using (SQLiteConnection connection = AppStore.OpenConnection())
                {
                    if (connection == null) return services;

                    for (int offset = 0; offset < names.Count; offset += MaxParametersPerQuery)
                    {
                        int count = Math.Min(MaxParametersPerQuery, names.Count - offset);
                        using (SQLiteCommand command = connection.CreateCommand())
                        {
                            StringBuilder sql = new StringBuilder(
                                "SELECT package, is_service FROM app_label " +
                                "WHERE device_key = @d AND is_service IS NOT NULL AND package IN (");
                            command.Parameters.AddWithValue("@d", Key(deviceKey));
                            AppendPackages(sql, command, names, offset, count);
                            sql.Append(");");
                            command.CommandText = sql.ToString();

                            using (SQLiteDataReader reader = command.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    services[reader.GetString(0)] = reader.GetInt64(1) != 0;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("批量读取系统服务标记失败：{0}", ex.Message);
            }

            return services;
        }

        /// <summary>写入系统服务标记；行不存在时其余列取默认值。</summary>
        public static void SetService(string deviceKey, string packageName, bool isService)
        {
            if (string.IsNullOrEmpty(packageName))
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
                            "INSERT INTO app_label(device_key, package, is_service, updated_at) VALUES(@d, @p, @s, @t) " +
                            "ON CONFLICT(device_key, package) DO UPDATE SET " +
                            "is_service = excluded.is_service, updated_at = excluded.updated_at;";
                        command.Parameters.AddWithValue("@d", Key(deviceKey));
                        command.Parameters.AddWithValue("@p", packageName);
                        command.Parameters.AddWithValue("@s", isService ? 1 : 0);
                        command.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("写入系统服务标记失败：{0}", ex.Message);
            }
        }

        /// <summary>标记"确定无名称"：资源齐全仍读不到名字的系统包，落空标记避免下次再拉 arsc。</summary>
        public static void SetLabelResolved(string deviceKey, string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
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
                            "INSERT INTO app_label(device_key, package, label_resolved, updated_at) VALUES(@d, @p, 1, @t) " +
                            "ON CONFLICT(device_key, package) DO UPDATE SET " +
                            "label_resolved = 1, updated_at = excluded.updated_at;";
                        command.Parameters.AddWithValue("@d", Key(deviceKey));
                        command.Parameters.AddWithValue("@p", packageName);
                        command.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("写入无名称标记失败：{0}", ex.Message);
            }
        }

        /// <summary>批量取"名称已解析（含确定无名称）"的包名集合，用于跳过重复解析。</summary>
        public static HashSet<string> GetResolvedNames(string deviceKey, IEnumerable<string> packages)
        {
            HashSet<string> resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (packages == null)
            {
                return resolved;
            }

            List<string> names = Collect(packages);
            if (names.Count == 0)
            {
                return resolved;
            }

            try
            {
                using (SQLiteConnection connection = AppStore.OpenConnection())
                {
                    if (connection == null) return resolved;

                    for (int offset = 0; offset < names.Count; offset += MaxParametersPerQuery)
                    {
                        int count = Math.Min(MaxParametersPerQuery, names.Count - offset);
                        using (SQLiteCommand command = connection.CreateCommand())
                        {
                            StringBuilder sql = new StringBuilder(
                                "SELECT package FROM app_label " +
                                "WHERE device_key = @d AND label_resolved = 1 AND package IN (");
                            command.Parameters.AddWithValue("@d", Key(deviceKey));
                            AppendPackages(sql, command, names, offset, count);
                            sql.Append(");");
                            command.CommandText = sql.ToString();

                            using (SQLiteDataReader reader = command.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    resolved.Add(reader.GetString(0));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("批量读取名称解析状态失败：{0}", ex.Message);
            }

            return resolved;
        }

        /// <summary>该设备上这个包的图标是否已尝试拉取且失败（避免每次重试）。</summary>
        public static bool IsIconFailed(string deviceKey, string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
            {
                return false;
            }

            try
            {
                using (SQLiteConnection connection = AppStore.OpenConnection())
                {
                    if (connection == null) return false;

                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "SELECT icon_failed FROM app_label WHERE device_key = @d AND package = @p LIMIT 1;";
                        command.Parameters.AddWithValue("@d", Key(deviceKey));
                        command.Parameters.AddWithValue("@p", packageName);
                        object value = command.ExecuteScalar();
                        return value != null && Convert.ToInt64(value) != 0;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("读取图标失败标记失败：{0}", ex.Message);
                return false;
            }
        }

        /// <summary>记录"图标确定拿不到"：无图标声明、路径解析不到或矢量无法渲染。</summary>
        public static void SetIconFailed(string deviceKey, string packageName)
        {
            if (string.IsNullOrEmpty(packageName))
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
                            "INSERT INTO app_label(device_key, package, icon_failed, updated_at) VALUES(@d, @p, 1, @t) " +
                            "ON CONFLICT(device_key, package) DO UPDATE SET " +
                            "icon_failed = 1, updated_at = excluded.updated_at;";
                        command.Parameters.AddWithValue("@d", Key(deviceKey));
                        command.Parameters.AddWithValue("@p", packageName);
                        command.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("写入图标失败标记失败：{0}", ex.Message);
            }
        }

        private static List<string> Collect(IEnumerable<string> packages)
        {
            List<string> names = new List<string>();
            foreach (string package in packages)
            {
                if (!string.IsNullOrEmpty(package)) names.Add(package);
            }
            return names;
        }

        private static void AppendPackages(StringBuilder sql, SQLiteCommand command,
            List<string> names, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sql.Append(',');
                string name = "@p" + i;
                sql.Append(name);
                command.Parameters.AddWithValue(name, names[offset + i]);
            }
        }

        private static string Key(string deviceKey)
        {
            return string.IsNullOrWhiteSpace(deviceKey) ? AppDeviceKey.Unknown : deviceKey;
        }
    }
}
