using System.Security;
using Microsoft.Win32;

namespace AppDataRebuilder
{
    /// <summary>
    /// 查找本机注册或安装的 WinRAR 命令行工具，以及程序目录下随附的 UnRAR。
    /// </summary>
    internal static class RarToolLocator
    {
        public const string UnRarResourceName = "AppDataRebuilder.UnRAR.exe";

        private static readonly string[] AppPathRegistryKeys =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Rar.exe",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\WinRAR.exe",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\Rar.exe",
        };

        private static readonly string[] WinRarRegistryKeys =
        {
            @"SOFTWARE\WinRAR",
            @"SOFTWARE\WOW6432Node\WinRAR",
        };

        private static readonly string[] CommandLineToolNames =
        {
            "Rar.exe",
            "WinRAR.exe",
        };

        /// <summary>
        /// 返回 WinRAR 命令行工具的完整路径；未找到时返回 null。
        /// 优先返回 Rar.exe，只有 WinRAR.exe 时也视为可用。
        /// </summary>
        public static string? FindWinRarPath()
        {
            foreach (var candidate in EnumerateRegistryCandidates())
            {
                var resolved = ResolveExecutable(candidate);
                if (resolved is not null)
                    return resolved;
            }

            foreach (var directory in EnumerateCommonInstallDirectories())
            {
                var resolved = ResolveFromDirectory(directory);
                if (resolved is not null)
                    return resolved;
            }

            foreach (var directory in EnumeratePathDirectories())
            {
                var resolved = ResolveFromDirectory(directory);
                if (resolved is not null)
                    return resolved;
            }

            return null;
        }

        /// <summary>
        /// 返回程序目录下 UnRAR.exe 的完整路径；未找到时返回 null。
        /// 只检查程序自身目录，不搜索当前工作目录或任意子目录。
        /// </summary>
        public static string? FindUnRarPath(string? baseDirectory)
        {
            foreach (var directory in EnumerateUnRarDirectories(baseDirectory))
            {
                foreach (var fileName in new[] { "UnRAR.exe", "unrar.exe" })
                {
                    try
                    {
                        var path = Path.Combine(directory, fileName);
                        if (File.Exists(path))
                            return Path.GetFullPath(path);
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
                    {
                        // 忽略无效目录，继续尝试其他候选名。
                    }
                }
            }

            return null;
        }

        /// <summary>程序集中是否打包了 UnRAR.exe。</summary>
        public static bool HasEmbeddedUnRar()
        {
            try
            {
                using var stream = typeof(RarToolLocator).Assembly
                    .GetManifestResourceStream(UnRarResourceName);
                return stream is not null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 仅在确实需要 UnRAR 时，把程序集内的 UnRAR.exe 释放到指定目录。
        /// 返回释放后的完整路径；没有嵌入资源或释放失败时返回 null。
        /// </summary>
        public static string? ExtractEmbeddedUnRar(string? targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
                return null;

            try
            {
                Directory.CreateDirectory(targetDirectory);
                var path = Path.Combine(targetDirectory, "UnRAR.exe");
                if (File.Exists(path))
                    return Path.GetFullPath(path);

                using var stream = typeof(RarToolLocator).Assembly
                    .GetManifestResourceStream(UnRarResourceName);
                if (stream is null)
                    return null;

                var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        stream.CopyTo(output);
                    }

                    File.Move(temporaryPath, path, overwrite: true);
                    return Path.GetFullPath(path);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                            File.Delete(temporaryPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 临时文件清理失败不阻塞结果。
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }
        }

        private static IEnumerable<string> EnumerateUnRarDirectories(string? baseDirectory)
        {
            var directory = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppContext.BaseDirectory
                : baseDirectory;

            if (string.IsNullOrWhiteSpace(directory))
                yield break;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(directory);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
            {
                yield break;
            }

            yield return fullPath;
        }

        private static IEnumerable<string?> EnumerateRegistryCandidates()
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var subKeyName in AppPathRegistryKeys)
                    yield return ReadRegistryValue(hive, subKeyName, string.Empty);

                foreach (var subKeyName in WinRarRegistryKeys)
                {
                    yield return ReadRegistryValue(hive, subKeyName, "exe64");
                    yield return ReadRegistryValue(hive, subKeyName, "exe32");
                }
            }
        }

        private static string? ReadRegistryValue(RegistryKey hive, string subKeyName, string valueName)
        {
            try
            {
                using var key = hive.OpenSubKey(subKeyName);
                if (key is null)
                    return null;

                var value = valueName.Length == 0
                    ? key.GetValue(null) as string
                    : key.GetValue(valueName) as string;

                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }

        private static string? ResolveExecutable(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return null;

            try
            {
                var path = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));

                if (Directory.Exists(path))
                    return ResolveFromDirectory(path);

                if (!File.Exists(path))
                    return null;

                // 注册表通常记录 WinRAR.exe，若同目录存在 Rar.exe 则优先使用命令行版。
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    var rarPath = Path.Combine(directory, "Rar.exe");
                    if (File.Exists(rarPath))
                        return Path.GetFullPath(rarPath);
                }

                return Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or SecurityException)
            {
                return null;
            }
        }

        private static string? ResolveFromDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                return null;

            foreach (var toolName in CommandLineToolNames)
            {
                try
                {
                    var path = Path.Combine(directory, toolName);
                    if (File.Exists(path))
                        return Path.GetFullPath(path);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
                {
                    // 忽略无效路径，继续尝试下一个候选。
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateCommonInstallDirectories()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
                yield return Path.Combine(programFiles, "WinRAR");

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86)
                && !string.Equals(programFiles, programFilesX86, StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(programFilesX86, "WinRAR");
            }
        }

        private static IEnumerable<string> EnumeratePathDirectories()
        {
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
                yield break;

            foreach (var segment in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var directory = segment.Trim().Trim('"');
                if (directory.Length > 0)
                    yield return directory;
            }
        }
    }
}
