using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

internal sealed class UiHangForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();

    private UiHangForm()
    {
        Text = "UiHangTest";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(300, 300);
        Size = new Size(520, 360);
        TopMost = true;
        BackColor = Color.SteelBlue;

        Label label = new Label();
        label.Text = "UI thread blocks 1.5 s every 3 s";
        label.ForeColor = Color.White;
        label.Font = new Font("Segoe UI", 14f);
        label.AutoSize = true;
        label.Location = new Point(40, 40);
        Controls.Add(label);

        _timer.Interval = 3000;
        _timer.Tick += delegate
        {
            Thread.Sleep(1500);
        };
        _timer.Start();
    }

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new UiHangForm());
    }
}
