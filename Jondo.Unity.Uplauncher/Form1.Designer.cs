namespace Jondo.Unity.Uplauncher
{
    partial class Form1
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
            buttonPlay = new Button();
            labelStatus = new Label();
            SuspendLayout();
            // 
            // buttonPlay
            // 
            buttonPlay.BackColor = Color.Red;
            buttonPlay.Cursor = Cursors.Hand;
            buttonPlay.Font = new Font("Yu Gothic", 24F, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point);
            buttonPlay.Location = new Point(291, 296);
            buttonPlay.Name = "buttonPlay";
            buttonPlay.Size = new Size(170, 72);
            buttonPlay.TabIndex = 0;
            buttonPlay.Text = "Jouer";
            buttonPlay.UseVisualStyleBackColor = false;
            buttonPlay.Click += button1_Click;
            // 
            // labelStatus
            // 
            labelStatus.AutoSize = true;
            labelStatus.Font = new Font("Segoe UI", 20F, FontStyle.Regular, GraphicsUnit.Point);
            labelStatus.Location = new Point(291, 191);
            labelStatus.Name = "labelStatus";
            labelStatus.Size = new Size(170, 37);
            labelStatus.TabIndex = 1;
            labelStatus.Text = "Lancer Dofus";
            labelStatus.Click += label1_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            Controls.Add(labelStatus);
            Controls.Add(buttonPlay);
            Name = "Form1";
            Text = "JondoUplauncher";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button buttonPlay;
        private Label labelStatus;
    }
}
