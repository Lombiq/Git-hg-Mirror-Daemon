namespace GitHgMirror.Daemon
{
    partial class GitHgMirrorService
    {
        /// <summary> 
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.serviceEventLog = new System.Diagnostics.EventLog();
            ((System.ComponentModel.ISupportInitialize)(this.serviceEventLog)).BeginInit();
            // 
            // serviceEventLog
            // 
            this.serviceEventLog.Log = "Git-hg Mirror Daemon";
            this.serviceEventLog.Source = "GitHgMirror.Daemon";
            // 
            // GitHgMirrorService
            // 
            this.ServiceName = "GitHgMirrorService";
            ((System.ComponentModel.ISupportInitialize)(this.serviceEventLog)).EndInit();

        }

        #endregion

        private System.Diagnostics.EventLog serviceEventLog;
    }
}
