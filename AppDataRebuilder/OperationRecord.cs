namespace AppDataRebuilder
{
    internal enum OperationType
    {
        Save,
        Restore,
    }

    /// <summary>
    /// 一次保存或恢复操作的完整历史记录。
    /// </summary>
    internal sealed class OperationRecord
    {
        public static readonly string[] CompressionLevels =
        {
            "储存",
            "最快",
            "较快",
            "标准",
            "较好",
            "最好",
        };

        public OperationType Type { get; set; } = OperationType.Save;
        public DateTimeOffset Time { get; set; } = DateTimeOffset.Now;

        /// <summary>保存时的 AppData 源目录；恢复记录也沿用同一语义。</summary>
        public string SourceDirectory { get; set; } = string.Empty;

        /// <summary>保存时的目标储存根目录。</summary>
        public string TargetDirectory { get; set; } = string.Empty;

        /// <summary>压缩时为 .rar 或 .zip 完整路径；不压缩时为备份文件夹完整路径。</summary>
        public string BackupPath { get; set; } = string.Empty;

        public bool Compressed { get; set; }

        /// <summary>压缩时是否使用 ZIP 格式；仅 Compressed 为 true 时有意义。</summary>
        public bool UseZip { get; set; }

        public string? Sha256 { get; set; }
        public int CompressionLevel { get; set; }
        public bool KeepTimestamps { get; set; }
        public bool KeepFileAttributes { get; set; }

        /// <summary>
        /// 是否为 ZIP 归档。旧版 Data 没有 UseZip 时按 .zip 扩展名判断。
        /// </summary>
        public bool IsZipArchive => Compressed
            && (UseZip
                || string.Equals(Path.GetExtension(BackupPath), ".zip", StringComparison.OrdinalIgnoreCase));

        public string ArchiveFormatText => IsZipArchive ? "ZIP" : "RAR";

        /// <summary>恢复操作实际写入的目录；保存操作为空。</summary>
        public string? RestoreDestination { get; set; }

        public bool Success { get; set; } = true;
        public string? Error { get; set; }

        public string TypeText => Type == OperationType.Save ? "保存" : "恢复";

        public string CompressionLevelText => GetCompressionLevelText(CompressionLevel);

        public static string GetCompressionLevelText(int level)
            => CompressionLevels[Math.Clamp(level, 0, CompressionLevels.Length - 1)];

        /// <summary>
        /// 去重键：同一源目录、目标目录只保留最新一条操作记录。
        /// 恢复记录会继承保存记录中的备份路径、压缩和 SHA256 信息。
        /// </summary>
        public string Key => $"{SourceDirectory}|{TargetDirectory}";

        public OperationRecord Clone()
        {
            return new OperationRecord
            {
                Type = Type,
                Time = Time,
                SourceDirectory = SourceDirectory,
                TargetDirectory = TargetDirectory,
                BackupPath = BackupPath,
                Compressed = Compressed,
                UseZip = UseZip,
                Sha256 = Sha256,
                CompressionLevel = CompressionLevel,
                KeepTimestamps = KeepTimestamps,
                KeepFileAttributes = KeepFileAttributes,
                RestoreDestination = RestoreDestination,
                Success = Success,
                Error = Error,
            };
        }
    }

    /// <summary>
    /// 启动保存任务时从 MainWindow 传给 SaverWindow 的参数。
    /// </summary>
    internal sealed class SaveJobSettings
    {
        public Guid JobId { get; init; } = Guid.NewGuid();
        public string SourceDirectory { get; init; } = string.Empty;
        public string SourceName { get; init; } = string.Empty;
        public string TargetDirectory { get; init; } = string.Empty;
        public string BackupPath { get; init; } = string.Empty;
        public bool Compress { get; init; }
        public bool UseZip { get; init; }

        /// <summary>勾选“不测试压缩的文件”时为 true，保存时不传 -t。</summary>
        public bool SkipArchiveTest { get; init; }

        public int CompressionLevel { get; init; }
        public bool KeepTimestamps { get; init; }
        public bool KeepFileAttributes { get; init; }
        public string CacheRoot { get; init; } = string.Empty;
        public string? WinRarPath { get; init; }
    }

    /// <summary>
    /// 启动恢复任务时从 MainWindow 传给 RebuilderWindow 的参数。
    /// </summary>
    internal sealed class RestoreJobSettings
    {
        public Guid JobId { get; init; } = Guid.NewGuid();
        public OperationRecord SaveRecord { get; init; } = new();
        public string DestinationDirectory { get; init; } = string.Empty;
        public string CacheRoot { get; init; } = string.Empty;
        public string? RarPath { get; init; }
        public string? UnRarPath { get; init; }
    }

    internal readonly record struct OperationProgress(int OverallPercent, int PhasePercent, string Phase);
}
