using System;
using System.Data.SQLite;
using System.IO;

namespace AdbTools
{
    /// <summary>
    /// 应用数据数据库的基础设施：库文件位置、连接、建库与版本升级。
    /// 库文件：%APPDATA%\AdbTools\data\app.db
    /// 具体表的读写放在各自的类里（如 AppLabelStore），这里不掺业务逻辑。
    /// </summary>
    internal static class AppStore
    {
        /// <summary>库结构版本。新增表/字段时 +1，并在 Upgrade 里补对应分支。</summary>
        public const int SchemaVersion = 1;

        private static readonly object Sync = new object();
        private static bool _ready;
        private static bool _failed;
        private static string _connectionString;

        public static string DatabasePath
        {
            get { return Path.Combine(Globals.AppDataDirectory, "data", "app.db"); }
        }

        private static string ConnectionString
        {
            get
            {
                if (_connectionString == null)
                {
                    _connectionString =
                        "Data Source=" + DatabasePath + ";Version=3;" +
                        "Pooling=True;Max Pool Size=20;" +
                        "Journal Mode=WAL;BusyTimeout=3000;Synchronous=Normal;";
                }
                return _connectionString;
            }
        }

        /// <summary>
        /// 确保库文件存在且结构为最新。只在第一次成功时做建库/升级；
        /// 失败后本次运行不再重试，调用方按"没有缓存"处理即可。
        /// </summary>
        public static bool EnsureReady()
        {
            if (_ready) return true;
            if (_failed) return false;

            lock (Sync)
            {
                if (_ready) return true;
                if (_failed) return false;

                try
                {
                    string path = DatabasePath;
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    using (SQLiteConnection connection = OpenRaw())
                    {
                        int version = ReadUserVersion(connection);
                        Upgrade(connection, version);
                    }

                    _ready = true;
                    AppLog.Write("应用数据数据库就绪：{0}（schema v{1}）", path, SchemaVersion);
                    return true;
                }
                catch (Exception ex)
                {
                    _failed = true;
                    AppLog.Write("应用数据数据库不可用，本次不缓存数据：{0}", ex.Message);
                    return false;
                }
            }
        }

        /// <summary>打开连接；数据库不可用或打开失败时返回 null，调用方直接跳过即可。</summary>
        public static SQLiteConnection OpenConnection()
        {
            if (!EnsureReady())
            {
                return null;
            }

            try
            {
                return OpenRaw();
            }
            catch (Exception ex)
            {
                AppLog.Write("打开应用数据数据库失败：{0}", ex.Message);
                return null;
            }
        }

        private static SQLiteConnection OpenRaw()
        {
            SQLiteConnection connection = new SQLiteConnection(ConnectionString);
            connection.Open();
            return connection;
        }

        private static int ReadUserVersion(SQLiteConnection connection)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version;";
                object value = command.ExecuteScalar();
                return value == null ? 0 : Convert.ToInt32(value);
            }
        }

        /// <summary>所有建表/改表集中在这里，按 user_version 逐级执行。</summary>
        private static void Upgrade(SQLiteConnection connection, int fromVersion)
        {
            if (fromVersion >= SchemaVersion) return;

            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                if (fromVersion < 1)
                {
                    Execute(connection, transaction,
                        "CREATE TABLE IF NOT EXISTS app_label(" +
                        "package TEXT PRIMARY KEY," +
                        "label TEXT NOT NULL," +
                        "label_resolved INTEGER NOT NULL DEFAULT 0," +
                        "is_service INTEGER NOT NULL DEFAULT 0," +
                        "icon_failed INTEGER NOT NULL DEFAULT 0," +
                        "updated_at INTEGER NOT NULL);");
                }

                // 后续加表/加字段在这里追加分支，例如：
                // if (fromVersion < 2)
                // {
                //     Execute(connection, transaction,
                //         "CREATE TABLE IF NOT EXISTS app_icon(package TEXT PRIMARY KEY, data BLOB NOT NULL);");
                // }

                transaction.Commit();
            }

            // user_version 不能放在事务里回滚，单独写入
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version = " + SchemaVersion + ";";
                command.ExecuteNonQuery();
            }

            AppLog.Write("应用数据数据库已升级：v{0} -> v{1}", fromVersion, SchemaVersion);
        }

        private static void Execute(SQLiteConnection connection, SQLiteTransaction transaction, string sql)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }
    }
}
