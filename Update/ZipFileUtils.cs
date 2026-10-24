using System;
using System.IO;
using System.IO.Compression;

namespace Update
{
    public class ZipFileUtils
    {
        /// <summary>
        /// 解压zip文件到指定目录（自动覆盖已存在文件，防止路径穿越）
        /// </summary>
        /// <exception cref="FileNotFoundException">zip文件不存在时抛出</exception>
        public static bool UnzipFile(string zipFilePath, string extractPath)
        {
            if (!File.Exists(zipFilePath))
            {
                throw new FileNotFoundException($"ZIP文件不存在：{zipFilePath}");
            }

            string rootPath = Path.GetFullPath(extractPath);
            Directory.CreateDirectory(rootPath);

            using (ZipArchive archive = ZipFile.OpenRead(zipFilePath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string fullPath = Path.GetFullPath(Path.Combine(rootPath, entry.FullName));
                    if (!fullPath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        // 跳过试图解压目标目录之外的条目
                        continue;
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        // 目录条目
                        Directory.CreateDirectory(fullPath);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                        // 覆盖已存在的文件
                        entry.ExtractToFile(fullPath, true);
                    }
                }
            }

            File.Delete(zipFilePath);

            return true;
        }

    }
}
