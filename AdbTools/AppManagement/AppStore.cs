using System;
using System.Data.SQLite;
using System.IO;

namespace AdbTools
{
    /// <summary>
    /// 应用数据数据库的基础设施：库文件位置、连接、建库与版本升级。
    /// 库文件：%APPDATA%\AdbTools\data\AdbTools.db
    /// 具体表的读写放在各自的类里（如 AppLabelStore），这里不掺业务逻辑。
    /// </summary>
    internal static class AppStore
    {
        /// <summary>库结构版本。新增表/字段时 +1，并在 Upgrade 里补对应分支。</summary>
        public const int SchemaVersion = 2;

        /// <summary>旧版本用过的库文件名，启动时清理，避免残留占空间。</summary>
        private static readonly string[] LegacyDatabaseNames = { "app.db", "apps.db", "database.db" };

        /// <summary>旧版本用过的图标目录（图标已改存数据库）。</summary>
        private const string LegacyIconsFolder = "icons";

        private static readonly object Sync = new object();
        private static bool _ready;
        private static bool _failed;
        private static string _connectionString;

        public static string DatabasePath
        {
            get { return Path.Combine(Globals.AppDataDirectory, "data", "AdbTools.db"); }
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

                    // 结构变化较大，改用新库文件；旧库与旧图标目录直接清掉
                    CleanupLegacyFiles();

                    using (SQLiteConnection connection = OpenRaw())
                    {
                        int version = ReadUserVersion(connection);
                        Upgrade(connection, version);
                        EnsureSchemaShape(connection);
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

            CreateSchema(connection);

            // user_version 不能放在事务里回滚，单独写入
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version = " + SchemaVersion + ";";
                command.ExecuteNonQuery();
            }

            AppLog.Write("应用数据数据库已升级：v{0} -> v{1}", fromVersion, SchemaVersion);
        }

        /// <summary>
        /// 一个包在：设备（device_key）上一次只可能有一套展示信息：
        /// 名称、名称是否已定论、系统服务判定、图标哈希、图标是否确定拿不到。
        /// </summary>
        private static void CreateSchema(SQLiteConnection connection)
        {
            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                Execute(connection, transaction,
                    "CREATE TABLE IF NOT EXISTS app_label(" +
                    "device_key TEXT NOT NULL," +
                    "package TEXT NOT NULL," +
                    "label TEXT," +
                    "label_resolved INTEGER NOT NULL DEFAULT 0," +
                    "is_service INTEGER," +
                    "icon_hash TEXT," +
                    "icon_failed INTEGER NOT NULL DEFAULT 0," +
                    "updated_at INTEGER," +
                    "PRIMARY KEY(device_key, package));");
                Execute(connection, transaction,
                    "CREATE TABLE IF NOT EXISTS icon_blob(" +
                    "icon_hash TEXT PRIMARY KEY," +
                    "extension TEXT NOT NULL," +
                    "size INTEGER NOT NULL," +
                    "data BLOB NOT NULL," +
                    "created_at INTEGER NOT NULL);");
                Execute(connection, transaction,
                    "CREATE INDEX IF NOT EXISTS idx_app_label_icon_hash ON app_label(icon_hash);");

                // v2：远程控制窗口的用户尺寸，横屏、竖屏各一份
                // （NULL 表示该方向还没调整过）。放在这里用 IF NOT EXISTS，
                // 旧库升级时只会补建这张表，不影响已有缓存。
                Execute(connection, transaction,
                    "CREATE TABLE IF NOT EXISTS remote_window(" +
                    "device_key TEXT PRIMARY KEY," +
                    "landscape_width REAL," +
                    "landscape_height REAL," +
                    "portrait_width REAL," +
                    "portrait_height REAL," +
                    "updated_at INTEGER NOT NULL);");

                transaction.Commit();
            }
        }

        /// <summary>
        /// 旧版本可能留下同版本号但结构不同的库文件（例如早期没有 device_key 的表），
        /// 缺列/缺表时整库重建，避免后续 SQL 全部失败。数据可重新解析，不做迁移。
        /// 注意：以后给 app_label 加列时，对应的 ColumnExists 检查也要加在这里。
        /// </summary>
        private static void EnsureSchemaShape(SQLiteConnection connection)
        {
            bool ok = TableExists(connection, "icon_blob")
                && TableExists(connection, "app_label")
                && TableExists(connection, "remote_window")
                && ColumnExists(connection, "app_label", "device_key")
                && ColumnExists(connection, "app_label", "label_resolved")
                && ColumnExists(connection, "app_label", "is_service")
                && ColumnExists(connection, "app_label", "icon_hash")
                && ColumnExists(connection, "app_label", "icon_failed");

            if (ok) return;

            AppLog.Write("应用数据表结构与当前版本不符，已按新结构重建");

            using (SQLiteTransaction transaction = connection.BeginTransaction())
            {
                Execute(connection, transaction, "DROP TABLE IF EXISTS app_label;");
                Execute(connection, transaction, "DROP TABLE IF EXISTS icon_blob;");
                transaction.Commit();
            }

            CreateSchema(connection);
        }

        private static bool TableExists(SQLiteConnection connection, string table)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n LIMIT 1;";
                command.Parameters.AddWithValue("@n", table);
                return command.ExecuteScalar() != null;
            }
        }

        private static bool ColumnExists(SQLiteConnection connection, string table, string column)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(" + table + ");";
                using (SQLiteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 清掉旧版本留下的库文件（及其 WAL/SHM）与图标目录。
        /// 旧库没有设备维度，无法判断数据归属，留着只会白占空间。
        /// 文件被占用（例如旧版程序还开着）时只记日志，不影响本次使用。
        /// </summary>
        private static void CleanupLegacyFiles()
        {
            string dir = Path.Combine(Globals.AppDataDirectory, "data");

            foreach (string name in LegacyDatabaseNames)
            {
                foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
                {
                    string path = Path.Combine(dir, name + suffix);
                    try
                    {
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("删除旧数据库文件失败：{0}", ex.Message);
                    }
                }
            }

            string icons = Path.Combine(dir, LegacyIconsFolder);
            try
            {
                if (Directory.Exists(icons)) Directory.Delete(icons, true);
            }
            catch (Exception ex)
            {
                AppLog.Write("清理旧图标缓存失败：{0}", ex.Message);
            }
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
