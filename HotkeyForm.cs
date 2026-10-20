namespace BtQuickConnect;

/// <summary>Окно записи кнопки: ждёт комбинацию клавиш или среднюю/боковую кнопку мыши.</summary>
internal sealed class HotkeyForm : Form
{
    private readonly HotkeyManager _manager;
    private readonly Label _prompt;
    private readonly Label _hint;

    /// <summary>Выбранное назначение; null вместе с Cleared — пользователь убрал кнопку.</summary>
    public HotkeyBinding? Result { get; private set; }
    public bool Cleared { get; private set; }

    public HotkeyForm(HotkeyManager manager, HotkeyBinding? current)
    {
        _manager = manager;

        Text = "Кнопка подключить/отключить";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(440, 190);
        Font = new Font("Segoe UI", 9.5f);

        _prompt = new Label
        {
            Text = "Нажмите комбинацию клавиш\nили среднюю / боковую кнопку мыши",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(10, 12),
            Size = new Size(420, 60),
        };
        var current_ = new Label
        {
            Text = "Сейчас: " + (current?.ToString() ?? "не назначено"),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(10, 76),
            Size = new Size(420, 22),
        };
        _hint = new Label
        {
            Text = "Буквы и цифры — только с Ctrl / Alt / Win. Esc — отмена.",
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(10, 100),
            Size = new Size(420, 40),
        };
        var clear = new Button { Text = "Убрать кнопку", Location = new Point(110, 148), Size = new Size(130, 30), TabStop = false };
        clear.Click += (_, _) => { Cleared = true; DialogResult = DialogResult.OK; };
        clear.Enabled = current != null;
        var cancel = new Button { Text = "Отмена", Location = new Point(250, 148), Size = new Size(90, 30), TabStop = false };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;

        Controls.AddRange(new Control[] { _prompt, current_, _hint, clear, cancel });
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        if (!_manager.BeginCapture(OnMouse, out string err)) ShowError(err);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _manager.EndCapture();
        base.OnFormClosed(e);
    }

    private void OnMouse(MouseButtons btn, int mods)
    {
        if (IsDisposed || DialogResult != DialogResult.None) return;
        Accept(new HotkeyBinding { Mouse = btn, Modifiers = mods });
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin
            or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu)
            return true;

        int mods = HotkeyBinding.CurrentModifiers();
        if (key == Keys.Escape && mods == 0)
        {
            DialogResult = DialogResult.Cancel;
            return true;
        }
        Accept(new HotkeyBinding { Key = key, Modifiers = mods });
        return true;
    }

    private void Accept(HotkeyBinding b)
    {
        if (HotkeyBinding.Validate(b) is { } error)
        {
            ShowError(error);   // окно ждёт следующего нажатия
            return;
        }
        Result = b;
        DialogResult = DialogResult.OK;
    }

    public void ShowError(string text)
    {
        _hint.ForeColor = Color.Firebrick;
        _hint.Text = text;
    }
}
