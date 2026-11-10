using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TaskMonitor;

// 항상 위에 떠 있는 작은 창: CPU·메모리·디스크·GPU·네트워크를 숫자로만 보여 준다.
// 아무 곳이나 끌어서 옮기고, 두 번 누르면 기본 창을 연다. 기본 창의 소유 창이 아니라서 기본 창을 최소화해도 남는다.
internal sealed class MiniWindow : Form
{
    private readonly Label _text = new()
    {
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI Variable Text", 9.5F),
        Padding = new Padding(8, 6, 8, 6),
    };

    public MiniWindow(Action openMain, Action close)
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(240, 76);
        Padding = new Padding(1); // 테두리가 라벨에 가리지 않게
        BackColor = Theme.Bg;
        _text.ForeColor = Theme.Fg;
        _text.Text = Strings.Localize("데이터 수집 중...");
        Controls.Add(_text);

        _text.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.Clicks == 1)
            {
                // 창 제목 줄을 잡은 것처럼 Windows에 끌기를 맡긴다.
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
            }
        };
        _text.DoubleClick += (_, _) => openMain();
        var menu = new ContextMenuStrip();
        menu.Items.Add(Strings.Localize("TaskMonitor 열기"), null, (_, _) => openMain());
        menu.Items.Add(Strings.Localize("미니 창 닫기"), null, (_, _) => close());
        _text.ContextMenuStrip = menu;
    }

    // 띄우는 창이 포커스를 가져가지 않게 한다(게임이나 작업 중인 창이 뒤로 밀리지 않게).
    protected override bool ShowWithoutActivation => true;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    public void Show(Snapshot snap, PerfSnapshot perf, double? cpuTempC)
    {
        var m = perf.Memory;
        double mem = m.Total > 0 ? m.Used * 100.0 / m.Total : 0;
        double disk = perf.Disks.Select(d => d.BusyPercent).DefaultIfEmpty(0).Max();
        double gpu = perf.Gpus.Select(g => g.Utilization).DefaultIfEmpty(0).Max();
        string temp = cpuTempC is { } t ? $"   {t:F0}°C" : "";
        _text.Text = $"CPU {snap.TotalCpuPercent:F0}%{temp}\n"
            + Strings.Fmt("메모리 {0:F0}%   디스크 {1:F0}%   GPU {2:F0}%", mem, disk, gpu) + "\n"
            + $"↓ {RowBuilders.FormatRate(perf.Nics.Sum(n => n.ReceiveBytesPerSec))}   ↑ {RowBuilders.FormatRate(perf.Nics.Sum(n => n.SendBytesPerSec))}";
    }

    // 저장된 위치가 지금 연결된 화면 밖이면(모니터를 뺀 경우) 주 화면 오른쪽 아래로 옮긴다.
    public void PlaceAt(int? x, int? y)
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var wanted = new Point(x ?? area.Right - Width - 16, y ?? area.Bottom - Height - 16);
        bool visible = Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Rectangle(wanted, Size)));
        Location = visible ? wanted : new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
