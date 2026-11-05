using System.Windows.Forms;

namespace TaskMonitor;

// 작은 대화상자 모음. 확인을 누르면 값을 돌려주고, 취소하면 null을 돌려준다.
internal static class Dialogs
{
    public static IReadOnlyList<string>? ChooseColumns(IWin32Window owner, IReadOnlyList<ColumnDef> all, IReadOnlyList<string> visible)
    {
        using var form = BaseForm("열 선택", 320, 480);
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        foreach (var column in all)
        {
            list.Items.Add(Strings.Localize(column.Header), visible.Contains(column.Key));
        }

        form.Controls.Add(list);
        form.Controls.Add(ButtonRow(form));
        if (form.ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }

        var keys = new List<string>();
        for (int i = 0; i < all.Count; i++)
        {
            if (list.GetItemChecked(i))
            {
                keys.Add(all[i].Key);
            }
        }

        return keys;
    }

    public static (string Command, bool Admin)? RunNewTask(IWin32Window owner)
    {
        using var form = BaseForm("새 작업 실행", 440, 200);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        var admin = new CheckBox { Text = Strings.Localize("관리자 권한으로 이 작업 만들기"), Dock = DockStyle.Top, Height = 30 };
        var input = new TextBox { Dock = DockStyle.Top };
        var label = new Label
        {
            Text = Strings.Localize("열 프로그램, 폴더, 문서 또는 인터넷 리소스의 이름을 입력하십시오."),
            Dock = DockStyle.Top,
            Height = 40,
        };
        body.Controls.Add(admin);
        body.Controls.Add(input);
        body.Controls.Add(label);
        form.Controls.Add(body);
        form.Controls.Add(ButtonRow(form));

        if (form.ShowDialog(owner) != DialogResult.OK || string.IsNullOrWhiteSpace(input.Text))
        {
            return null;
        }

        return (input.Text.Trim(), admin.Checked);
    }

    // 선호도: 논리 프로세서별 체크박스. 최대 64개까지 표시한다.
    public static ulong? Affinity(IWin32Window owner, int processorCount, ulong current)
    {
        using var form = BaseForm("선호도 설정", 300, 420);
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        int count = Math.Min(processorCount, 64);
        for (int i = 0; i < count; i++)
        {
            list.Items.Add($"CPU {i}", ((current >> i) & 1) != 0);
        }

        form.Controls.Add(list);
        form.Controls.Add(ButtonRow(form));
        if (form.ShowDialog(owner) != DialogResult.OK)
        {
            return null;
        }

        ulong mask = 0;
        for (int i = 0; i < count; i++)
        {
            if (list.GetItemChecked(i))
            {
                mask |= 1UL << i;
            }
        }

        if (mask == 0)
        {
            MessageBox.Show(owner, Strings.Localize("프로세서를 하나 이상 선택해야 합니다."), Strings.Localize("선호도 설정"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        return mask;
    }

    public static string? SendMessage(IWin32Window owner)
    {
        using var form = BaseForm("메시지 보내기", 440, 240);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        var input = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
        var label = new Label { Text = Strings.Localize("보낼 메시지:"), Dock = DockStyle.Top, Height = 24 };
        body.Controls.Add(input);
        body.Controls.Add(label);
        form.Controls.Add(body);
        form.Controls.Add(ButtonRow(form));

        if (form.ShowDialog(owner) != DialogResult.OK || string.IsNullOrWhiteSpace(input.Text))
        {
            return null;
        }

        return input.Text;
    }

    private static Form BaseForm(string title, int width, int height)
    {
        var form = new Form
        {
            Text = Strings.Localize(title),
            Width = width,
            Height = height,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            Font = new Font("Segoe UI Variable Text", 9F),
            BackColor = Theme.Bg,
            ForeColor = Theme.Fg,
        };

        // 대화상자 안의 글자 요소는 모두 만든 뒤에 테마를 맞춘다.
        form.Shown += (_, _) => Theme.ApplyTextColors(form);
        return form;
    }

    private static FlowLayoutPanel ButtonRow(Form form)
    {
        // 디자인: 확인은 강조색 단추, 취소는 테두리 단추. 아래 바는 탐색과 같은 배경을 쓴다.
        var ok = new Button { Text = Strings.Localize("확인"), DialogResult = DialogResult.OK, Width = 90, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Theme.Bg };
        var cancel = new Button { Text = Strings.Localize("취소"), DialogResult = DialogResult.Cancel, Width = 90, FlatStyle = FlatStyle.Flat, BackColor = Theme.Input, ForeColor = Theme.Fg };
        ok.FlatAppearance.BorderSize = 0;
        cancel.FlatAppearance.BorderColor = Theme.Border;
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        // 오른쪽 정렬 흐름이라 취소를 먼저 넣어야 확인이 왼쪽에 놓인다.
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
            BackColor = Theme.Nav,
        };
        panel.Controls.Add(cancel);
        panel.Controls.Add(ok);
        return panel;
    }
}
