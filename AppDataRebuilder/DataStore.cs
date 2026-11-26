using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AppDataRebuilder
{
    /// <summary>
    /// 程序目录下无后缀 Data 文件（UTF-8 XML）的读写封装。
    /// 保存目录历史和保存/恢复操作历史。
    /// </summary>
    internal sealed class DataStore
    {
        public const string FileName = "Data";

        private const string RootElementName = "AppDataRebuilder";
        private const string HistoryElementName = "History";
        private const string DirectoryElementName = "Directory";
        private const string OperationsElementName = "Operations";
        private const string OperationElementName = "Operation";
        private const string VersionAttributeName = "version";
        private const string CurrentVersion = "2";

        private readonly object _sync = new();
        private readonly List<string> _historyDirectories = new();
        private readonly List<OperationRecord> _operations = new();

        public DataStore(string? baseDirectory = null)
        {
            var directory = string.IsNullOrWhiteSpace(baseDirectory)
                ? Application.StartupPath
                : baseDirectory;

            FilePath = Path.Combine(directory!, FileName);
        }

        /// <summary>Data 文件的完整路径（不带扩展名）。</summary>
        public string FilePath { get; }

        /// <summary>历史存取目录，最新使用的排在前面。</summary>
        public IReadOnlyList<string> HistoryDirectories
        {
            get
            {
                lock (_sync)
                    return _historyDirectories.ToArray();
            }
        }

        /// <summary>保存/恢复操作历史，最新操作排在前面。</summary>
        public IReadOnlyList<OperationRecord> Operations
        {
            get
            {
                lock (_sync)
                    return _operations.Select(record => record.Clone()).ToArray();
            }
        }

        /// <summary>最近一次读取失败的原因；读取成功时为 null。</summary>
        public string? LastLoadError { get; private set; }

        /// <summary>最近一次 Load 调用前 Data 文件是否已经存在。</summary>
        public bool FileExistedBeforeLoad { get; private set; }

        public static string GetDefaultPath()
            => Path.Combine(Application.StartupPath, FileName);

        /// <summary>
        /// 读取 Data 文件；文件不存在、为空或格式损坏时会重新创建。
        /// </summary>
        public bool Load()
        {
            lock (_sync)
            {
                LastLoadError = null;
                _historyDirectories.Clear();
                _operations.Clear();
                FileExistedBeforeLoad = File.Exists(FilePath);

                if (!FileExistedBeforeLoad)
                {
                    SaveCore();
                    return true;
                }

                try
                {
                    string content;
                    using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
                    {
                        content = reader.ReadToEnd();
                    }

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        SaveCore();
                        return true;
                    }

                    var document = XDocument.Parse(content);
                    var root = document.Root;

                    var history = root?.Element(HistoryElementName);
                    if (history is not null)
                    {
                        var loadedHistory = new List<string>();
                        foreach (var element in history.Elements(DirectoryElementName))
                        {
                            var normalized = NormalizeDirectory(element.Value);
                            if (normalized is null)
                                continue;

                            if (!loadedHistory.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                                loadedHistory.Add(normalized);
                        }

                        _historyDirectories.AddRange(loadedHistory);
                    }

                    var operations = root?.Element(OperationsElementName);
                    if (operations is not null)
                    {
                        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var element in operations.Elements(OperationElementName))
                        {
                            var record = ParseOperation(element);
                            if (record is null)
                                continue;

                            // 文件按最新在前保存；同一去重键只保留第一条。
                            if (seenKeys.Add(record.Key))
                                _operations.Add(record);
                        }
                    }

                    return true;
                }
                catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    LastLoadError = ex.Message;
                    _historyDirectories.Clear();
                    _operations.Clear();

                    try
                    {
                        SaveCore();
                    }
                    catch
                    {
                        // 重建失败时保持内存中的空历史，由调用方提示用户。
                    }

                    return false;
                }
            }
        }

        /// <summary>
        /// 记录一个历史目录并立即写回 Data 文件。
        /// </summary>
        /// <returns>历史列表发生变化时返回 true。</returns>
        public bool AddHistory(string? path)
        {
            lock (_sync)
            {
                if (!AddHistoryCore(path))
                    return false;

                SaveCore();
                return true;
            }
        }

        /// <summary>
        /// 记录一次保存/恢复操作；同一类型、源/目标（恢复还含写入目录）只保留最新一条。
        /// </summary>
        public void RecordOperation(OperationRecord record)
        {
            lock (_sync)
            {
                AddHistoryCore(record.SourceDirectory);
                UpsertOperationCore(record.Clone());
                SaveCore();
            }
        }

        /// <summary>
        /// 查找指定源目录最近一条可用于恢复的记录。
        /// 保存记录必须成功；恢复记录保留原保存信息，即使恢复失败也可重试。
        /// </summary>
        public OperationRecord? GetLatestRestorableRecord(string sourceDirectory)
        {
            lock (_sync)
            {
                return _operations
                    .Where(record => !string.IsNullOrWhiteSpace(record.BackupPath)
                        && string.Equals(record.SourceDirectory, sourceDirectory, StringComparison.OrdinalIgnoreCase)
                        && (record.Type == OperationType.Restore || record.Success)
                        && (!record.Compressed || !string.IsNullOrWhiteSpace(record.Sha256)))
                    .OrderByDescending(record => record.Time)
                    .FirstOrDefault()
                    ?.Clone();
            }
        }

        /// <summary>
        /// 查找指定源目录最近一次保存尝试（无论成功失败），用于给出更准确的恢复提示。
        /// </summary>
        public OperationRecord? GetLatestSaveAttempt(string sourceDirectory)
        {
            lock (_sync)
            {
                return _operations
                    .Where(record => record.Type == OperationType.Save
                        && string.Equals(record.SourceDirectory, sourceDirectory, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(record => record.Time)
                    .FirstOrDefault()
                    ?.Clone();
            }
        }

        /// <summary>
        /// 将当前历史目录和操作历史以 UTF-8 XML 写回 Data 文件（无扩展名）。
        /// </summary>
        public void Save()
        {
            lock (_sync)
                SaveCore();
        }

        private void SaveCore()
        {
            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(
                    RootElementName,
                    new XAttribute(VersionAttributeName, CurrentVersion),
                    new XElement(
                        HistoryElementName,
                        _historyDirectories.Select(directory => new XElement(DirectoryElementName, directory))),
                    new XElement(
                        OperationsElementName,
                        _operations.Select(CreateOperationElement))));

            var directoryPath = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directoryPath))
                Directory.CreateDirectory(directoryPath);

            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\r\n",
                NewLineHandling = NewLineHandling.Replace,
            };

            using var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = XmlWriter.Create(stream, settings);
            document.Save(writer);
        }

        private static XElement CreateOperationElement(OperationRecord record)
        {
            return new XElement(
                OperationElementName,
                new XAttribute("type", record.Type == OperationType.Save ? "Save" : "Restore"),
                new XAttribute("time", record.Time.ToString("O", CultureInfo.InvariantCulture)),
                new XAttribute("source", record.SourceDirectory),
                new XAttribute("target", record.TargetDirectory),
                new XAttribute("backupPath", record.BackupPath),
                new XAttribute("compressed", record.Compressed ? "true" : "false"),
                new XAttribute("useZip", record.UseZip ? "true" : "false"),
                new XAttribute("sha256", record.Sha256 ?? string.Empty),
                new XAttribute("level", record.CompressionLevel.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("keepTimestamps", record.KeepTimestamps ? "true" : "false"),
                new XAttribute("keepFileAttributes", record.KeepFileAttributes ? "true" : "false"),
                new XAttribute("restoreDestination", record.RestoreDestination ?? string.Empty),
                new XAttribute("success", record.Success ? "true" : "false"),
                new XAttribute("error", record.Error ?? string.Empty));
        }

        private static OperationRecord? ParseOperation(XElement element)
        {
            var source = (string?)element.Attribute("source");
            var target = (string?)element.Attribute("target");
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return null;

            var type = string.Equals((string?)element.Attribute("type"), "Restore", StringComparison.OrdinalIgnoreCase)
                ? OperationType.Restore
                : OperationType.Save;

            var time = DateTimeOffset.MinValue;
            var timeText = (string?)element.Attribute("time");
            if (!string.IsNullOrWhiteSpace(timeText))
                DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time);

            var level = 0;
            var levelText = (string?)element.Attribute("level");
            if (!string.IsNullOrWhiteSpace(levelText))
                int.TryParse(levelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out level);

            var restoreDestination = (string?)element.Attribute("restoreDestination");

            return new OperationRecord
            {
                Type = type,
                Time = time,
                SourceDirectory = source,
                TargetDirectory = target,
                BackupPath = (string?)element.Attribute("backupPath") ?? string.Empty,
                Compressed = string.Equals((string?)element.Attribute("compressed"), "true", StringComparison.OrdinalIgnoreCase),
                UseZip = string.Equals((string?)element.Attribute("useZip"), "true", StringComparison.OrdinalIgnoreCase),
                Sha256 = NullIfEmpty((string?)element.Attribute("sha256")),
                CompressionLevel = Math.Clamp(level, 0, OperationRecord.CompressionLevels.Length - 1),
                KeepTimestamps = string.Equals((string?)element.Attribute("keepTimestamps"), "true", StringComparison.OrdinalIgnoreCase),
                KeepFileAttributes = string.Equals((string?)element.Attribute("keepFileAttributes"), "true", StringComparison.OrdinalIgnoreCase),
                RestoreDestination = NullIfEmpty(restoreDestination),
                Success = !string.Equals((string?)element.Attribute("success"), "false", StringComparison.OrdinalIgnoreCase),
                Error = NullIfEmpty((string?)element.Attribute("error")),
            };
        }

        private static string? NullIfEmpty(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value;

        private void UpsertOperationCore(OperationRecord record)
        {
            var index = _operations.FindIndex(
                existing => string.Equals(existing.Key, record.Key, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
                _operations.RemoveAt(index);

            _operations.Insert(0, record);
        }

        private bool AddHistoryCore(string? path)
        {
            var normalized = NormalizeDirectory(path);
            if (normalized is null)
                return false;

            var index = _historyDirectories.FindIndex(
                existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase));

            if (index == 0)
                return false;

            if (index > 0)
                _historyDirectories.RemoveAt(index);

            _historyDirectories.Insert(0, normalized);
            return true;
        }

        private static string? NormalizeDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
                var root = Path.GetPathRoot(fullPath);

                // 保留 "C:\" 这类根路径的结尾分隔符。
                if (!string.IsNullOrEmpty(root) && fullPath.Length > root.Length)
                    fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                return fullPath;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
            {
                return null;
            }
        }
    }
}
