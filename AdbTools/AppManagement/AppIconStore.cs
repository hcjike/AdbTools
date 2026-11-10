using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdbTools
{
    /// <summary>
    /// 应用图标缓存，按设备隔离、按内容去重：
    /// - icon_blob：以图标内容的 SHA1 为主键，同一张图只存一份；
    /// - app_label.icon_hash：（设备键, 包名）→ 哈希，与名称同一行，不同设备可各自指向不同图标。
    /// WebP 在入库前统一转成 PNG，后续启动直接走系统解码器。
    /// </summary>
    internal static class AppIconStore
    {
        private const int MaxParametersPerQuery = 400;

        /// <summary>未被任何设备引用的图标内容只清理一次。</summary>
        private static bool _orphansCleaned;

        /// <summary>批量取某设备下这些包的图标，用于列表首次加载。</summary>
        public static Dictionary<string, ImageSource> GetIcons(string deviceKey, IEnumerable<string> packages)
        {
            Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
            if (packages == null)
            {
                return icons;
            }

            List<string> names = new List<string>();
            foreach (string package in packages)
            {
                if (!string.IsNullOrEmpty(package)) names.Add(package);
            }
            if (names.Count == 0)
            {
                return icons;
            }

            try
            {
                using (SQLiteConnection connection = AppStore.OpenConnection())
                {
                    if (connection == null) return icons;

                    // 同一张图可能被多个包引用，解码一次即可
                    Dictionary<string, ImageSource> byHash = new Dictionary<string, ImageSource>(StringComparer.Ordinal);

                    for (int offset = 0; offset < names.Count; offset += MaxParametersPerQuery)
                    {
                        int count = Math.Min(MaxParametersPerQuery, names.Count - offset);
                        using (SQLiteCommand command = connection.CreateCommand())
                        {
                            StringBuilder sql = new StringBuilder(
                                "SELECT a.package, a.icon_hash, b.data FROM app_label a " +
                                "JOIN icon_blob b ON b.icon_hash = a.icon_hash " +
                                "WHERE a.device_key = @d AND a.package IN (");
                            command.Parameters.AddWithValue("@d", Key(deviceKey));
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
                                    string package = reader.GetString(0);
                                    string hash = reader.GetString(1);

                                    ImageSource image;
                                    if (!byHash.TryGetValue(hash, out image))
                                    {
                                        image = Decode((byte[])reader.GetValue(2));
                                        byHash[hash] = image;
                                    }

                                    if (image != null)
                                    {
                                        icons[package] = image;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("批量读取应用图标失败：{0}", ex.Message);
            }

            return icons;
        }

        /// <summary>取单个包的图标。</summary>
        public static ImageSource GetIcon(string deviceKey, string packageName)
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
                            "SELECT b.data FROM app_label a JOIN icon_blob b ON b.icon_hash = a.icon_hash " +
                            "WHERE a.device_key = @d AND a.package = @p LIMIT 1;";
                        command.Parameters.AddWithValue("@d", Key(deviceKey));
                        command.Parameters.AddWithValue("@p", packageName);

                        byte[] data = command.ExecuteScalar() as byte[];
                        return data == null ? null : Decode(data);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("读取应用图标失败：{0}", ex.Message);
                return null;
            }
        }

        /// <summary>该设备下这个包是否已有图标，避免重复拉取。</summary>
        public static bool HasIcon(string deviceKey, string packageName)
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
                            "SELECT 1 FROM app_label WHERE device_key = @d AND package = @p " +
                            "AND icon_hash IS NOT NULL LIMIT 1;";
                        command.Parameters.AddWithValue("@d", Key(deviceKey));
                        command.Parameters.AddWithValue("@p", packageName);
                        return command.ExecuteScalar() != null;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("查询应用图标失败：{0}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 保存图标：WebP 先转 PNG，内容按 SHA1 去重写入 icon_blob，
        /// 再记下（设备, 包名）→ 哈希的映射。同一张图不会重复存。
        /// </summary>
        public static bool Save(string deviceKey, string packageName, byte[] data, string extension)
        {
            if (string.IsNullOrEmpty(packageName) || data == null || data.Length == 0)
            {
                return false;
            }

            // WebP 只在取图时解码一次，随后以 PNG 入库：解码器缺失也不会反复失败
            if (NormalizeExtension(extension) == "webp" || WebpDecoder.IsWebp(data))
            {
                byte[] png = EncodePng(WebpDecoder.Decode(data));
                if (png == null)
                {
                    return false;
                }

                data = png;
                extension = "png";
            }

            string hash = ComputeHash(data);
            string normalized = NormalizeExtension(extension);

            try
            {
                using (SQLiteConnection connection = AppStore.OpenConnection())
                {
                    if (connection == null) return false;

                    using (SQLiteTransaction transaction = connection.BeginTransaction())
                    {
                        using (SQLiteCommand insertBlob = connection.CreateCommand())
                        {
                            insertBlob.Transaction = transaction;
                            insertBlob.CommandText =
                                "INSERT OR IGNORE INTO icon_blob(icon_hash, extension, size, data, created_at) " +
                                "VALUES(@h, @e, @s, @b, @t);";
                            insertBlob.Parameters.AddWithValue("@h", hash);
                            insertBlob.Parameters.AddWithValue("@e", normalized);
                            insertBlob.Parameters.AddWithValue("@s", data.Length);
                            insertBlob.Parameters.AddWithValue("@b", data);
                            insertBlob.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                            insertBlob.ExecuteNonQuery();
                        }

                        using (SQLiteCommand upsert = connection.CreateCommand())
                        {
                            upsert.Transaction = transaction;
                            upsert.CommandText =
                                "INSERT INTO app_label(device_key, package, icon_hash, icon_failed, updated_at) " +
                                "VALUES(@d, @p, @h, 0, @t) " +
                                "ON CONFLICT(device_key, package) DO UPDATE SET " +
                                "icon_hash = excluded.icon_hash, icon_failed = 0, updated_at = excluded.updated_at;";
                            upsert.Parameters.AddWithValue("@d", Key(deviceKey));
                            upsert.Parameters.AddWithValue("@p", packageName);
                            upsert.Parameters.AddWithValue("@h", hash);
                            upsert.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                            upsert.ExecuteNonQuery();
                        }

                        transaction.Commit();
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("保存应用图标失败：{0}", ex.Message);
                return false;
            }
        }

        /// <summary>保存一张位图（矢量图标渲染结果）：统一转成 PNG 后入库。</summary>
        public static bool SaveBitmap(string deviceKey, string packageName, BitmapSource image)
        {
            byte[] png = EncodePng(image);
            return png != null && Save(deviceKey, packageName, png, "png");
        }

        /// <summary>清理没有任何设备引用的图标内容（应用被卸载、映射被更新后留下）。</summary>
        public static void CleanupOrphansOnce()
        {
            if (_orphansCleaned)
            {
                return;
            }
            _orphansCleaned = true;

            try
            {
                using (SQLiteConnection connection = AppStore.OpenConnection())
                {
                    if (connection == null) return;

                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "DELETE FROM icon_blob WHERE icon_hash NOT IN " +
                            "(SELECT icon_hash FROM app_label WHERE icon_hash IS NOT NULL);";
                        int removed = command.ExecuteNonQuery();
                        if (removed > 0)
                        {
                            AppLog.Write("已清理 {0} 个无人引用的图标缓存", removed);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("清理图标缓存失败：{0}", ex.Message);
            }
        }

        private static ImageSource Decode(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            // 旧数据可能是 WebP：交给 libwebp 解码
            if (WebpDecoder.IsWebp(data))
            {
                return WebpDecoder.Decode(data);
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(data, false))
                {
                    BitmapImage image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 64;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch
            {
                // 系统缺解码器（如损坏的图片）时按没有图标处理
                return null;
            }
        }

        private static byte[] EncodePng(BitmapSource image)
        {
            if (image == null)
            {
                return null;
            }

            try
            {
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (MemoryStream stream = new MemoryStream())
                {
                    encoder.Save(stream);
                    return stream.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string ComputeHash(byte[] data)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }
                return builder.ToString();
            }
        }

        private static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrEmpty(extension))
            {
                return "png";
            }

            string value = extension.Trim().TrimStart('.').ToLowerInvariant();
            switch (value)
            {
                case "png":
                case "webp":
                case "jpg":
                case "jpeg":
                    return value;
                default:
                    return "png";
            }
        }

        private static string Key(string deviceKey)
        {
            return string.IsNullOrWhiteSpace(deviceKey) ? AppDeviceKey.Unknown : deviceKey;
        }
    }
}
