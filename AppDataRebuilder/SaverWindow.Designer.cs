namespace AppDataRebuilder
{
    partial class SaverWindow
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
            LowProgress = new ProgressBar();
            HighProgress = new ProgressBar();
            WorkflowTextBox = new TextBox();
            CancalBT = new Button();
            MinimizeBT = new Button();
            backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            SuspendLayout();
            // 
            // LowProgress
            // 
            LowProgress.Location = new Point(12, 48);
            LowProgress.Name = "LowProgress";
            LowProgress.Size = new Size(460, 30);
            LowProgress.TabIndex = 0;
            LowProgress.Click += progressBar1_Click;
            // 
            // HighProgress
            // 
            HighProgress.Location = new Point(12, 12);
            HighProgress.Name = "HighProgress";
            HighProgress.Size = new Size(460, 30);
            HighProgress.TabIndex = 1;
            // 
            // WorkflowTextBox
            // 
            WorkflowTextBox.Location = new Point(12, 84);
            WorkflowTextBox.Multiline = true;
            WorkflowTextBox.Name = "WorkflowTextBox";
            WorkflowTextBox.ReadOnly = true;
            WorkflowTextBox.Size = new Size(460, 93);
            WorkflowTextBox.TabIndex = 2;
            WorkflowTextBox.Text = "工作历史将显示在这里";
            // 
            // CancalBT
            // 
            CancalBT.Location = new Point(12, 204);
            CancalBT.Name = "CancalBT";
            CancalBT.Size = new Size(147, 45);
            CancalBT.TabIndex = 3;
            CancalBT.Text = "中止操作";
            CancalBT.UseVisualStyleBackColor = true;
            // 
            // MinimizeBT
            // 
            MinimizeBT.Location = new Point(165, 204);
            MinimizeBT.Name = "MinimizeBT";
            MinimizeBT.Size = new Size(307, 45);
            MinimizeBT.TabIndex = 4;
            MinimizeBT.Text = "最小化窗口";
            MinimizeBT.UseVisualStyleBackColor = true;
            // 
            // SaverWindow
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(480, 257);
            ControlBox = false;
            Controls.Add(MinimizeBT);
            Controls.Add(CancalBT);
            Controls.Add(WorkflowTextBox);
            Controls.Add(HighProgress);
            Controls.Add(LowProgress);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "SaverWindow";
            ShowIcon = false;
            SizeGripStyle = SizeGripStyle.Hide;
            Text = "正在恢复到目标目录";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private ProgressBar LowProgress;
        private ProgressBar HighProgress;
        private TextBox WorkflowTextBox;
        private Button CancalBT;
        private Button MinimizeBT;
        private System.ComponentModel.BackgroundWorker backgroundWorker1;
    }
}
