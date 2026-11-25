#nullable disable

namespace AppDataRebuilder
{
    partial class MainWindow
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            SDSelectBT = new Button();
            TDSelectBT = new Button();
            SDinputbox = new TextBox();
            textBox2 = new TextBox();
            SDAutoFindComboBox = new ComboBox();
            SDAutoFind = new Button();
            TimestampOF = new CheckBox();
            FilestampOF = new CheckBox();
            MainStatusBar = new StatusStrip();
            MainProgressBar = new ToolStripProgressBar();
            MainStatusLabel = new ToolStripStatusLabel();
            toolStripProgressBar1 = new ToolStripProgressBar();
            FastestWorkPersent = new ToolStripStatusLabel();
            folderBrowserDialog1 = new FolderBrowserDialog();
            CompassLv = new TrackBar();
            CompressOF = new CheckBox();
            CompassLvText = new TextBox();
            DataFileOpenBT = new Button();
            ComboBoxSelect = new ComboBox();
            StartRebuildBT = new Button();
            StartSaveBT = new Button();
            MainStatusBar.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)CompassLv).BeginInit();
            SuspendLayout();
            // 
            // SDSelectBT
            // 
            SDSelectBT.Location = new Point(21, 12);
            SDSelectBT.Name = "SDSelectBT";
            SDSelectBT.Size = new Size(106, 40);
            SDSelectBT.TabIndex = 0;
            SDSelectBT.Text = "AppData目录";
            SDSelectBT.UseVisualStyleBackColor = true;
            SDSelectBT.Click += SDSelectBT_Click;
            // 
            // TDSelectBT
            // 
            TDSelectBT.Location = new Point(21, 110);
            TDSelectBT.Name = "TDSelectBT";
            TDSelectBT.Size = new Size(106, 40);
            TDSelectBT.TabIndex = 1;
            TDSelectBT.Text = "目标储存目录";
            TDSelectBT.UseVisualStyleBackColor = true;
            TDSelectBT.Click += TDSelectBT_Click;
            // 
            // SDinputbox
            // 
            SDinputbox.AllowDrop = true;
            SDinputbox.Location = new Point(133, 21);
            SDinputbox.Name = "SDinputbox";
            SDinputbox.Size = new Size(321, 23);
            SDinputbox.TabIndex = 3;
            SDinputbox.TextChanged += SDinputbox_TextChanged;
            // 
            // textBox2
            // 
            textBox2.Location = new Point(133, 119);
            textBox2.Name = "textBox2";
            textBox2.Size = new Size(321, 23);
            textBox2.TabIndex = 4;
            // 
            // SDAutoFindComboBox
            // 
            SDAutoFindComboBox.FormattingEnabled = true;
            SDAutoFindComboBox.Location = new Point(133, 64);
            SDAutoFindComboBox.Name = "SDAutoFindComboBox";
            SDAutoFindComboBox.Size = new Size(239, 25);
            SDAutoFindComboBox.TabIndex = 5;
            SDAutoFindComboBox.SelectedIndexChanged += SDAutoFindComboBox_SelectedIndexChanged;
            SDAutoFindComboBox.KeyDown += SDAutoFindComboBox_KeyDown;
            // 
            // SDAutoFind
            // 
            SDAutoFind.Location = new Point(378, 63);
            SDAutoFind.Name = "SDAutoFind";
            SDAutoFind.Size = new Size(76, 25);
            SDAutoFind.TabIndex = 7;
            SDAutoFind.Text = "自动寻找";
            SDAutoFind.UseVisualStyleBackColor = true;
            SDAutoFind.Click += SDAutoFind_Click;
            // 
            // TimestampOF
            // 
            TimestampOF.AutoSize = true;
            TimestampOF.Location = new Point(21, 184);
            TimestampOF.Name = "TimestampOF";
            TimestampOF.Size = new Size(87, 21);
            TimestampOF.TabIndex = 9;
            TimestampOF.Text = "保留时间戳";
            TimestampOF.UseVisualStyleBackColor = true;
            TimestampOF.CheckedChanged += TimestampOF_CheckedChanged;
            // 
            // FilestampOF
            // 
            FilestampOF.AutoSize = true;
            FilestampOF.Location = new Point(116, 184);
            FilestampOF.Name = "FilestampOF";
            FilestampOF.Size = new Size(123, 21);
            FilestampOF.TabIndex = 10;
            FilestampOF.Text = "保留完整文件属性";
            FilestampOF.UseVisualStyleBackColor = true;
            FilestampOF.CheckedChanged += FilestampOF_CheckedChanged;
            // 
            // MainStatusBar
            // 
            MainStatusBar.Items.AddRange(new ToolStripItem[] { MainProgressBar, MainStatusLabel, toolStripProgressBar1, FastestWorkPersent });
            MainStatusBar.Location = new Point(0, 314);
            MainStatusBar.Name = "MainStatusBar";
            MainStatusBar.Size = new Size(466, 22);
            MainStatusBar.TabIndex = 12;
            MainStatusBar.Text = "statusStrip1";
            // 
            // MainProgressBar
            // 
            MainProgressBar.ForeColor = Color.Lime;
            MainProgressBar.Name = "MainProgressBar";
            MainProgressBar.Size = new Size(60, 16);
            // 
            // MainStatusLabel
            // 
            MainStatusLabel.Name = "MainStatusLabel";
            MainStatusLabel.Size = new Size(68, 17);
            MainStatusLabel.Text = "后台无进程";
            MainStatusLabel.Click += toolStripStatusLabel1_Click;
            // 
            // toolStripProgressBar1
            // 
            toolStripProgressBar1.Alignment = ToolStripItemAlignment.Right;
            toolStripProgressBar1.Name = "toolStripProgressBar1";
            toolStripProgressBar1.Size = new Size(160, 16);
            toolStripProgressBar1.Visible = false;
            // 
            // FastestWorkPersent
            // 
            FastestWorkPersent.Name = "FastestWorkPersent";
            FastestWorkPersent.Size = new Size(140, 17);
            FastestWorkPersent.Text = "当前进程最快任务的进度";
            FastestWorkPersent.Visible = false;
            // 
            // CompassLv
            // 
            CompassLv.Location = new Point(78, 211);
            CompassLv.Maximum = 5;
            CompassLv.Name = "CompassLv";
            CompassLv.Size = new Size(248, 45);
            CompassLv.TabIndex = 13;
            CompassLv.TickStyle = TickStyle.Both;
            CompassLv.Value = 5;
            CompassLv.ValueChanged += CompassLv_ValueChanged;
            // 
            // CompressOF
            // 
            CompressOF.AutoSize = true;
            CompressOF.Location = new Point(21, 223);
            CompressOF.Name = "CompressOF";
            CompressOF.Size = new Size(51, 21);
            CompressOF.TabIndex = 14;
            CompressOF.Text = "压缩";
            CompressOF.UseVisualStyleBackColor = true;
            // 
            // CompassLvText
            // 
            CompassLvText.Location = new Point(332, 221);
            CompassLvText.Name = "CompassLvText";
            CompassLvText.ReadOnly = true;
            CompassLvText.Size = new Size(60, 23);
            CompassLvText.TabIndex = 15;
            // 
            // DataFileOpenBT
            // 
            DataFileOpenBT.Location = new Point(322, 182);
            DataFileOpenBT.Name = "DataFileOpenBT";
            DataFileOpenBT.Size = new Size(132, 23);
            DataFileOpenBT.TabIndex = 16;
            DataFileOpenBT.Text = "查看保存的路径数据";
            DataFileOpenBT.UseVisualStyleBackColor = true;
            DataFileOpenBT.Click += DataFileOpenBT_Click;
            // 
            // ComboBoxSelect
            // 
            ComboBoxSelect.FormattingEnabled = true;
            ComboBoxSelect.ImeMode = ImeMode.Off;
            ComboBoxSelect.Location = new Point(21, 64);
            ComboBoxSelect.Name = "ComboBoxSelect";
            ComboBoxSelect.Size = new Size(106, 25);
            ComboBoxSelect.TabIndex = 17;
            ComboBoxSelect.Text = "处理目录历史";
            ComboBoxSelect.SelectedIndexChanged += ComboBoxSelect_SelectedIndexChanged;
            // 
            // StartRebuildBT
            // 
            StartRebuildBT.Location = new Point(78, 262);
            StartRebuildBT.Name = "StartRebuildBT";
            StartRebuildBT.Size = new Size(150, 37);
            StartRebuildBT.TabIndex = 18;
            StartRebuildBT.Text = "开始恢复";
            StartRebuildBT.UseVisualStyleBackColor = true;
            StartRebuildBT.Click += StartRebuildBT_Click;
            // 
            // StartSaveBT
            // 
            StartSaveBT.Location = new Point(266, 262);
            StartSaveBT.Name = "StartSaveBT";
            StartSaveBT.Size = new Size(106, 37);
            StartSaveBT.TabIndex = 19;
            StartSaveBT.Text = "开始保存";
            StartSaveBT.UseVisualStyleBackColor = true;
            StartSaveBT.Click += StartSaveBT_Click;
            // 
            // MainWindow
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            BackgroundImageLayout = ImageLayout.None;
            ClientSize = new Size(466, 336);
            Controls.Add(StartSaveBT);
            Controls.Add(StartRebuildBT);
            Controls.Add(ComboBoxSelect);
            Controls.Add(DataFileOpenBT);
            Controls.Add(CompassLvText);
            Controls.Add(CompressOF);
            Controls.Add(CompassLv);
            Controls.Add(MainStatusBar);
            Controls.Add(FilestampOF);
            Controls.Add(TimestampOF);
            Controls.Add(SDAutoFind);
            Controls.Add(SDAutoFindComboBox);
            Controls.Add(textBox2);
            Controls.Add(SDinputbox);
            Controls.Add(TDSelectBT);
            Controls.Add(SDSelectBT);
            MaximizeBox = false;
            Name = "MainWindow";
            ShowIcon = false;
            Text = "App UserData ReBuilder";
            Load += MainWindow_Load;
            MainStatusBar.ResumeLayout(false);
            MainStatusBar.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)CompassLv).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private System.ComponentModel.BackgroundWorker backgroundWorker1;
        private Button SDSelectBT;
        private Button TDSelectBT;
        private TextBox SDinputbox;
        private TextBox textBox2;
        private ComboBox SDAutoFindComboBox;
        private Button SDAutoFind;
        private CheckBox TimestampOF;
        private CheckBox FilestampOF;
        private StatusStrip MainStatusBar;
        private ToolStripProgressBar MainProgressBar;
        private FolderBrowserDialog folderBrowserDialog1;
        private TrackBar CompassLv;
        private CheckBox CompressOF;
        private TextBox CompassLvText;
        private ToolStripStatusLabel MainStatusLabel;
        private ToolStripProgressBar toolStripProgressBar1;
        private ToolStripStatusLabel FastestWorkPersent;
        private Button DataFileOpenBT;
        private ComboBox ComboBoxSelect;
        private Button StartRebuildBT;
        private Button StartSaveBT;
    }
}
