namespace AppDataRebuilder
{
    public partial class SaverWindow : Form
    {
        private readonly MainWindow? _host;
        private readonly SaveJobSettings? _settings;
        private readonly Guid _operationId;

        private CancellationTokenSource? _cancellation;
        private Guid? _workItemId;
        private bool _running;
        private bool _closeAfterCancel;
        private bool _closeAfterRun;

        public SaverWindow()
        {
            InitializeComponent();
            ConfigureWindow();
        }

        internal SaverWindow(MainWindow host, SaveJobSettings settings, Guid operationId) : this()
        {
            _host = host;
            _settings = settings;
            _operationId = operationId;

            Name = $"SaverWindow_{settings.JobId:N}";
            Text = $"正在保存 - {settings.SourceName} - {DateTime.Now:yyyyMMdd_HHmmss_fff}";

            Shown += SaverWindow_Shown;
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
            FormClosing += SaverWindow_FormClosing;
            FormClosed += SaverWindow_FormClosed;
        }

        private async void SaverWindow_Shown(object? sender, EventArgs e)
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
            _workItemId = _host.RegisterWorkItem($"保存 {_settings.SourceName}", _settings.BackupPath);

            var progress = new Progress<OperationProgress>(UpdateProgress);
            IProgress<string> log = new Progress<string>(AppendLog);

            log.Report($"开始保存：{_settings.SourceDirectory}");
            log.Report($"目标目录：{_settings.TargetDirectory}");
            log.Report(_settings.Compress
                ? $"压缩方式：{OperationRecord.GetCompressionLevelText(_settings.CompressionLevel)}"
                : "压缩方式：不压缩");

            try
            {
                var record = await BackupService.SaveAsync(
                    _settings,
                    progress,
                    log,
                    _cancellation.Token);

                log.Report("保存完成");

                if (!_host.TryRecordOperation(record, out var recordError))
                    throw new InvalidOperationException("保存完成，但写入 Data 失败：" + recordError);

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
                AppendLog("保存失败：" + ex.Message);
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

        private void RecordFailure(string message)
        {
            if (_host is null || _settings is null)
                return;

            var record = new OperationRecord
            {
                Type = OperationType.Save,
                Time = DateTimeOffset.Now,
                SourceDirectory = _settings.SourceDirectory,
                TargetDirectory = _settings.TargetDirectory,
                BackupPath = _settings.BackupPath,
                Compressed = _settings.Compress,
                CompressionLevel = _settings.CompressionLevel,
                KeepTimestamps = _settings.KeepTimestamps,
                KeepFileAttributes = _settings.KeepFileAttributes,
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

        private void SaverWindow_FormClosing(object? sender, FormClosingEventArgs e)
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
                "保存任务正在运行，是否中止并关闭？",
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

        private void SaverWindow_FormClosed(object? sender, FormClosedEventArgs e)
        {
            _host?.UnregisterOperation(_operationId);

            if (_workItemId is Guid workItemId)
                _host?.RemoveWorkItem(workItemId);

            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
        }

        private void progressBar1_Click(object? sender, EventArgs e)
        {
        }
    }
}
