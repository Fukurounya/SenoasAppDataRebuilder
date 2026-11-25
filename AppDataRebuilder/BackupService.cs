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
                CompressionLevel = settings.CompressionLevel,
                KeepTimestamps = settings.KeepTimestamps,
                KeepFileAttributes = settings.KeepFileAttributes,
                Success = true,
            };

            if (settings.Compress)
            {
                if (string.IsNullOrWhiteSpace(settings.WinRarPath) || !File.Exists(settings.WinRarPath))
                    throw new InvalidOperationException("未找到可用的 WinRAR 命令行工具");

                var cacheDirectory = Path.Combine(settings.CacheRoot, settings.JobId.ToString("N"));
                Directory.CreateDirectory(cacheDirectory);
                var cacheArchivePath = Path.Combine(cacheDirectory, settings.SourceName + ".rar");

                try
                {
                    log.Report("开始压缩：" + settings.SourceDirectory);
                    Report(progress, 0, 60, 0, "压缩");

                    var arguments = BuildArchiveArguments(settings, cacheArchivePath);
                    await RunRarAsync(
                        settings.WinRarPath,
                        arguments,
                        settings.SourceDirectory,
                        0,
                        60,
                        progress,
                        log,
                        cancellationToken,
                        "压缩").ConfigureAwait(false);

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
                var toolPath = GetRestoreToolPath(settings);
                if (string.IsNullOrWhiteSpace(toolPath) || !File.Exists(toolPath))
                    throw new InvalidOperationException("未找到可用的 WinRAR / UnRAR 命令行工具");

                if (!File.Exists(saveRecord.BackupPath))
                    throw new FileNotFoundException("备份归档不存在", saveRecord.BackupPath);

                if (string.IsNullOrWhiteSpace(saveRecord.Sha256))
                    throw new InvalidOperationException("保存记录中没有 SHA256，无法校验归档");

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

                    log.Report("测试归档完整性…");
                    await RunRarAsync(
                        toolPath,
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
                        toolPath,
                        new[] { "x", "-y", "-o+", cacheArchivePath, extractDirectory + Path.DirectorySeparatorChar },
                        cacheDirectory,
                        40,
                        30,
                        progress,
                        log,
                        cancellationToken,
                        "解压归档").ConfigureAwait(false);

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
                "-t",
            };

            if (settings.KeepTimestamps)
                arguments.Add("-ts");

            arguments.Add(archivePath);
            arguments.Add("*");
            return arguments;
        }

        private static string? GetRestoreToolPath(RestoreJobSettings settings)
        {
            // Rar.exe 是最稳定的命令行选择；没有 Rar.exe 时优先使用随附 UnRAR，
            // 最后才退回 WinRAR.exe 后台模式。
            if (!string.IsNullOrWhiteSpace(settings.RarPath)
                && string.Equals(Path.GetFileName(settings.RarPath), "Rar.exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(settings.RarPath))
            {
                return settings.RarPath;
            }

            if (!string.IsNullOrWhiteSpace(settings.UnRarPath) && File.Exists(settings.UnRarPath))
                return settings.UnRarPath;

            if (!string.IsNullOrWhiteSpace(settings.RarPath) && File.Exists(settings.RarPath))
                return settings.RarPath;

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
    }
}
