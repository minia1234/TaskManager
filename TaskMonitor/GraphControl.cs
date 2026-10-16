using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TaskMonitor;

// 0~100 사용률 그래프. 최근 값이 오른쪽에 오고, 한 화면에 최대 Capacity개를 그린다.
internal sealed class GraphControl : Control
{
    internal const int Capacity = 60;

    private IReadOnlyList<double> _values = Array.Empty<double>();

    public GraphControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Bg;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color LineColor { get; set; } = ColorTranslator.FromHtml("#0078D4");

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color FillColor { get; set; } = ColorTranslator.FromHtml("#CCE8FF");

    public void SetValues(IReadOnlyList<double> values)
    {
        _values = values;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        using (var gridPen = new Pen(Theme.Grid))
        {
            for (int i = 1; i < 4; i++)
            {
                int y = Height * i / 4;
                g.DrawLine(gridPen, 0, y, Width, y);
            }
        }

        int n = _values.Count;
        if (n < 2 || Width < 2 || Height < 2)
        {
            return;
        }

        float w = Width - 1, h = Height - 1;
        var points = new PointF[n];
        for (int i = 0; i < n; i++)
        {
            float x = w - (n - 1 - i) * (w / (Capacity - 1));
            float y = h - (float)(Math.Clamp(_values[i], 0, 100) / 100.0) * h;
            points[i] = new PointF(x, y);
        }

        var area = new PointF[n + 2];
        Array.Copy(points, area, n);
        area[n] = new PointF(points[n - 1].X, h);
        area[n + 1] = new PointF(points[0].X, h);

        using (var brush = new SolidBrush(FillColor))
        {
            g.FillPolygon(brush, area);
        }

        using var pen = new Pen(LineColor, 1.5F);
        g.DrawLines(pen, points);
    }
}
