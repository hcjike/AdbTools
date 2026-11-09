using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Text;

namespace AdbTools
{
    /// <summary>
    /// app_label 表（包名 → 应用显示名）的读写。
    /// 连接、建表与升级由 AppStore 负责，这里只做该表的数据操作。
    /// </summary>
    internal static class AppLabelStore
    {
        /// <summary>SQLite 单条语句参数上限 999，按 500 分批。</summary>
        private const int MaxParametersPerQuery = 500;

        public static string GetLabel(string packageName)
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
                        command.CommandText = "SELECT label FROM app_label WHERE package = @p LIMIT 1;";
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
        public static Dictionary<string, string> GetLabels(IEnumerable<string> packages)
        {
            Dictionary<string, string> labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (packages == null)
            {
                return labels;
            }

            List<string> names = new List<string>();
            foreach (string package in packages)
            {
                if (!string.IsNullOrEmpty(package)) names.Add(package);
            }
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
                            StringBuilder sql = new StringBuilder("SELECT package, label FROM app_label WHERE package IN (");
                            for (int i = 0; i < count; i++)
                            {
                                if (i > 0) sql.Append(',');
                                string name = "@p" + i;
                                sql.Append(name);
                                command.Parameters.AddWithValue(name, names[offset + i]);
                            }
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

        public static void SetLabel(string packageName, string label)
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
                            "INSERT INTO app_label(package, label, updated_at) VALUES(@p, @l, @t) " +
                            "ON CONFLICT(package) DO UPDATE SET label = excluded.label, updated_at = excluded.updated_at;";
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
    }
}
