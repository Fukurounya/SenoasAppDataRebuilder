namespace AppDataRebuilder
{
    public partial class RebuilderWindow : Form
    {
        private readonly MainWindow? _host;
        private readonly RestoreJobSettings? _settings;
        private readonly Guid _operationId;

        private CancellationTokenSource? _cancellation;
        private Guid? _workItemId;
        private bool _running;
        private bool _closeAfterCancel;
        private bool _closeAfterRun;

        public RebuilderWindow()
        {
            InitializeComponent();
            ConfigureWindow();
        }

        internal RebuilderWindow(MainWindow host, RestoreJobSettings settings, Guid operationId) : this()
        {
            _host = host;
            _settings = settings;
            _operationId = operationId;

            var sourceName = Path.GetFileName(settings.SaveRecord.SourceDirectory.TrimEnd('\\', '/'));

            Name = $"RebuilderWindow_{settings.JobId:N}";
            Text = $"正在恢复 - {(string.IsNullOrWhiteSpace(sourceName) ? "备份" : sourceName)} - {DateTime.Now:yyyyMMdd_HHmmss_fff}";

            Shown += RebuilderWindow_Shown;
        }

        private void ConfigureWindow()
        {
            HighProgress.Minimum = 0;
            HighProgress.Maximum = 100;
            HighProgress.Value = 0;

            LowProgress.Minimum = 0;
            LowProgress.Maximum = 100;
            LowProgress.Value = 0;

            WorkflowTextBox.ScrollBars = ScrollBars.Vertical;
            WorkflowTextBox.Clear();

            CancalBT.Click += CancalBT_Click;
            MinimizeBT.Click += MinimizeBT_Click;
            FormClosing += RebuilderWindow_FormClosing;
            FormClosed += RebuilderWindow_FormClosed;
        }

        private async void RebuilderWindow_Shown(object? sender, EventArgs e)
        {
            if (_host is null || _settings is null || _running)
                return;

            await RunAsync();
        }

        private async Task RunAsync()
        {
            if (_host is null || _settings is null)
                return;

            _running = true;
            _closeAfterRun = false;
            _cancellation = new CancellationTokenSource();

            CancalBT.Text = "中止操作";
            CancalBT.Enabled = true;

            var sourceName = Path.GetFileName(_settings.SaveRecord.SourceDirectory.TrimEnd('\\', '/'));
            _workItemId = _host.RegisterWorkItem(
                $"恢复 {(string.IsNullOrWhiteSpace(sourceName) ? "备份" : sourceName)}",
                _settings.DestinationDirectory);

            AppendRecordHeader(_settings);
            AppendLog("开始恢复…");

            var progress = new Progress<OperationProgress>(UpdateProgress);
            IProgress<string> log = new Progress<string>(AppendLog);

            try
            {
                var record = await BackupService.RestoreAsync(
                    _settings,
                    progress,
                    log,
                    _cancellation.Token);

                AppendLog("恢复完成");

                if (!_host.TryRecordOperation(record, out var recordError))
                    throw new InvalidOperationException("恢复完成，但写入 Data 失败：" + recordError);

                if (_workItemId is Guid completedWorkItem)
                    _host.CompleteWorkItem(completedWorkItem, true);

                _closeAfterRun = true;
            }
            catch (OperationCanceledException)
            {
                AppendLog("操作已中止");
                RecordFailure("用户中止");

                if (_workItemId is Guid cancelledWorkItem)
                    _host.CompleteWorkItem(cancelledWorkItem, false);

                ShowFailureState();
            }
            catch (Exception ex)
            {
                AppendLog("恢复失败：" + ex.Message);
                RecordFailure(ex.Message);

                if (_workItemId is Guid failedWorkItem)
                    _host.CompleteWorkItem(failedWorkItem, false);

                ShowFailureState();
            }
            finally
            {
                _running = false;

                _cancellation?.Dispose();
                _cancellation = null;

                _host.UnregisterOperation(_operationId);

                CancalBT.Enabled = true;
                if (!_closeAfterRun && !_closeAfterCancel)
                    CancalBT.Text = "关闭";

                if ((_closeAfterRun || _closeAfterCancel) && !IsDisposed)
                    Close();
            }
        }

        private void AppendRecordHeader(RestoreJobSettings settings)
        {
            var record = settings.SaveRecord;

            AppendLog("===== 操作记录 =====");
            AppendLog("最近操作：" + record.TypeText);
            AppendLog("操作时间：" + record.Time.ToString("yyyy-MM-dd HH:mm:ss"));
            AppendLog("最近结果：" + (record.Success
                ? "成功"
                : "失败" + (string.IsNullOrWhiteSpace(record.Error) ? string.Empty : "：" + record.Error)));
            AppendLog("源目录：" + record.SourceDirectory);
            AppendLog("目标目录：" + record.TargetDirectory);
            AppendLog("备份文件：" + record.BackupPath);
            AppendLog("压缩：" + (record.Compressed
                ? $"是（{record.CompressionLevelText}，{record.ArchiveFormatText}）"
                : "否"));
            AppendLog("保留时间戳：" + (record.KeepTimestamps ? "是" : "否"));
            AppendLog("保留完整文件属性：" + (record.KeepFileAttributes ? "是" : "否"));
            AppendLog("SHA256：" + (string.IsNullOrWhiteSpace(record.Sha256) ? "无" : record.Sha256));
            AppendLog("恢复目标：" + settings.DestinationDirectory);
            AppendLog("====================");
        }

        private void RecordFailure(string message)
        {
            if (_host is null || _settings is null)
                return;

            var saveRecord = _settings.SaveRecord;
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
                RestoreDestination = _settings.DestinationDirectory,
                Success = false,
                Error = message,
            };

            if (!_host.TryRecordOperation(record, out var error))
                AppendLog("写入失败记录失败：" + error);
        }

        private void ShowFailureState()
        {
            HighProgress.Style = ProgressBarStyle.Blocks;
            CancalBT.Text = "关闭";
            CancalBT.Enabled = true;
        }

        private void UpdateProgress(OperationProgress progress)
        {
            if (IsDisposed)
                return;

            HighProgress.Value = Math.Clamp(progress.OverallPercent, 0, 100);
            LowProgress.Value = Math.Clamp(progress.PhasePercent, 0, 100);

            if (_workItemId is Guid workItemId)
                _host?.ReportWorkItem(workItemId, progress.OverallPercent);
        }

        private void AppendLog(string message)
        {
            if (IsDisposed)
                return;

            WorkflowTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            WorkflowTextBox.SelectionStart = WorkflowTextBox.TextLength;
            WorkflowTextBox.ScrollToCaret();
        }

        private void CancalBT_Click(object? sender, EventArgs e)
        {
            if (_running)
            {
                AppendLog("正在中止操作…");
                CancalBT.Enabled = false;
                _cancellation?.Cancel();
                return;
            }

            Close();
        }

        private void MinimizeBT_Click(object? sender, EventArgs e)
        {
            WindowState = FormWindowState.Minimized;
        }

        private void RebuilderWindow_FormClosing(object? sender, FormClosingEventArgs e)
        {
            if (!_running)
                return;

            if (_closeAfterCancel)
            {
                e.Cancel = true;
                return;
            }

            var result = MessageBox.Show(
                this,
                "恢复任务正在运行，是否中止并关闭？",
                "任务运行中",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _closeAfterCancel = true;
            _cancellation?.Cancel();
            CancalBT.Enabled = false;
            e.Cancel = true;
        }

        private void RebuilderWindow_FormClosed(object? sender, FormClosedEventArgs e)
        {
            _host?.UnregisterOperation(_operationId);

            if (_workItemId is Guid workItemId)
                _host?.RemoveWorkItem(workItemId);

            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
        }

        private void RebuilderWindow_Load(object? sender, EventArgs e)
        {
        }
    }
}
