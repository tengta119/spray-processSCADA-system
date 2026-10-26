using YR.FrameWork.DependencyInjection;

namespace Yale.SprayProcessSCADASystem;

public partial class FromStartLoad : Form, ISingletonSelfDependency
{
    public FromStartLoad()
    {
        InitializeComponent();
        ClientSize = new Size(377, 260);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        BackColor = Color.DarkCyan;
        Controls.Add(new PictureBox
        {
            Image = Properties.Resources._waiting,
            Location = new Point(126, 30),
            Size = new Size(125, 125),
            SizeMode = PictureBoxSizeMode.Zoom
        });
        Controls.Add(new Label
        {
            Text = "正在初始化数据，请稍后…",
            Font = new Font("微软雅黑", 13),
            ForeColor = Color.White,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(10, 180),
            Size = new Size(357, 40)
        });
    }

    // Retain the existing call sites while keeping every window on the main UI thread.
    public void OpenFormThread()
    {
        if (!Visible) Show();
    }

    public void CloseFormThread()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(new Action(CloseFormThread));
            return;
        }
        Hide();
    }
}
