namespace AppDataRebuilder
{
    partial class RebuilderWindow
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            MinimizeBT = new Button();
            CancalBT = new Button();
            WorkflowTextBox = new TextBox();
            HighProgress = new ProgressBar();
            LowProgress = new ProgressBar();
            backgroundWorker1 = new System.ComponentModel.BackgroundWorker();
            SuspendLayout();
            // 
            // MinimizeBT
            // 
            MinimizeBT.Location = new Point(165, 204);
            MinimizeBT.Name = "MinimizeBT";
            MinimizeBT.Size = new Size(307, 45);
            MinimizeBT.TabIndex = 9;
            MinimizeBT.Text = "最小化窗口";
            MinimizeBT.UseVisualStyleBackColor = true;
            // 
            // CancalBT
            // 
            CancalBT.Location = new Point(12, 204);
            CancalBT.Name = "CancalBT";
            CancalBT.Size = new Size(147, 45);
            CancalBT.TabIndex = 8;
            CancalBT.Text = "中止操作";
            CancalBT.UseVisualStyleBackColor = true;
            // 
            // WorkflowTextBox
            // 
            WorkflowTextBox.Location = new Point(12, 84);
            WorkflowTextBox.Multiline = true;
            WorkflowTextBox.Name = "WorkflowTextBox";
            WorkflowTextBox.ReadOnly = true;
            WorkflowTextBox.Size = new Size(460, 93);
            WorkflowTextBox.TabIndex = 7;
            WorkflowTextBox.Text = "工作历史将显示在这里";
            // 
            // HighProgress
            // 
            HighProgress.Location = new Point(12, 12);
            HighProgress.Name = "HighProgress";
            HighProgress.Size = new Size(460, 30);
            HighProgress.TabIndex = 6;
            // 
            // LowProgress
            // 
            LowProgress.Location = new Point(12, 48);
            LowProgress.Name = "LowProgress";
            LowProgress.Size = new Size(460, 30);
            LowProgress.TabIndex = 5;
            // 
            // RebuilderWindow
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(484, 261);
            ControlBox = false;
            Controls.Add(MinimizeBT);
            Controls.Add(CancalBT);
            Controls.Add(WorkflowTextBox);
            Controls.Add(HighProgress);
            Controls.Add(LowProgress);
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "RebuilderWindow";
            ShowIcon = false;
            Text = "正在进行保存";
            Load += RebuilderWindow_Load;
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button MinimizeBT;
        private Button CancalBT;
        private TextBox WorkflowTextBox;
        private ProgressBar HighProgress;
        private ProgressBar LowProgress;
        private System.ComponentModel.BackgroundWorker backgroundWorker1;
    }
}