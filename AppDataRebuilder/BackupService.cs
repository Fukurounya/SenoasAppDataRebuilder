using System.IO.Compression;
using System.Text.RegularExpressions;

namespace AppDataRebuilder
{
    /// <summary>
    /// 保存/恢复的实际执行逻辑：压缩、SHA256、复制、校验、解压、robocopy。
    /// </summary>
    internal static class BackupService
    {
        private const string ExcludedRelativePath = @"Cache\cache";
        private const string RobocopyArguments = "/MIR /XJ /R:2 /W:2 /NP /NFL /NDL /NJH /NJS";

        private static readonly Regex PercentRegex = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);

        public static async Task<OperationRecord> SaveAsync(
            SaveJobSettings settings,
            IProgress<OperationProgress> progress,
            IProgress<string> log,
            CancellationToken cancellationToken)
        {
            var record = new OperationRecord
            {
                Type = OperationType.Save,
                Time = DateTimeOffset.Now,
                SourceDirectory = settings.SourceDirectory,
                TargetDirectory = settings.TargetDirectory,
                BackupPath = settings.BackupPath,
                Compressed = settings.Compress,
                UseZip = settings.UseZip,
                CompressionLevel = settings.CompressionLevel,
                KeepTimestamps = settings.KeepTimestamps,
                KeepFileAttributes = settings.KeepFileAttributes,
                Success = true,
            };

            if (settings.Compress)
            {
                // ZIP 由 .NET 生成，不依赖 WinRAR；只有 RAR 压缩需要 WinRAR。
                if (!settings.UseZip
                    && (string.IsNullOrWhiteSpace(settings.WinRarPath) || !File.Exists(settings.WinRarPath)))
                {
                    throw new InvalidOperationException("RAR 压缩需要 WinRAR，但未找到 WinRAR 命令行工具");
                }

                var cacheDirectory = Path.Combine(settings.CacheRoot, settings.JobId.ToString("N"));
                Directory.CreateDirectory(cacheDirectory);
                var cacheArchivePath = Path.Combine(
                    cacheDirectory,
                    settings.SourceName + (settings.UseZip ? ".zip" : ".rar"));

                try
                {
                    if (settings.UseZip)
                    {
                        log.Report("开始压缩为 ZIP：" + settings.SourceDirectory);
                        await CreateZipArchiveAsync(
                            settings,
                            cacheArchivePath,
                            0,
                            60,
                            progress,
                            log,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        log.Report("开始压缩：" + settings.SourceDirectory);
                        Report(progress, 0, 60, 0, "压缩");

                        var arguments = BuildArchiveArguments(settings, cacheArchivePath);
                        await RunRarAsync(
                            settings.WinRarPath!,
                            arguments,
                            settings.SourceDirectory,
                            0,
                            60,
                            progress,
                            log,
                            cancellationToken,
                            "压缩").ConfigureAwait(false);
                    }

                    if (!File.Exists(cacheArchivePath))
                        throw new InvalidOperationException("压缩完成但未生成归档文件");

                    log.Report("计算压缩文件 SHA256…");
                    Report(progress, 60, 10, 0, "计算 SHA256");
                    var sha256 = await FileSystemUtility.ComputeSha256Async(
                        cacheArchivePath,
                        new Progress<int>(value => Report(progress, 60, 10, value, "计算 SHA256")),
                        cancellationToken).ConfigureAwait(false);
                    record.Sha256 = sha256;

                    log.Report("复制归档到目标目录：" + settings.BackupPath);
                    var targetDirectory = Path.GetDirectoryName(settings.BackupPath);
                    if (!string.IsNullOrEmpty(targetDirectory))
                        Directory.CreateDirectory(targetDirectory);

                    await CopyFileWithProgressAsync(
                        cacheArchivePath,
                        settings.BackupPath,
                        70,
                        20,
                        "复制归档",
                        progress,
                        cancellationToken).ConfigureAwait(false);

                    log.Report("校验目标归档 SHA256…");
                    var targetSha256 = await FileSystemUtility.ComputeSha256Async(
                        settings.BackupPath,
                        new Progress<int>(value => Report(progress, 90, 5, value, "校验 SHA256")),
                        cancellationToken).ConfigureAwait(false);

                    if (!string.Equals(targetSha256, sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("复制后的归档 SHA256 与源归档不一致");

                    Report(progress, 95, 5, 100, "完成");
                }
                finally
                {
                    FileSystemUtility.TryDeleteDirectory(cacheDirectory);
                }
            }
            else
            {
                log.Report("使用 robocopy 复制文件夹：" + settings.SourceDirectory);
                await RunRobocopyAsync(
                    settings.SourceDirectory,
                    settings.BackupPath,
                    settings.KeepTimestamps,
                    settings.KeepFileAttributes,
                    0,
                    95,
                    progress,
                    log,
                    cancellationToken,
                    "复制文件").ConfigureAwait(false);

                Report(progress, 95, 5, 100, "完成");
            }

            return record;
        }

        public static async Task<OperationRecord> RestoreAsync(
            RestoreJobSettings settings,
            IProgress<OperationProgress> progress,
            IProgress<string> log,
            CancellationToken cancellationToken)
        {
            var saveRecord = settings.SaveRecord;
            var record = new OperationRecord
            {
                Type = OperationType.Restore,
                Time = DateTimeOffset.Now,
                SourceDirectory = saveRecord.SourceDirectory,
                TargetDirectory = saveRecord.TargetDirectory,
                BackupPath = saveRecord.BackupPath,
                Compressed = saveRecord.Compressed,
                UseZip = saveRecord.IsZipArchive,
                Sha256 = saveRecord.Sha256,
                CompressionLevel = saveRecord.CompressionLevel,
                KeepTimestamps = saveRecord.KeepTimestamps,
                KeepFileAttributes = saveRecord.KeepFileAttributes,
                RestoreDestination = settings.DestinationDirectory,
                Success = true,
            };

            Directory.CreateDirectory(settings.DestinationDirectory);

            if (saveRecord.Compressed)
            {
                var isZip = saveRecord.IsZipArchive;
                string? toolPath = null;

                if (!File.Exists(saveRecord.BackupPath))
                    throw new FileNotFoundException("备份归档不存在", saveRecord.BackupPath);

                if (string.IsNullOrWhiteSpace(saveRecord.Sha256))
                    throw new InvalidOperationException("保存记录中没有 SHA256，无法校验归档");

                if (!isZip)
                {
                    var resolvedToolPath = GetRestoreToolPath(settings);
                    if (string.IsNullOrWhiteSpace(resolvedToolPath) || !File.Exists(resolvedToolPath))
                        throw new InvalidOperationException("未找到可用的 WinRAR / UnRAR 命令行工具");

                    toolPath = resolvedToolPath;
                }

                log.Report("校验备份归档 SHA256…");
                Report(progress, 0, 10, 0, "校验归档");
                var archiveSha256 = await FileSystemUtility.ComputeSha256Async(
                    saveRecord.BackupPath,
                    new Progress<int>(value => Report(progress, 0, 10, value, "校验归档")),
                    cancellationToken).ConfigureAwait(false);

                if (!string.Equals(archiveSha256, saveRecord.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("备份归档 SHA256 校验失败，可能已被覆盖或损坏");

                var cacheDirectory = Path.Combine(settings.CacheRoot, settings.JobId.ToString("N"));
                Directory.CreateDirectory(cacheDirectory);
                var cacheArchivePath = Path.Combine(cacheDirectory, Path.GetFileName(saveRecord.BackupPath));
                var extractDirectory = Path.Combine(cacheDirectory, "Extract");

                try
                {
                    log.Report("复制归档到缓存目录…");
                    await CopyFileWithProgressAsync(
                        saveRecord.BackupPath,
                        cacheArchivePath,
                        10,
                        15,
                        "复制归档",
                        progress,
                        cancellationToken).ConfigureAwait(false);

                    log.Report("校验缓存中的归档…");
                    var cacheSha256 = await FileSystemUtility.ComputeSha256Async(
                        cacheArchivePath,
                        new Progress<int>(value => Report(progress, 25, 5, value, "校验缓存")),
                        cancellationToken).ConfigureAwait(false);

                    if (!string.Equals(cacheSha256, saveRecord.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("缓存归档 SHA256 校验失败");

                    if (isZip)
                    {
                        log.Report("解压 ZIP 归档到缓存目录…");
                        await ExtractZipArchiveAsync(
                            cacheArchivePath,
                            extractDirectory,
                            saveRecord.KeepTimestamps,
                            30,
                            40,
                            progress,
                            log,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        log.Report("测试归档完整性…");
                        await RunRarAsync(
                            toolPath!,
                            new[] { "t", "-y", cacheArchivePath },
                            cacheDirectory,
                            30,
                            10,
                            progress,
                            log,
                            cancellationToken,
                            "校验归档").ConfigureAwait(false);

                        Directory.CreateDirectory(extractDirectory);
                        log.Report("解压归档到缓存目录…");
                        await RunRarAsync(
                            toolPath!,
                            new[] { "x", "-y", "-o+", cacheArchivePath, extractDirectory + Path.DirectorySeparatorChar },
                            cacheDirectory,
                            40,
                            30,
                            progress,
                            log,
                            cancellationToken,
                            "解压归档").ConfigureAwait(false);
                    }

                    log.Report("复制解压结果到恢复目录：" + settings.DestinationDirectory);
                    await RunRobocopyAsync(
                        extractDirectory,
                        settings.DestinationDirectory,
                        keepTimestamps: true,
                        keepFileAttributes: true,
                        70,
                        25,
                        progress,
                        log,
                        cancellationToken,
                        "恢复文件").ConfigureAwait(false);

                    Report(progress, 95, 5, 100, "完成");
                }
                finally
                {
                    FileSystemUtility.TryDeleteDirectory(cacheDirectory);
                }
            }
            else
            {
                if (!Directory.Exists(saveRecord.BackupPath))
                    throw new DirectoryNotFoundException("备份文件夹不存在：" + saveRecord.BackupPath);

                log.Report("使用 robocopy 恢复文件夹：" + saveRecord.BackupPath);
                await RunRobocopyAsync(
                    saveRecord.BackupPath,
                    settings.DestinationDirectory,
                    keepTimestamps: true,
                    keepFileAttributes: true,
                    0,
                    95,
                    progress,
                    log,
                    cancellationToken,
                    "恢复文件").ConfigureAwait(false);

                Report(progress, 95, 5, 100, "完成");
            }

            return record;
        }

        private static List<string> BuildArchiveArguments(SaveJobSettings settings, string archivePath)
        {
            var arguments = new List<string>
            {
                "a",
                "-r",
                "-y",
                $"-m{Math.Clamp(settings.CompressionLevel, 0, 5)}",
                "-x" + ExcludedRelativePath,
            };

            // “测试压缩的文件”对应 RAR 的 -t；勾选“不测试压缩的文件”时不传该参数。
            if (!settings.SkipArchiveTest)
                arguments.Add("-t");

            if (settings.KeepTimestamps)
                arguments.Add("-ts");

            arguments.Add(archivePath);
            arguments.Add("*");
            return arguments;
        }

        private static async Task CreateZipArchiveAsync(
            SaveJobSettings settings,
            string archivePath,
            int overallBase,
            int overallSpan,
            IProgress<OperationProgress> progress,
            IProgress<string> log,
            CancellationToken cancellationToken)
        {
            var files = new List<ZipFileEntry>();
            var directories = new List<string>();
            var pending = new Stack<string>();
            pending.Push(settings.SourceDirectory);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();

                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(current))
                    {
                        var relativePath = Path.GetRelativePath(settings.SourceDirectory, directory);
                        if (IsExcludedArchivePath(relativePath))
                            continue;

                        directories.Add(relativePath);
                        pending.Push(directory);
                    }

                    foreach (var file in Directory.EnumerateFiles(current))
                    {
                        var relativePath = Path.GetRelativePath(settings.SourceDirectory, file);
                        if (IsExcludedArchivePath(relativePath))
                            continue;

                        try
                        {
                            files.Add(new ZipFileEntry(file, relativePath, new FileInfo(file).Length));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            log.Report("跳过无法读取的文件：" + file);
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    log.Report("跳过无权限的目录：" + current);
                }
                catch (IOException ex)
                {
                    log.Report("跳过无法读取的目录：" + current + "：" + ex.Message);
                }
            }

            files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
            directories.Sort(StringComparer.OrdinalIgnoreCase);

            var totalBytes = files.Sum(file => file.Length);
            var compressionLevel = GetZipCompressionLevel(settings.CompressionLevel);
            long processedBytes = 0;
            var lastPercent = -1;

            using var archiveStream = new FileStream(
                archivePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous);

            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create);

            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = archive.CreateEntry(directory.Replace('\\', '/').TrimEnd('/') + "/");
                if (settings.KeepTimestamps)
                    SetZipEntryTimestamp(entry, Directory.GetLastWriteTime(Path.Combine(settings.SourceDirectory, directory)));
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = archive.CreateEntry(file.RelativePath.Replace('\\', '/'), compressionLevel);
                if (settings.KeepTimestamps)
                    SetZipEntryTimestamp(entry, File.GetLastWriteTime(file.FullPath));

                await using (var input = new FileStream(
                    file.FullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var output = entry.Open())
                {
                    var buffer = new byte[1024 * 1024];

                    while (true)
                    {
                        var read = await input.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken).ConfigureAwait(false);

                        if (read <= 0)
                            break;

                        await output.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken).ConfigureAwait(false);

                        processedBytes += read;

                        if (totalBytes > 0)
                        {
                            var percent = (int)Math.Clamp(processedBytes * 100 / totalBytes, 0, 100);
                            if (percent != lastPercent)
                            {
                                lastPercent = percent;
                                Report(progress, overallBase, overallSpan, percent, "压缩");
                            }
                        }
                    }
                }
            }

            Report(progress, overallBase, overallSpan, 100, "压缩");
        }

        private static async Task ExtractZipArchiveAsync(
            string archivePath,
            string extractDirectory,
            bool keepTimestamps,
            int overallBase,
            int overallSpan,
            IProgress<OperationProgress> progress,
            IProgress<string> log,
            CancellationToken cancellationToken)
        {
            var rootDirectory = Path.GetFullPath(extractDirectory);
            var rootPrefix = rootDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            Directory.CreateDirectory(rootDirectory);

            await using var archiveStream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);

            var fileEntries = archive.Entries
                .Where(entry => entry.Name.Length > 0)
                .ToList();
            var totalBytes = fileEntries.Sum(entry => entry.Length);
            long processedBytes = 0;
            var lastPercent = -1;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = entry.FullName
                    .Replace('/', Path.DirectorySeparatorChar)
                    .TrimStart(Path.DirectorySeparatorChar);

                if (relativePath.Length == 0)
                    continue;

                var destinationPath = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
                if (!destinationPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(destinationPath, rootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("ZIP 归档包含越界路径：" + entry.FullName);
                }

                if (entry.Name.Length == 0)
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDirectory))
                    Directory.CreateDirectory(destinationDirectory);

                await using (var input = entry.Open())
                await using (var output = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = new byte[1024 * 1024];

                    while (true)
                    {
                        var read = await input.ReadAsync(
                            buffer.AsMemory(0, buffer.Length),
                            cancellationToken).ConfigureAwait(false);

                        if (read <= 0)
                            break;

                        await output.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken).ConfigureAwait(false);

                        processedBytes += read;

                        if (totalBytes > 0)
                        {
                            var percent = (int)Math.Clamp(processedBytes * 100 / totalBytes, 0, 100);
                            if (percent != lastPercent)
                            {
                                lastPercent = percent;
                                Report(progress, overallBase, overallSpan, percent, "解压");
                            }
                        }
                    }
                }

                if (keepTimestamps)
                    TrySetExtractedTimestamp(destinationPath, entry.LastWriteTime, log);
            }

            Report(progress, overallBase, overallSpan, 100, "解压");
        }

        private static void TrySetExtractedTimestamp(
            string path,
            DateTimeOffset timestamp,
            IProgress<string> log)
        {
            if (timestamp.Year is < 1980 or > 2107)
                return;

            try
            {
                File.SetLastWriteTime(path, timestamp.LocalDateTime);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                log.Report("无法恢复时间戳：" + path);
            }
        }

        private static CompressionLevel GetZipCompressionLevel(int level)
        {
            return Math.Clamp(level, 0, 5) switch
            {
                0 => CompressionLevel.NoCompression,
                1 or 2 => CompressionLevel.Fastest,
                3 or 4 => CompressionLevel.Optimal,
                _ => CompressionLevel.SmallestSize,
            };
        }

        private static void SetZipEntryTimestamp(ZipArchiveEntry entry, DateTime timestamp)
        {
            // ZIP 的 DOS 时间范围是 1980-2107，超出范围时保留默认时间。
            if (timestamp.Year is >= 1980 and <= 2107)
                entry.LastWriteTime = new DateTimeOffset(timestamp);
        }

        private static bool IsExcludedArchivePath(string relativePath)
        {
            var normalized = relativePath.Replace('/', '\\').Trim('\\');
            return string.Equals(normalized, ExcludedRelativePath, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(ExcludedRelativePath + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetRestoreToolPath(RestoreJobSettings settings)
        {
            // WinRAR / Rar.exe 始终优先；UnRAR 只在 WinRAR 命令行不可用时回退。
            if (!string.IsNullOrWhiteSpace(settings.RarPath)
                && File.Exists(settings.RarPath)
                && !IsUnRarExe(settings.RarPath))
            {
                return settings.RarPath;
            }

            if (!string.IsNullOrWhiteSpace(settings.UnRarPath) && File.Exists(settings.UnRarPath))
                return settings.UnRarPath;

            return null;
        }

        private static async Task RunRarAsync(
            string toolPath,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            int overallBase,
            int overallSpan,
            IProgress<OperationProgress> progress,
            IProgress<string> log,
            CancellationToken cancellationToken,
            string phase)
        {
            var finalArguments = new List<string>();
            if (IsWinRarExe(toolPath))
                finalArguments.Add("-ibck");

            finalArguments.AddRange(arguments);

            var sync = new object();
            var lastPercent = -1;

            void HandleLine(string line)
            {
                var matches = PercentRegex.Matches(line);
                if (matches.Count > 0
                    && int.TryParse(matches[^1].Groups[1].Value, out var percent))
                {
                    lock (sync)
                    {
                        if (percent > lastPercent)
                        {
                            lastPercent = percent;
                            Report(progress, overallBase, overallSpan, percent, phase);
                        }
                    }
                }

                if (LooksLikeProblem(line))
                    log.Report(line);
            }

            Report(progress, overallBase, overallSpan, 0, phase);

            var result = await ProcessRunner.RunAsync(
                toolPath,
                finalArguments,
                workingDirectory,
                HandleLine,
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{phase}失败（WinRAR/UnRAR 返回 {result.ExitCode}）：{result.GetErrorSummary()}");
            }

            Report(progress, overallBase, overallSpan, 100, phase);
        }

        private static async Task RunRobocopyAsync(
            string sourceDirectory,
            string destinationDirectory,
            bool keepTimestamps,
            bool keepFileAttributes,
            int overallBase,
            int overallSpan,
            IProgress<OperationProgress> progress,
            IProgress<string> log,
            CancellationToken cancellationToken,
            string phase)
        {
            Directory.CreateDirectory(destinationDirectory);

            var robocopyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "robocopy.exe");

            if (!File.Exists(robocopyPath))
                robocopyPath = "robocopy.exe";

            var arguments = new List<string>
            {
                sourceDirectory,
                destinationDirectory,
            };

            arguments.AddRange(RobocopyArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            arguments.Add($"/MT:{FileSystemUtility.GetRobocopyThreadCount()}");

            if (keepFileAttributes)
            {
                arguments.Add("/COPY:DAT");
                arguments.Add("/DCOPY:DAT");
            }
            else if (keepTimestamps)
            {
                arguments.Add("/COPY:DT");
                arguments.Add("/DCOPY:T");
            }
            else
            {
                arguments.Add("/COPY:D");
            }

            var totalBytes = FileSystemUtility.GetDirectorySize(sourceDirectory);
            using var pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pollTask = PollCopyProgressAsync(
                destinationDirectory,
                totalBytes,
                overallBase,
                overallSpan,
                progress,
                phase,
                pollCancellation.Token);

            try
            {
                Report(progress, overallBase, overallSpan, 0, phase);

                var result = await ProcessRunner.RunAsync(
                    robocopyPath,
                    arguments,
                    workingDirectory: null,
                    onOutputLine: line =>
                    {
                        if (LooksLikeProblem(line))
                            log.Report(line);
                    },
                    cancellationToken).ConfigureAwait(false);

                if (result.ExitCode >= 8)
                {
                    throw new InvalidOperationException(
                        $"{phase}失败（robocopy 返回 {result.ExitCode}）：{result.GetErrorSummary()}");
                }
            }
            finally
            {
                pollCancellation.Cancel();
                try
                {
                    await pollTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 正常的轮询结束。
                }
            }

            Report(progress, overallBase, overallSpan, 100, phase);
        }

        private static async Task PollCopyProgressAsync(
            string destinationDirectory,
            long totalBytes,
            int overallBase,
            int overallSpan,
            IProgress<OperationProgress> progress,
            string phase,
            CancellationToken cancellationToken)
        {
            var lastPercent = -1;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(400, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var copiedBytes = FileSystemUtility.GetDirectorySize(destinationDirectory);
                var percent = totalBytes <= 0
                    ? 99
                    : (int)Math.Clamp(copiedBytes * 100 / totalBytes, 0, 99);

                if (percent > lastPercent)
                {
                    lastPercent = percent;
                    Report(progress, overallBase, overallSpan, percent, phase);
                }
            }
        }

        private static async Task CopyFileWithProgressAsync(
            string sourcePath,
            string destinationPath,
            int overallBase,
            int overallSpan,
            string phase,
            IProgress<OperationProgress> progress,
            CancellationToken cancellationToken)
        {
            var fileProgress = new Progress<int>(
                value => Report(progress, overallBase, overallSpan, value, phase));

            await FileSystemUtility.CopyFileWithProgressAsync(
                sourcePath,
                destinationPath,
                fileProgress,
                cancellationToken).ConfigureAwait(false);
        }

        private static void Report(
            IProgress<OperationProgress> progress,
            int overallBase,
            int overallSpan,
            int phasePercent,
            string phase)
        {
            var clampedPhase = Math.Clamp(phasePercent, 0, 100);
            var overall = Math.Clamp(overallBase + overallSpan * clampedPhase / 100, 0, 100);
            progress.Report(new OperationProgress(overall, clampedPhase, phase));
        }

        private static bool IsWinRarExe(string toolPath)
            => string.Equals(Path.GetFileName(toolPath), "WinRAR.exe", StringComparison.OrdinalIgnoreCase);

        private static bool IsUnRarExe(string toolPath)
            => string.Equals(Path.GetFileName(toolPath), "UnRAR.exe", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeProblem(string line)
        {
            return line.Contains("错误", StringComparison.OrdinalIgnoreCase)
                || line.Contains("失败", StringComparison.OrdinalIgnoreCase)
                || line.Contains("警告", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                || line.Contains("WARNING", StringComparison.OrdinalIgnoreCase)
                || line.Contains("cannot", StringComparison.OrdinalIgnoreCase)
                || line.Contains("access denied", StringComparison.OrdinalIgnoreCase);
        }

        private readonly record struct ZipFileEntry(string FullPath, string RelativePath, long Length);
    }
}
