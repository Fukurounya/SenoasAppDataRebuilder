using System.Security.Cryptography;

namespace AppDataRebuilder
{
    internal static class FileSystemUtility
    {
        private const int BufferSize = 1024 * 1024;

        public static string NormalizePath(string path)
        {
            var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
            var root = Path.GetPathRoot(fullPath);

            if (!string.IsNullOrEmpty(root) && fullPath.Length > root.Length)
                fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return fullPath;
        }

        public static bool IsRootDirectory(string path)
        {
            var fullPath = NormalizePath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
                return false;

            return string.Equals(
                fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 校验目录路径；返回 null 表示通过，否则返回错误说明。
        /// </summary>
        public static string? ValidateDirectoryPath(string path, bool mustExist)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "目录不能为空";

            string fullPath;
            try
            {
                fullPath = NormalizePath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
            {
                return "目录路径无效：" + ex.Message;
            }

            if (IsRootDirectory(fullPath))
                return "不能使用磁盘根目录";

            if (File.Exists(fullPath))
                return "路径是文件，不是目录";

            if (mustExist && !Directory.Exists(fullPath))
                return "目录不存在：" + fullPath;

            return null;
        }

        public static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            try
            {
                return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>
        /// 判断两个路径是否相同或互为父子目录，用于任务冲突检测。
        /// </summary>
        public static bool PathsOverlap(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            string leftPath;
            string rightPath;
            try
            {
                leftPath = NormalizePath(left);
                rightPath = NormalizePath(right);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                return false;
            }

            if (string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase))
                return true;

            var leftWithSeparator = leftPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var rightWithSeparator = rightPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            return leftWithSeparator.StartsWith(rightWithSeparator, StringComparison.OrdinalIgnoreCase)
                || rightWithSeparator.StartsWith(leftWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        public static int GetRobocopyThreadCount()
            => Math.Clamp(Environment.ProcessorCount, 4, 32);

        public static long GetDirectorySize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return 0;

            long total = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 忽略读取期间消失的文件。
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录在枚举期间不可读，返回已统计的部分。
            }

            return total;
        }

        public static async Task<string> ComputeSha256Async(
            string path,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BufferSize];
            var total = stream.Length;
            long processed = 0;
            var lastPercent = -1;

            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    break;

                hash.AppendData(buffer, 0, read);
                processed += read;

                if (total > 0)
                {
                    var percent = (int)Math.Clamp(processed * 100 / total, 0, 100);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(percent);
                    }
                }
            }

            progress?.Report(100);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        public static async Task CopyFileWithProgressAsync(
            string sourcePath,
            string destinationPath,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[BufferSize];
            var total = source.Length;
            long copied = 0;
            var lastPercent = -1;

            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;

                if (total > 0)
                {
                    var percent = (int)Math.Clamp(copied * 100 / total, 0, 100);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Report(percent);
                    }
                }
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(100);
        }

        public static void TryDeleteDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清理失败不阻塞任务结果。
            }
        }
    }
}
