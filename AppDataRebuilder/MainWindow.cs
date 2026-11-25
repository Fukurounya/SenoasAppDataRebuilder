using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace AppDataRebuilder
{
    public partial class MainWindow : Form
    {
        private const string HistorySourceText = "处理目录历史";
        private const string MatchedSourceText = "匹配到的目录";
        private const string RadarListName = "radarlist";
        private const string RadarListResourceName = "AppDataRebuilder.radarlist";
        private const string DataSnapshotName = "Data.txt";
        private const string TempProductFolderName = "Senoa";
        private const string TempWorkFolderName = "DataReBuilder";

        // 挡位为 1-6，对应 WinRAR 的 6 个压缩级别。
        private static readonly string[] CompassLevelTexts = OperationRecord.CompressionLevels;

        private static readonly char[] DirectorySeparators = { '\\', '/' };

        private readonly System.Windows.Forms.Timer _startupReadyTimer = new() { Interval = 5000 };
        private readonly System.Windows.Forms.Timer _transientStatusTimer = new() { Interval = 3500 };
        private readonly List<string> _matchedDirectories = new();
        private readonly List<string> _warningDetails = new();
        private readonly List<ActiveOperation> _activeOperations = new();
        private readonly List<BackgroundWorkItem> _workItems = new();
        private readonly List<ToolStripProgressBar> _dynamicWorkBars = new();

        private DataStore? _dataStore;
        private string? _tempWorkDirectory;
        private string? _snapshotPath;
        private string? _warningMessage;
        private string? _dataStoreError;
        private bool _dataFileMissing;
        private string? _selectedRestoreSourceDirectory;
        private CancellationTokenSource? _autoFindCancellation;
        private DirectorySource _directorySource = DirectorySource.History;
        private StatusMode _statusMode = StatusMode.Idle;
        private bool _saveAllowed;
        private bool _autoFindRunning;
        private bool _suppressComboEvents;
        private bool _suppressSourceTextChanged;
        private bool _statusBarHover;
        private bool _startupReadyVisible;
        private bool _awaitingDifferentDirectoryConfirmation;
        private bool _closing;
        private int _backgroundTaskCount;
        private string? _backgroundTaskMessage;

        private sealed class ActiveOperation
        {
            public Guid Id { get; init; }
            public string Description { get; init; } = string.Empty;
            public List<string> ReadPaths { get; init; } = new();
            public List<string> WritePaths { get; init; } = new();
        }

        private sealed class BackgroundWorkItem
        {
            public Guid Id { get; init; }
            public string Description { get; init; } = string.Empty;
            public string Target { get; init; } = string.Empty;
            public int HighPercent { get; set; }
            public bool Failed { get; set; }
            public ToolStripProgressBar? ProgressBar { get; set; }
        }

        /// <summary>检测到的 WinRAR 命令行工具路径，供保存窗口使用。</summary>
        public static string? WinRarPath { get; private set; }

        /// <summary>程序目录下 UnRAR 的路径，供恢复窗口使用。</summary>
        public static string? UnRarPath { get; private set; }

        /// <summary>WinRAR 可用时才允许压缩保存。</summary>
        public static bool CanSaveCompressed => WinRarPath is not null;

        private enum DirectorySource
        {
            History,
            Matched,
        }

        private enum StatusMode
        {
            Idle,
            Warning,
            Busy,
            Transient,
        }

        public MainWindow()
        {
            InitializeComponent();
            _startupReadyTimer.Tick += StartupReadyTimer_Tick;
            _transientStatusTimer.Tick += TransientStatusTimer_Tick;
            MainStatusBar.MouseEnter += StatusBar_MouseEnter;
            MainStatusBar.MouseLeave += StatusBar_MouseLeave;
        }

        private async void MainWindow_Load(object? sender, EventArgs e)
        {
            ConfigureStatusBar();
            InitializeCompressionLevel();
            InitializeTemporaryDirectory();

            BeginBackgroundTask("正在读取 Data 文件…");
            try
            {
                await Task.Run(LoadDataFile);
                if (_closing || IsDisposed)
                    return;

                ReportBackgroundProgress(100);
            }
            finally
            {
                EndBackgroundTask();
            }

            InitializeSourceSelector();

            // 工具检测完成前先禁用保存，避免误触发。
            StartSaveBT.Enabled = false;

            BeginBackgroundTask("正在检查 WinRAR 命令行工具…");
            try
            {
                var winRarPath = await Task.Run(RarToolLocator.FindWinRarPath);
                if (_closing || IsDisposed)
                    return;

                ApplyToolAvailability(winRarPath, RarToolLocator.FindUnRarPath(Application.StartupPath));
            }
            catch (Exception ex)
            {
                if (_closing || IsDisposed)
                    return;

                ApplyToolAvailability(null, RarToolLocator.FindUnRarPath(Application.StartupPath));
                _warningDetails.Add("检测 WinRAR 时出错：" + ex.Message);
                UpdateStatusToolTip();
            }
            finally
            {
                EndBackgroundTask();
            }

            // Data 文件异常只做一次性提示，之后仍保持空闲状态。
            if (!_closing && !IsDisposed)
            {
                if (_dataFileMissing)
                    ShowTemporaryStatus("Data 文件不存在，已建立新的 Data 文件", true, 6000);
                else if (!string.IsNullOrEmpty(_dataStoreError))
                    ShowTemporaryStatus("Data 文件异常，历史目录可能不可用", true, 6000);
                else
                    ShowStartupReadyForFiveSeconds();
            }
        }

        private void ConfigureStatusBar()
        {
            MainStatusBar.ShowItemToolTips = true;
            MainProgressBar.Minimum = 0;
            MainProgressBar.Maximum = 100;
            MainProgressBar.Style = ProgressBarStyle.Blocks;
            MainProgressBar.Value = 0;

            toolStripProgressBar1.Name = "FastestWorkProgress";
            toolStripProgressBar1.Minimum = 0;
            toolStripProgressBar1.Maximum = 100;
            toolStripProgressBar1.Style = ProgressBarStyle.Blocks;
            toolStripProgressBar1.Visible = false;

            FastestWorkPersent.Alignment = ToolStripItemAlignment.Right;
            FastestWorkPersent.Visible = false;
        }

        private void InitializeCompressionLevel()
        {
            CompassLv.Minimum = 0;
            CompassLv.Maximum = 5;
            CompassLv.SmallChange = 1;
            CompassLv.LargeChange = 1;
            CompassLv.TickFrequency = 1;
            CompassLv.Value = Math.Clamp(CompassLv.Value, CompassLv.Minimum, CompassLv.Maximum);
            UpdateCompressionLevelText();
        }

        private void InitializeTemporaryDirectory()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), TempProductFolderName);
            _tempWorkDirectory = Path.Combine(tempRoot, TempWorkFolderName);

            // 清理上次异常退出可能留下的快照目录，但不触碰 Senoa 下的其他内容。
            TryDeleteDirectory(_tempWorkDirectory);
        }

        private void LoadDataFile()
        {
            try
            {
                _dataStore = new DataStore(Application.StartupPath);
                _dataFileMissing = !File.Exists(_dataStore.FilePath);

                if (!_dataStore.Load())
                {
                    _dataStoreError = string.IsNullOrWhiteSpace(_dataStore.LastLoadError)
                        ? "Data 文件格式不正确，已重新创建"
                        : "Data 文件格式不正确，已重新创建：" + _dataStore.LastLoadError;
                }
            }
            catch (Exception ex)
            {
                _dataStore = null;
                _dataStoreError = "无法读取或创建 Data 文件：" + ex.Message;
            }
        }

        private void InitializeSourceSelector()
        {
            _suppressComboEvents = true;
            try
            {
                ComboBoxSelect.Items.Clear();
                ComboBoxSelect.Items.Add(HistorySourceText);
                ComboBoxSelect.SelectedIndex = 0;
            }
            finally
            {
                _suppressComboEvents = false;
            }

            ShowDirectorySource(DirectorySource.History);
        }

        private void ApplyToolAvailability(string? winRarPath, string? unRarPath)
        {
            WinRarPath = winRarPath;
            UnRarPath = unRarPath;
            _saveAllowed = winRarPath is not null;

            // 功能与按钮同时禁用；点击事件里还会再次校验。
            StartSaveBT.Enabled = _saveAllowed;

            _warningMessage = null;
            _warningDetails.Clear();

            if (winRarPath is null)
            {
                _warningMessage = unRarPath is not null
                    ? "未找到 WinRAR，已使用 UnRAR，保存功能已禁用"
                    : "未找到 WinRAR / UnRAR，保存与解压均不可用";
            }
            else
            {
                _warningDetails.Add("WinRAR 命令行：" + winRarPath);
            }

            UpdateStatusToolTip();
            RestoreBaseStatus();
        }

        private void UpdateStatusToolTip()
        {
            var details = new List<string>
            {
                "Data 文件：" + (_dataStore?.FilePath ?? DataStore.GetDefaultPath()),
                "radarlist：" + GetRadarListSourceDescription(),
                "WinRAR：" + (WinRarPath ?? "未找到"),
                "UnRAR：" + (UnRarPath ?? "未找到"),
                "开始保存：" + (_saveAllowed ? "可用" : "已禁用"),
            };

            if (!string.IsNullOrEmpty(_dataStoreError))
                details.Add(_dataStoreError);

            details.AddRange(_warningDetails);
            MainStatusLabel.ToolTipText = string.Join(Environment.NewLine, details);
        }

        private void RestoreBaseStatus()
        {
            if (_closing || IsDisposed)
                return;

            _transientStatusTimer.Stop();
            _startupReadyTimer.Stop();
            _startupReadyVisible = false;

            if (_backgroundTaskCount > 0)
            {
                ShowBusyStatus();
                return;
            }

            if (_workItems.Count > 0)
            {
                ShowWorkStatus();
                return;
            }

            if (!string.IsNullOrEmpty(_warningMessage))
            {
                ShowWarningStatus();
                return;
            }

            _statusMode = StatusMode.Idle;
            ShowIdleStatus(_statusBarHover);
        }

        private void ShowBusyStatus()
        {
            if (_closing || IsDisposed)
                return;

            _statusMode = StatusMode.Busy;
            MainStatusLabel.ForeColor = SystemColors.ControlText;

            var text = string.IsNullOrWhiteSpace(_backgroundTaskMessage)
                ? "后台任务运行中"
                : _backgroundTaskMessage;

            if (_workItems.Count > 0)
            {
                var failedCount = _workItems.Count(item => item.Failed);
                text += failedCount > 0
                    ? $"（{_workItems.Count} 个任务，{failedCount} 失败）"
                    : $"（{_workItems.Count} 个任务）";
            }

            MainStatusLabel.Text = text;
        }

        private void ShowWorkStatus()
        {
            if (_closing || IsDisposed)
                return;

            _startupReadyTimer.Stop();
            _startupReadyVisible = false;
            _transientStatusTimer.Stop();

            var running = _workItems.Where(item => !item.Failed).ToList();
            var failedCount = _workItems.Count - running.Count;

            UpdateWorkItemVisuals();

            if (running.Count > 0)
            {
                _statusMode = StatusMode.Busy;
                MainStatusLabel.ForeColor = SystemColors.ControlText;

                if (running.Count == 1)
                {
                    MainStatusLabel.Text = $"1 个后台任务：{running[0].Description}"
                        + (failedCount > 0 ? $"（{failedCount} 个失败）" : string.Empty);
                }
                else
                {
                    MainStatusLabel.Text = $"{running.Count} 个后台任务运行中"
                        + (failedCount > 0 ? $"（{failedCount} 个失败）" : string.Empty);
                }

                MainProgressBar.Style = ProgressBarStyle.Blocks;
                MainProgressBar.Value = 0;
                return;
            }

            _statusMode = StatusMode.Warning;
            MainStatusLabel.ForeColor = Color.Firebrick;
            MainStatusLabel.Text = failedCount == 1
                ? "1 个任务失败，等待关闭"
                : $"{failedCount} 个任务失败，等待关闭";
            MainProgressBar.Style = ProgressBarStyle.Blocks;
            MainProgressBar.Value = 0;
        }

        private void RefreshWorkStatus()
        {
            if (_closing || IsDisposed)
                return;

            if (_statusMode == StatusMode.Transient)
                return;

            if (_backgroundTaskCount > 0)
            {
                ShowBusyStatus();
                return;
            }

            if (_workItems.Count > 0)
            {
                ShowWorkStatus();
                return;
            }

            RestoreBaseStatus();
        }

        internal Guid RegisterWorkItem(string description, string target)
        {
            var item = new BackgroundWorkItem
            {
                Id = Guid.NewGuid(),
                Description = description,
                Target = target,
            };

            _workItems.Add(item);
            RebuildWorkBars();
            RefreshWorkStatus();

            return item.Id;
        }

        internal void ReportWorkItem(Guid workItemId, int highPercent)
        {
            var item = _workItems.FirstOrDefault(work => work.Id == workItemId);
            if (item is null || item.Failed)
                return;

            item.HighPercent = Math.Clamp(highPercent, 0, 100);
            UpdateWorkItemVisuals();
        }

        internal void CompleteWorkItem(Guid workItemId, bool success)
        {
            var item = _workItems.FirstOrDefault(work => work.Id == workItemId);
            if (item is null)
                return;

            if (success)
            {
                RemoveWorkItem(workItemId);
                return;
            }

            item.Failed = true;
            item.HighPercent = 100;
            UpdateWorkItemVisuals();
            RefreshWorkStatus();
        }

        internal void RemoveWorkItem(Guid workItemId)
        {
            var index = _workItems.FindIndex(work => work.Id == workItemId);
            if (index < 0)
                return;

            _workItems.RemoveAt(index);
            RebuildWorkBars();
            RefreshWorkStatus();
        }

        private void RebuildWorkBars()
        {
            if (IsDisposed)
                return;

            foreach (var bar in _dynamicWorkBars)
            {
                MainStatusBar.Items.Remove(bar);
                bar.Dispose();
            }

            _dynamicWorkBars.Clear();

            foreach (var item in _workItems)
                item.ProgressBar = null;

            if (_workItems.Count == 0)
            {
                toolStripProgressBar1.Visible = false;
                FastestWorkPersent.Visible = false;
                MainStatusBar.PerformLayout();
                return;
            }

            var multiple = _workItems.Count > 1;
            var barWidth = _workItems.Count == 1
                ? 160
                : (_workItems.Count <= 3 ? 56 : 42);

            for (var index = 0; index < _workItems.Count; index++)
            {
                ToolStripProgressBar bar;

                if (index == 0)
                {
                    bar = toolStripProgressBar1;
                    bar.Name = "FastestWorkProgress";
                }
                else
                {
                    bar = new ToolStripProgressBar
                    {
                        Name = $"FastestWorkProgress{index + 1}",
                        Alignment = ToolStripItemAlignment.Right,
                        AutoSize = false,
                        Visible = true,
                    };

                    MainStatusBar.Items.Add(bar);
                    _dynamicWorkBars.Add(bar);
                }

                bar.Alignment = ToolStripItemAlignment.Right;
                bar.Minimum = 0;
                bar.Maximum = 100;
                bar.Style = ProgressBarStyle.Blocks;
                bar.Size = new Size(barWidth, 16);
                bar.Visible = true;
                _workItems[index].ProgressBar = bar;
            }

            FastestWorkPersent.Alignment = ToolStripItemAlignment.Right;
            FastestWorkPersent.Visible = !multiple;

            UpdateWorkItemVisuals();
            MainStatusBar.PerformLayout();
        }

        private void UpdateWorkItemVisuals()
        {
            if (IsDisposed)
                return;

            foreach (var item in _workItems)
            {
                var bar = item.ProgressBar;
                if (bar is null)
                    continue;

                bar.Value = item.Failed ? 100 : item.HighPercent;
                bar.ForeColor = item.Failed ? Color.Red : Color.Empty;
                bar.ToolTipText = BuildWorkToolTip(item);
            }

            if (_workItems.Count == 1)
            {
                var item = _workItems[0];
                FastestWorkPersent.Text = item.Failed ? "失败 100%" : $"{item.HighPercent}%";
                FastestWorkPersent.ForeColor = item.Failed ? Color.Firebrick : SystemColors.ControlText;
                FastestWorkPersent.ToolTipText = BuildWorkToolTip(item);
                FastestWorkPersent.Visible = true;
            }
            else
            {
                FastestWorkPersent.Visible = false;
            }
        }

        private static string BuildWorkToolTip(BackgroundWorkItem item)
        {
            var status = item.Failed
                ? "状态：失败（100%）"
                : $"进度：{item.HighPercent}%";

            return $"{item.Description}{Environment.NewLine}目标：{item.Target}{Environment.NewLine}{status}";
        }

        private void ShowWarningStatus()
        {
            _statusMode = StatusMode.Warning;
            MainStatusLabel.ForeColor = Color.Firebrick;
            MainStatusLabel.Text = _warningMessage ?? string.Empty;
            MainProgressBar.Style = ProgressBarStyle.Blocks;
            MainProgressBar.Value = 0;
        }

        private void ShowIdleStatus(bool showReady)
        {
            MainStatusLabel.ForeColor = showReady ? Color.ForestGreen : SystemColors.ControlText;
            MainStatusLabel.Text = showReady ? "就绪" : "后台无进程";
            MainProgressBar.Style = ProgressBarStyle.Blocks;
            MainProgressBar.Value = showReady ? 100 : 0;
        }

        /// <summary>
        /// 启动加载完成后显示 5 秒“就绪”，之后只在空闲悬停时显示。
        /// </summary>
        private void ShowStartupReadyForFiveSeconds()
        {
            if (_closing || IsDisposed)
                return;

            if (_backgroundTaskCount > 0 || _workItems.Count > 0)
            {
                RestoreBaseStatus();
                return;
            }

            if (!string.IsNullOrEmpty(_warningMessage))
            {
                ShowWarningStatus();
                return;
            }

            _statusMode = StatusMode.Idle;
            _startupReadyVisible = true;
            ShowIdleStatus(true);

            _startupReadyTimer.Stop();
            _startupReadyTimer.Interval = 5000;
            _startupReadyTimer.Start();
        }

        private void StartupReadyTimer_Tick(object? sender, EventArgs e)
        {
            _startupReadyTimer.Stop();
            _startupReadyVisible = false;

            if (_statusMode != StatusMode.Idle)
                return;

            ShowIdleStatus(_statusBarHover);
        }

        private void StatusBar_MouseEnter(object? sender, EventArgs e)
        {
            _statusBarHover = true;

            if (_statusMode == StatusMode.Idle)
                ShowIdleStatus(true);
        }

        private void StatusBar_MouseLeave(object? sender, EventArgs e)
        {
            _statusBarHover = false;

            if (_statusMode == StatusMode.Idle && !_startupReadyVisible)
                ShowIdleStatus(false);
        }

        private void TransientStatusTimer_Tick(object? sender, EventArgs e)
        {
            _transientStatusTimer.Stop();
            RestoreBaseStatus();
        }

        /// <summary>
        /// 标记核心后台任务开始运行，MainProgressBar 切换为滚动指示。
        /// </summary>
        public void BeginBackgroundTask(string message)
        {
            if (_closing || IsDisposed)
                return;

            _backgroundTaskCount++;
            _backgroundTaskMessage = message;
            _statusMode = StatusMode.Busy;
            _startupReadyTimer.Stop();
            _startupReadyVisible = false;
            _transientStatusTimer.Stop();
            MainProgressBar.Style = ProgressBarStyle.Marquee;
            ShowBusyStatus();
        }

        /// <summary>
        /// 更新核心后台任务的确定进度（0-100）。
        /// </summary>
        public void ReportBackgroundProgress(int percent)
        {
            if (_closing || IsDisposed || _backgroundTaskCount <= 0)
                return;

            MainProgressBar.Style = ProgressBarStyle.Blocks;
            MainProgressBar.Value = Math.Clamp(percent, 0, 100);
        }

        /// <summary>
        /// 结束核心后台任务，恢复空闲状态；有后台进程期间不会显示“就绪”。
        /// </summary>
        public void EndBackgroundTask()
        {
            if (_closing || IsDisposed)
                return;

            if (_backgroundTaskCount > 0)
                _backgroundTaskCount--;

            if (_backgroundTaskCount > 0)
            {
                _backgroundTaskMessage = $"后台任务运行中（{_backgroundTaskCount}）";
                MainProgressBar.Style = ProgressBarStyle.Marquee;
                ShowBusyStatus();
                return;
            }

            MainProgressBar.Style = ProgressBarStyle.Blocks;
            MainProgressBar.Value = 0;
            RestoreBaseStatus();
        }

        /// <summary>
        /// 临时显示一条状态信息，超时后恢复基础状态。
        /// </summary>
        public void ShowTemporaryStatus(string message, bool isWarning = false, int milliseconds = 3500)
        {
            if (_closing || IsDisposed || string.IsNullOrWhiteSpace(message))
                return;

            _statusMode = StatusMode.Transient;
            _startupReadyTimer.Stop();
            _startupReadyVisible = false;
            MainStatusLabel.ForeColor = isWarning ? Color.Firebrick : SystemColors.ControlText;
            MainStatusLabel.Text = message;
            MainProgressBar.Style = ProgressBarStyle.Blocks;
            MainProgressBar.Value = 0;

            _transientStatusTimer.Stop();
            _transientStatusTimer.Interval = Math.Max(1000, milliseconds);
            _transientStatusTimer.Start();
        }

        private void SDSelectBT_Click(object? sender, EventArgs e)
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
                userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;

            var appDataDirectory = string.IsNullOrWhiteSpace(userProfile)
                ? string.Empty
                : Path.Combine(userProfile, "AppData");

            if (string.IsNullOrEmpty(appDataDirectory) || !Directory.Exists(appDataDirectory))
            {
                ShowTemporaryStatus("找不到当前用户的程序数据文件夹", true, 5000);
                return;
            }

            var selectedDirectory = PickFolder("选择 AppData 目录", appDataDirectory);
            if (selectedDirectory is null)
                return;

            FillSourceDirectory(selectedDirectory, matchRestoreRecord: true);
            RememberDirectory(selectedDirectory);
        }

        private void TDSelectBT_Click(object? sender, EventArgs e)
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrWhiteSpace(programFiles) || !Directory.Exists(programFiles))
            {
                programFiles = @"C:\Program Files";
                if (!Directory.Exists(programFiles))
                {
                    var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
                    programFiles = string.IsNullOrEmpty(systemDrive) ? @"C:\" : systemDrive;
                }
            }

            var currentTarget = textBox2.Text.Trim();
            var initialDirectory = Directory.Exists(currentTarget)
                ? currentTarget
                : File.Exists(currentTarget)
                    ? Path.GetDirectoryName(currentTarget) ?? programFiles
                    : programFiles;

            var selectedDirectory = PickFolder(
                "选择目标储存目录（压缩文件由 Data 自动定位）",
                initialDirectory);

            if (selectedDirectory is null)
                return;

            textBox2.Text = selectedDirectory;
        }

        private string? PickFolder(string description, string initialDirectory)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = description,
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(initialDirectory)
                    ? initialDirectory
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };

            return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
        }

        private void FillSourceDirectory(
            string directory,
            bool matchRestoreRecord,
            bool fillTargetFromRecord = false)
        {
            _suppressSourceTextChanged = true;
            try
            {
                SDinputbox.Text = directory;
            }
            finally
            {
                _suppressSourceTextChanged = false;
            }

            if (matchRestoreRecord)
                _selectedRestoreSourceDirectory = directory;

            ResetRestoreConfirmation();

            var record = matchRestoreRecord
                ? _dataStore?.GetLatestRestorableRecord(directory)
                : null;

            if (record is not null
                && (fillTargetFromRecord || string.IsNullOrWhiteSpace(textBox2.Text))
                && !string.IsNullOrWhiteSpace(record.TargetDirectory))
            {
                // 回填该记录对应的目标储存目录，便于看到 .rar 所在的文件夹。
                textBox2.Text = record.TargetDirectory;
            }

            if (!Directory.Exists(directory))
            {
                // 允许选择已删除的历史目录；恢复时 BackupService 会重新创建目标目录。
                ShowTemporaryStatus(
                    record is not null
                        ? "该目录已不存在，已匹配保存记录；恢复时会自动创建"
                        : "该目录已不存在，没有找到对应保存记录",
                    true,
                    5000);
            }
            else if (record is not null)
            {
                ShowTemporaryStatus(
                    $"已匹配记录（{record.Time:yyyy-MM-dd HH:mm}）",
                    false,
                    4000);
            }
        }

        private void SDinputbox_TextChanged(object? sender, EventArgs e)
        {
            if (_suppressSourceTextChanged)
                return;

            ResetRestoreConfirmation();
        }

        private void ResetRestoreConfirmation()
        {
            _awaitingDifferentDirectoryConfirmation = false;

            if (!string.Equals(StartRebuildBT.Text, "开始恢复", StringComparison.Ordinal))
                StartRebuildBT.Text = "开始恢复";
        }

        private void RememberDirectory(string directory)
        {
            if (_dataStore is null || string.IsNullOrWhiteSpace(directory))
                return;

            try
            {
                if (!_dataStore.AddHistory(directory))
                    return;

                if (_directorySource == DirectorySource.History)
                    ShowDirectorySource(DirectorySource.History);
            }
            catch (Exception ex)
            {
                _dataStoreError = "历史目录写入失败：" + ex.Message;
                UpdateStatusToolTip();
                ShowTemporaryStatus("历史目录写入失败，未能更新 Data 文件", true, 5000);
            }
        }

        private void ShowDirectorySource(DirectorySource source)
        {
            _directorySource = source;
            var directories = GetSourceDirectories(source);

            _suppressComboEvents = true;
            try
            {
                SDAutoFindComboBox.Items.Clear();
                if (directories.Count > 0)
                    SDAutoFindComboBox.Items.AddRange(directories.Cast<object>().ToArray());

                SDAutoFindComboBox.Text = string.Empty;

                var autoComplete = new AutoCompleteStringCollection();
                autoComplete.AddRange(directories.ToArray());
                SDAutoFindComboBox.AutoCompleteCustomSource = autoComplete;
                SDAutoFindComboBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                SDAutoFindComboBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
            }
            finally
            {
                _suppressComboEvents = false;
            }
        }

        private List<string> GetSourceDirectories(DirectorySource source)
        {
            if (source == DirectorySource.Matched)
                return new List<string>(_matchedDirectories);

            if (_dataStore is null)
                return new List<string>();

            // 目录历史与 Data 操作记录合并，避免历史区缺失但操作记录仍在时无法回填。
            var directories = new List<string>();
            directories.AddRange(_dataStore.HistoryDirectories);

            foreach (var record in _dataStore.Operations)
            {
                if (!string.IsNullOrWhiteSpace(record.SourceDirectory))
                    directories.Add(record.SourceDirectory);
            }

            return directories
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool IsMatchedSourceSelected()
            => string.Equals(ComboBoxSelect.SelectedItem as string, MatchedSourceText, StringComparison.Ordinal);

        private void EnsureMatchedSourceItem()
        {
            if (!ComboBoxSelect.Items.Contains(MatchedSourceText))
                ComboBoxSelect.Items.Add(MatchedSourceText);
        }

        private void ComboBoxSelect_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_suppressComboEvents || _closing)
                return;

            ShowDirectorySource(IsMatchedSourceSelected() ? DirectorySource.Matched : DirectorySource.History);
        }

        private void SDAutoFindComboBox_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_suppressComboEvents || _closing)
                return;

            if (SDAutoFindComboBox.SelectedItem is not string directory)
                return;

            FillSourceDirectory(directory, matchRestoreRecord: true, fillTargetFromRecord: true);

            // 从“匹配到的目录”里选中项时，同时把它加入历史目录。
            if (_directorySource == DirectorySource.Matched)
                RememberDirectory(directory);
        }

        private void SDAutoFindComboBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter)
                return;

            e.SuppressKeyPress = true;

            var query = SDAutoFindComboBox.Text.Trim();
            if (query.Length == 0)
                return;

            var match = GetSourceDirectories(_directorySource)
                .FirstOrDefault(directory => directory.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

            if (match is not null)
            {
                _suppressComboEvents = true;
                try
                {
                    SDAutoFindComboBox.SelectedItem = match;
                }
                finally
                {
                    _suppressComboEvents = false;
                }

                FillSourceDirectory(match, matchRestoreRecord: true, fillTargetFromRecord: true);

                if (_directorySource == DirectorySource.Matched)
                    RememberDirectory(match);

                return;
            }

            if (Directory.Exists(query))
            {
                FillSourceDirectory(query, matchRestoreRecord: true);
                RememberDirectory(query);
                return;
            }

            ShowTemporaryStatus("历史目录中没有匹配项", true, 5000);
        }

        private async void SDAutoFind_Click(object? sender, EventArgs e)
        {
            if (_autoFindRunning || _closing)
                return;

            string[] entries;
            BeginBackgroundTask("正在读取 radarlist…");
            try
            {
                entries = ReadRadarListEntries();
                ReportBackgroundProgress(100);
            }
            catch (FileNotFoundException)
            {
                EndBackgroundTask();
                ShowTemporaryStatus("找不到 radarlist 文件", true, 5000);
                return;
            }
            catch (Exception ex)
            {
                EndBackgroundTask();
                ShowTemporaryStatus("读取 radarlist 失败：" + ex.Message, true, 6000);
                return;
            }

            if (entries.Length == 0)
            {
                EndBackgroundTask();
                ShowTemporaryStatus("radarlist 中没有记录目录名", true, 5000);
                return;
            }

            _autoFindRunning = true;
            SDAutoFind.Enabled = false;
            _autoFindCancellation = new CancellationTokenSource();
            var cancellationToken = _autoFindCancellation.Token;

            string? resultMessage = null;
            var isWarning = false;

            _backgroundTaskMessage = "正在自动寻找匹配的目录…";
            MainProgressBar.Style = ProgressBarStyle.Marquee;
            ShowBusyStatus();
            try
            {
                var roots = GetRadarSearchRoots();
                var matches = await Task.Run(
                    () => FindRadarDirectories(roots, entries, cancellationToken),
                    cancellationToken);

                if (_closing)
                    return;

                _matchedDirectories.Clear();
                _matchedDirectories.AddRange(matches);
                EnsureMatchedSourceItem();

                _suppressComboEvents = true;
                try
                {
                    ComboBoxSelect.SelectedItem = MatchedSourceText;
                }
                finally
                {
                    _suppressComboEvents = false;
                }

                ShowDirectorySource(DirectorySource.Matched);

                resultMessage = matches.Count == 0
                    ? "没有找到匹配的目录"
                    : $"找到 {matches.Count} 个匹配目录";
            }
            catch (OperationCanceledException)
            {
                // 窗口关闭或任务被取消，无需提示。
            }
            catch (Exception ex)
            {
                resultMessage = "自动寻找失败：" + ex.Message;
                isWarning = true;
            }
            finally
            {
                _autoFindCancellation?.Dispose();
                _autoFindCancellation = null;
                _autoFindRunning = false;

                if (!_closing && !IsDisposed)
                {
                    SDAutoFind.Enabled = true;
                    EndBackgroundTask();
                }
            }

            if (!_closing && !IsDisposed && resultMessage is not null)
                ShowTemporaryStatus(resultMessage, isWarning, 5000);
        }

        private static string? ResolveRadarListPath()
        {
            foreach (var fileName in new[] { RadarListName, RadarListName + ".txt" })
            {
                var path = Path.Combine(Application.StartupPath, fileName);
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        private static Stream? OpenRadarListStream()
        {
            var embeddedStream = typeof(MainWindow).Assembly.GetManifestResourceStream(RadarListResourceName);
            if (embeddedStream is not null)
                return embeddedStream;

            var externalPath = ResolveRadarListPath();
            if (externalPath is null)
                return null;

            return new FileStream(externalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        private static string GetRadarListSourceDescription()
        {
            using var embeddedStream = typeof(MainWindow).Assembly.GetManifestResourceStream(RadarListResourceName);
            if (embeddedStream is not null)
                return "嵌入资源 " + RadarListResourceName;

            return ResolveRadarListPath() ?? "未找到";
        }

        private static string[] ReadRadarListEntries()
        {
            var entries = new List<string>();

            using var stream = OpenRadarListStream()
                ?? throw new FileNotFoundException("找不到 radarlist 嵌入资源或程序目录文件");
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var entry = line.Trim();
                if (entry.Length == 0)
                    continue;

                if (entry.StartsWith('#') || entry.StartsWith("//", StringComparison.Ordinal))
                    continue;

                entry = entry.Trim('"').Trim().TrimEnd('\\', '/');
                if (entry.Length == 0)
                    continue;

                if (!entries.Contains(entry, StringComparer.OrdinalIgnoreCase))
                    entries.Add(entry);
            }

            return entries.ToArray();
        }

        private static string[] GetRadarSearchRoots()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(userProfile))
                userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(userProfile))
                return Array.Empty<string>();

            var appDataDirectory = Path.Combine(userProfile, "AppData");

            return new[]
            {
                Path.Combine(appDataDirectory, "LocalLow"),
                Path.Combine(appDataDirectory, "Roaming"),
            };
        }

        private static List<string> FindRadarDirectories(
            IReadOnlyList<string> rootDirectories,
            IReadOnlyList<string> entries,
            CancellationToken cancellationToken)
        {
            var matches = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in rootDirectories)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    continue;

                var pending = new Stack<string>();
                pending.Push(root);

                while (pending.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = pending.Pop();

                    try
                    {
                        foreach (var directory in Directory.EnumerateDirectories(current))
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var folderName = Path.GetFileName(directory);
                            if (IsRadarMatch(directory, folderName, entries) && seen.Add(directory))
                                matches.Add(directory);

                            FileAttributes attributes;
                            try
                            {
                                attributes = File.GetAttributes(directory);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                continue;
                            }

                            // 不进入符号链接/联接点，避免目录循环。
                            if ((attributes & FileAttributes.ReparsePoint) == 0)
                                pending.Push(directory);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // 当前目录无访问权限，跳过并继续搜索。
                    }
                    catch (IOException)
                    {
                        // 目录在枚举过程中消失或不可读，跳过。
                    }
                    catch (System.Security.SecurityException)
                    {
                        // 安全策略拒绝访问，跳过。
                    }
                }
            }

            matches.Sort(StringComparer.OrdinalIgnoreCase);
            return matches;
        }

        private static bool IsRadarMatch(string fullPath, string folderName, IReadOnlyList<string> entries)
        {
            foreach (var entry in entries)
            {
                if (entry.IndexOfAny(DirectorySeparators) >= 0)
                {
                    // radarlist 中记录的是相对路径时，按路径结尾匹配。
                    var relativePath = entry.Replace('/', '\\').Trim('\\');
                    if (relativePath.Length > 0
                        && fullPath.EndsWith("\\" + relativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                else if (string.Equals(folderName, entry, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void CompassLv_ValueChanged(object? sender, EventArgs e)
        {
            UpdateCompressionLevelText();
        }

        private void FilestampOF_CheckedChanged(object? sender, EventArgs e)
        {
            if (FilestampOF.Checked)
                TimestampOF.Checked = true;
        }

        private void TimestampOF_CheckedChanged(object? sender, EventArgs e)
        {
            // 保留完整文件属性依赖时间戳，禁止单独取消时间戳。
            if (!TimestampOF.Checked && FilestampOF.Checked)
                TimestampOF.Checked = true;
        }

        private void UpdateCompressionLevelText()
        {
            if (CompassLvText is null)
                return;

            var index = Math.Clamp(CompassLv.Value, 0, CompassLevelTexts.Length - 1);
            CompassLvText.Text = CompassLevelTexts[index];
        }

        private async void DataFileOpenBT_Click(object? sender, EventArgs e)
        {
            try
            {
                var snapshotPath = await PrepareDataSnapshotAsync();
                if (snapshotPath is null)
                {
                    if (!_closing && !IsDisposed)
                        ShowTemporaryStatus("找不到 Data 文件，无法显示数据快照", true, 5000);
                    return;
                }

                if (_closing || IsDisposed)
                    return;

                try
                {
                    Process.Start(new ProcessStartInfo(snapshotPath) { UseShellExecute = true });
                }
                catch (Win32Exception)
                {
                    // 系统没有 .txt 关联程序时退回记事本。
                    Process.Start(new ProcessStartInfo("notepad.exe")
                    {
                        Arguments = "\"" + snapshotPath + "\"",
                        UseShellExecute = true,
                    });
                }

                ShowTemporaryStatus("正在显示数据快照", false, 5000);
            }
            catch (Exception ex)
            {
                ShowTemporaryStatus("打开数据快照失败：" + ex.Message, true, 6000);
            }
        }

        private async Task<string?> PrepareDataSnapshotAsync()
        {
            var dataPath = _dataStore?.FilePath ?? DataStore.GetDefaultPath();

            BeginBackgroundTask("正在复制数据快照…");
            try
            {
                ReportBackgroundProgress(10);

                // 尽量把内存中的最新历史写入磁盘后再复制。
                if (_dataStore is not null)
                {
                    try
                    {
                        _dataStore.Save();
                    }
                    catch
                    {
                        // 写入失败时仍然复制磁盘上已有的 Data 文件。
                    }
                }

                if (!File.Exists(dataPath) || string.IsNullOrEmpty(_tempWorkDirectory))
                    return null;

                Directory.CreateDirectory(_tempWorkDirectory);
                CleanupSnapshotFile();

                var snapshotPath = Path.Combine(_tempWorkDirectory, DataSnapshotName);
                var copyProgress = new Progress<int>(
                    value => ReportBackgroundProgress(20 + value * 80 / 100));

                await FileSystemUtility.CopyFileWithProgressAsync(
                    dataPath,
                    snapshotPath,
                    copyProgress,
                    CancellationToken.None);

                if (_closing || IsDisposed)
                {
                    try
                    {
                        File.Delete(snapshotPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 关闭时由临时目录清理。
                    }

                    return null;
                }

                _snapshotPath = snapshotPath;
                ReportBackgroundProgress(100);
                return snapshotPath;
            }
            finally
            {
                EndBackgroundTask();
            }
        }

        private async void StartSaveBT_Click(object? sender, EventArgs e)
        {
            if (!_saveAllowed)
            {
                ShowTemporaryStatus("未找到 WinRAR 命令行工具，无法开始保存", true, 5000);
                return;
            }

            if (!TryValidateSourceDirectory() || !TryValidateTargetDirectory())
                return;

            string sourceDirectory;
            string targetDirectory;
            try
            {
                sourceDirectory = FileSystemUtility.NormalizePath(SDinputbox.Text);
                targetDirectory = FileSystemUtility.NormalizePath(textBox2.Text);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                ShowTemporaryStatus("目录路径无效：" + ex.Message, true, 6000);
                return;
            }

            if (FileSystemUtility.PathsOverlap(sourceDirectory, targetDirectory))
            {
                ShowTemporaryStatus("源目录与目标储存目录不能重叠", true, 6000);
                return;
            }

            var sourceName = new DirectoryInfo(sourceDirectory).Name;
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                ShowTemporaryStatus("无法确定源目录名称", true, 6000);
                return;
            }

            var compress = CompressOF.Checked;
            var backupPath = compress
                ? Path.Combine(targetDirectory, sourceName + ".rar")
                : Path.Combine(targetDirectory, sourceName);

            if (FileSystemUtility.PathsOverlap(backupPath, sourceDirectory))
            {
                ShowTemporaryStatus("备份路径不能位于源目录内", true, 6000);
                return;
            }

            if (compress && Directory.Exists(backupPath))
            {
                ShowTemporaryStatus("目标目录中已存在同名文件夹，无法创建压缩文件", true, 6000);
                return;
            }

            if (!compress && File.Exists(backupPath))
            {
                ShowTemporaryStatus("目标目录中已存在同名文件，无法创建备份文件夹", true, 6000);
                return;
            }

            var operationId = RegisterOperation(
                $"保存 {sourceName}",
                new[] { sourceDirectory },
                new[] { backupPath },
                out var conflictMessage);

            if (operationId is null)
            {
                ShowTemporaryStatus(conflictMessage ?? "任务路径冲突", true, 6000);
                return;
            }

            RememberDirectory(sourceDirectory);

            var settings = new SaveJobSettings
            {
                JobId = Guid.NewGuid(),
                SourceDirectory = sourceDirectory,
                SourceName = sourceName,
                TargetDirectory = targetDirectory,
                BackupPath = backupPath,
                Compress = compress,
                CompressionLevel = CompassLv.Value,
                KeepTimestamps = TimestampOF.Checked || FilestampOF.Checked,
                KeepFileAttributes = FilestampOF.Checked,
                CacheRoot = GetCacheRoot(),
                WinRarPath = WinRarPath,
            };

            string? openError = null;

            BeginBackgroundTask("正在打开保存窗口…");
            ReportBackgroundProgress(0);

            try
            {
                await Task.Yield();

                if (_closing || IsDisposed)
                    return;

                var window = new SaverWindow(this, settings, operationId.Value);
                window.Show(this);
                ReportBackgroundProgress(100);
            }
            catch (Exception ex)
            {
                UnregisterOperation(operationId.Value);
                openError = "无法打开保存窗口：" + ex.Message;
            }
            finally
            {
                EndBackgroundTask();
            }

            if (openError is not null && !_closing && !IsDisposed)
                ShowTemporaryStatus(openError, true, 6000);
        }

        private async void StartRebuildBT_Click(object? sender, EventArgs e)
        {
            var validationError = FileSystemUtility.ValidateDirectoryPath(SDinputbox.Text, mustExist: false);
            if (validationError is not null)
            {
                ShowTemporaryStatus(validationError, true, 6000);
                return;
            }

            string destinationDirectory;
            try
            {
                destinationDirectory = FileSystemUtility.NormalizePath(SDinputbox.Text);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                ShowTemporaryStatus("恢复目标路径无效：" + ex.Message, true, 6000);
                return;
            }

            var sourceForKey = _selectedRestoreSourceDirectory ?? destinationDirectory;
            var saveRecord = _dataStore?.GetLatestRestorableRecord(sourceForKey);
            if (saveRecord is null)
            {
                var lastAttempt = _dataStore?.GetLatestSaveAttempt(sourceForKey);
                ShowTemporaryStatus(
                    lastAttempt is not null
                        ? "该源目录最近一次保存未成功，请重新保存后再恢复"
                        : "没有找到该源目录的保存记录",
                    true,
                    6000);
                return;
            }

            _selectedRestoreSourceDirectory ??= saveRecord.SourceDirectory;

            var tempDirectory = string.IsNullOrWhiteSpace(_tempWorkDirectory)
                ? Path.Combine(Path.GetTempPath(), TempProductFolderName, TempWorkFolderName)
                : _tempWorkDirectory;

            if (FileSystemUtility.PathsOverlap(destinationDirectory, tempDirectory))
            {
                ShowTemporaryStatus("恢复目标不能位于临时工作目录中", true, 6000);
                return;
            }

            if (!FileSystemUtility.PathsEqual(destinationDirectory, saveRecord.SourceDirectory)
                && !_awaitingDifferentDirectoryConfirmation)
            {
                _awaitingDifferentDirectoryConfirmation = true;
                StartRebuildBT.Text = "确定恢复到异目录";
                ShowTemporaryStatus(
                    "恢复目标与保存记录源目录不同，请再次点击“确定恢复到异目录”",
                    true,
                    6000);
                return;
            }

            if (saveRecord.Compressed)
            {
                if (string.IsNullOrWhiteSpace(saveRecord.BackupPath) || !File.Exists(saveRecord.BackupPath))
                {
                    ShowTemporaryStatus("备份归档不存在，无法恢复", true, 6000);
                    return;
                }

                if (WinRarPath is null && UnRarPath is null)
                {
                    ShowTemporaryStatus("缺少 WinRAR / UnRAR，无法解压恢复", true, 6000);
                    return;
                }
            }
            else if (string.IsNullOrWhiteSpace(saveRecord.BackupPath)
                || !Directory.Exists(saveRecord.BackupPath))
            {
                ShowTemporaryStatus("备份文件夹不存在，无法恢复", true, 6000);
                return;
            }

            if (FileSystemUtility.PathsOverlap(saveRecord.BackupPath, destinationDirectory))
            {
                ShowTemporaryStatus("恢复目标与备份路径重叠", true, 6000);
                return;
            }

            var recordSourceName = Path.GetFileName(saveRecord.SourceDirectory.TrimEnd(DirectorySeparators));
            var operationId = RegisterOperation(
                $"恢复 {(string.IsNullOrWhiteSpace(recordSourceName) ? "备份" : recordSourceName)}",
                new[] { saveRecord.BackupPath },
                new[] { destinationDirectory },
                out var conflictMessage);

            if (operationId is null)
            {
                ShowTemporaryStatus(conflictMessage ?? "任务路径冲突", true, 6000);
                return;
            }

            RememberDirectory(saveRecord.SourceDirectory);
            if (!FileSystemUtility.PathsEqual(destinationDirectory, saveRecord.SourceDirectory))
                RememberDirectory(destinationDirectory);

            var settings = new RestoreJobSettings
            {
                JobId = Guid.NewGuid(),
                SaveRecord = saveRecord,
                DestinationDirectory = destinationDirectory,
                CacheRoot = GetCacheRoot(),
                RarPath = WinRarPath,
                UnRarPath = UnRarPath,
            };

            string? openError = null;

            BeginBackgroundTask("正在打开恢复窗口…");
            ReportBackgroundProgress(0);

            try
            {
                await Task.Yield();

                if (_closing || IsDisposed)
                    return;

                var window = new RebuilderWindow(this, settings, operationId.Value);
                window.Show(this);
                ReportBackgroundProgress(100);
            }
            catch (Exception ex)
            {
                UnregisterOperation(operationId.Value);
                openError = "无法打开恢复窗口：" + ex.Message;
            }
            finally
            {
                EndBackgroundTask();

                if (!_closing && !IsDisposed)
                    ResetRestoreConfirmation();
            }

            if (openError is not null && !_closing && !IsDisposed)
                ShowTemporaryStatus(openError, true, 6000);
        }

        private bool TryValidateSourceDirectory()
        {
            var error = FileSystemUtility.ValidateDirectoryPath(SDinputbox.Text, mustExist: true);
            if (error is null)
                return true;

            ShowTemporaryStatus(error, true, 6000);
            return false;
        }

        private bool TryValidateTargetDirectory()
        {
            var error = FileSystemUtility.ValidateDirectoryPath(textBox2.Text, mustExist: true);
            if (error is null)
                return true;

            ShowTemporaryStatus("目标储存目录：" + error, true, 6000);
            return false;
        }

        private string GetCacheRoot()
        {
            var baseDirectory = string.IsNullOrWhiteSpace(_tempWorkDirectory)
                ? Path.Combine(Path.GetTempPath(), TempProductFolderName, TempWorkFolderName)
                : _tempWorkDirectory;

            return Path.Combine(baseDirectory, "Cache");
        }

        internal bool TryRecordOperation(OperationRecord record, out string? error)
        {
            error = null;

            if (_dataStore is null)
            {
                error = "Data 文件不可用";
                return false;
            }

            try
            {
                _dataStore.RecordOperation(record);
                return true;
            }
            catch (Exception ex)
            {
                _dataStoreError = "操作历史写入失败：" + ex.Message;
                UpdateStatusToolTip();
                error = _dataStoreError;
                return false;
            }
        }

        internal Guid? RegisterOperation(
            string description,
            IEnumerable<string> readPaths,
            IEnumerable<string> writePaths,
            out string? error)
        {
            error = null;

            var normalizedReads = NormalizePaths(readPaths);
            var normalizedWrites = NormalizePaths(writePaths);

            foreach (var active in _activeOperations)
            {
                foreach (var writePath in normalizedWrites)
                {
                    if (active.WritePaths.Any(path => FileSystemUtility.PathsOverlap(writePath, path))
                        || active.ReadPaths.Any(path => FileSystemUtility.PathsOverlap(writePath, path)))
                    {
                        error = $"与正在运行的任务“{active.Description}”路径冲突";
                        return null;
                    }
                }

                foreach (var readPath in normalizedReads)
                {
                    if (active.WritePaths.Any(path => FileSystemUtility.PathsOverlap(readPath, path)))
                    {
                        error = $"与正在运行的任务“{active.Description}”路径冲突";
                        return null;
                    }
                }
            }

            var id = Guid.NewGuid();
            _activeOperations.Add(new ActiveOperation
            {
                Id = id,
                Description = description,
                ReadPaths = normalizedReads,
                WritePaths = normalizedWrites,
            });

            return id;
        }

        internal void UnregisterOperation(Guid operationId)
        {
            var index = _activeOperations.FindIndex(operation => operation.Id == operationId);
            if (index >= 0)
                _activeOperations.RemoveAt(index);
        }

        private static List<string> NormalizePaths(IEnumerable<string> paths)
        {
            var result = new List<string>();

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                string normalized;
                try
                {
                    normalized = FileSystemUtility.NormalizePath(path);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
                {
                    normalized = path;
                }

                if (!result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    result.Add(normalized);
            }

            return result;
        }

        private void toolStripStatusLabel1_Click(object? sender, EventArgs e)
        {
            var details = new List<string>
            {
                "Data 文件：" + (_dataStore?.FilePath ?? DataStore.GetDefaultPath()),
                "radarlist：" + GetRadarListSourceDescription(),
                "WinRAR：" + (WinRarPath ?? "未找到"),
                "UnRAR：" + (UnRarPath ?? "未找到"),
                "开始保存：" + (_saveAllowed ? "可用" : "已禁用"),
            };

            if (!string.IsNullOrEmpty(_dataStoreError))
                details.Add(_dataStoreError);

            details.AddRange(_warningDetails);

            MessageBox.Show(
                this,
                string.Join(Environment.NewLine, details),
                "运行状态",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_activeOperations.Count > 0)
            {
                MessageBox.Show(
                    this,
                    $"还有 {_activeOperations.Count} 个保存/恢复任务正在运行。\n请等待任务结束，或先在任务窗口中止操作。",
                    "任务运行中",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                e.Cancel = true;
                return;
            }

            _closing = true;
            _startupReadyTimer.Stop();
            _transientStatusTimer.Stop();

            try
            {
                _autoFindCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 后台任务已经结束。
            }

            try
            {
                _dataStore?.Save();
            }
            catch
            {
                // 关闭时不阻塞退出。
            }

            // 关闭主窗口时销毁复制到 TEMP 的 Data 快照文件。
            CleanupSnapshotFile();

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CleanupSnapshotFile();

            // 只删除 Senoa\DataReBuilder，不删除 Senoa 及其下的其他文件夹。
            TryDeleteDirectory(_tempWorkDirectory);

            _startupReadyTimer.Dispose();
            _transientStatusTimer.Dispose();

            base.OnFormClosed(e);
        }

        private void CleanupSnapshotFile()
        {
            var snapshotPath = _snapshotPath;
            _snapshotPath = null;

            if (string.IsNullOrEmpty(snapshotPath))
                return;

            try
            {
                if (File.Exists(snapshotPath))
                    File.Delete(snapshotPath);
            }
            catch (IOException)
            {
                // 编辑器仍占用快照文件，退出时随 DataReBuilder 目录一起清理。
            }
            catch (UnauthorizedAccessException)
            {
                // 无权限删除时保持文件，下次启动会清理整个快照目录。
            }
        }

        private static void TryDeleteDirectory(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // 目录内文件仍被占用。
            }
            catch (UnauthorizedAccessException)
            {
                // 无权限删除。
            }
        }
    }
}
