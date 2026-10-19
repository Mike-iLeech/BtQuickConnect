namespace BtQuickConnect;

internal enum LedState { Red, Orange, Green }

internal sealed class TrayApp : IDisposable
{
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _poll;
    private readonly System.Windows.Forms.Timer _blink = new() { Interval = 400 };
    private readonly Dictionary<LedState, Icon> _icons = new();
    private readonly Icon _dimIcon;
    private readonly HotkeyManager _hotkeys;
    private HotkeyForm? _hotkeyForm;

    private readonly Settings _settings = Settings.Load();
    private List<BtDevice>? _devices = new();
    private LedState _state = LedState.Red;
    private int _polling;
    private bool _busy;               // идёт подключение/отключение
    private DateTime _connectDeadline;
    private bool _blinkOn;

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 30_000 };
    private ReleaseInfo? _pendingUpdate;
    private bool _updating;

    public TrayApp(string? updatedFrom = null)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
        _icons[LedState.Red] = IconArt.MakeIcon(s, IconArt.Red);
        _icons[LedState.Orange] = IconArt.MakeIcon(s, IconArt.Orange);
        _icons[LedState.Green] = IconArt.MakeIcon(s, IconArt.Green);
        _dimIcon = IconArt.MakeIcon(s, IconArt.Dim);

        _menu.Opening += (_, _) => BuildMenu();
        _tray.ContextMenuStrip = _menu;
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ConnectSelected(); };
        _tray.Icon = _icons[LedState.Red];
        _tray.Text = "BT Quick Connect";
        _tray.Visible = true;

        _blink.Tick += (_, _) => OnBlink();
        _poll = new System.Threading.Timer(_ => Poll(), null, 0, 2000);

        _hotkeys = new HotkeyManager(_ui);
        _hotkeys.Pressed += ConnectSelected;
        if (!_hotkeys.Apply(_settings.Hotkey, out string hkErr))
            Balloon("Кнопка подключения не работает", hkErr + " Назначьте другую через меню.", ToolTipIcon.Warning);
        else if (updatedFrom != null)
            Balloon("BT Quick Connect обновлён", $"Установлена версия {Updater.Current.ToString(3)}.", ToolTipIcon.Info);

        // первая проверка через 30 секунд после старта, дальше раз в 6 часов
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = (int)UpdateInterval.TotalMilliseconds;
            CheckUpdates(manual: false);
        };
        if (Updater.ExePath != null) _updateTimer.Start();
    }

    // ---------- обновление ----------

    private async void CheckUpdates(bool manual)
    {
        if (_updating) return;
        if (Updater.ExePath == null)
        {
            if (manual) Balloon("Обновление", "Обновляется только собранный BtQuickConnect.exe.", ToolTipIcon.Info);
            return;
        }
        ReleaseInfo? r;
        try { r = await Updater.FetchLatestAsync(); }
        catch (UpdateException e)
        {
            if (manual) Balloon("Не удалось проверить обновления", e.Message, ToolTipIcon.Warning);
            return;
        }

        if (r == null || !Updater.IsNewer(r))
        {
            _pendingUpdate = null;
            if (manual) Balloon("Обновлений нет", $"Установлена последняя версия {Updater.Current.ToString(3)}.", ToolTipIcon.Info);
            return;
        }

        _pendingUpdate = r;
        if (manual || _settings.AutoUpdate) InstallUpdate(r, manual);
        else Balloon("Доступно обновление", $"Версия {r.Version.ToString(3)}: ПКМ по значку → «Установить обновление».", ToolTipIcon.Info);
    }

    private async void InstallUpdate(ReleaseInfo r, bool manual)
    {
        // не перезапускаемся посреди подключения или записи кнопки — дождёмся следующей проверки
        if (_updating || Updater.ExePath is not { } exe) return;
        if (!manual && (_busy || _hotkeyForm != null)) return;

        _updating = true;
        try
        {
            string file = await Updater.DownloadAsync(r, exe);
            Updater.InstallAndRestart(file, exe);
            Exit();
        }
        catch (UpdateException e)
        {
            Balloon("Обновление не установлено", e.Message, ToolTipIcon.Warning);
        }
        finally { _updating = false; }
    }

    // ---------- состояние ----------

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var devices = Bluetooth.GetPairedDevices();
            _ui.Post(_ => Apply(devices), null);
        }
        catch { /* следующий тик попробует снова */ }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private BtDevice? Selected =>
        _settings.Address == null ? null
            : _devices?.FirstOrDefault(d => d.Address.Equals(_settings.Address, StringComparison.OrdinalIgnoreCase));

    private void Apply(List<BtDevice>? devices)
    {
        _devices = devices;
        var sel = Selected;

        if (sel != null && sel.Name.Length > 0 && sel.Name != _settings.Name)
        {
            _settings.Name = sel.Name;
            TrySave();
        }

        _state = sel == null ? LedState.Red : sel.Connected ? LedState.Green : LedState.Orange;

        if (_busy && _state == LedState.Green) StopBusy();
        if (_busy && DateTime.Now > _connectDeadline)
        {
            StopBusy();
            Balloon("Не удалось подключиться", $"{_settings.Name} не ответило. Проверьте, что устройство включено и рядом.",
                ToolTipIcon.Warning);
        }

        if (!_busy) _tray.Icon = _icons[_state];
        _tray.Text = Truncate(TooltipText());
    }

    private string TooltipText()
    {
        if (_devices == null) return "Bluetooth выключен или адаптер не найден";
        if (_settings.Address == null) return "Устройство не выбрано — ПКМ → «Выбрать устройство»";
        string name = _settings.Name ?? _settings.Address;
        if (_busy) return $"{name}: подключение…";
        return _state switch
        {
            LedState.Green => $"{name}: подключено",
            LedState.Orange => $"{name}: не подключено (двойной клик — подключить)",
            _ => $"{name}: не спарено с компьютером",
        };
    }

    private static string Truncate(string s) => s.Length <= 127 ? s : s[..127];

    // ---------- действия ----------

    private void ConnectSelected()
    {
        if (_busy) return;
        if (_settings.Address == null)
        {
            Balloon("Устройство не выбрано", "Щёлкните правой кнопкой по значку → «Выбрать устройство».", ToolTipIcon.Info);
            return;
        }
        if (_devices == null)
        {
            Balloon("Bluetooth выключен", "Включите Bluetooth на компьютере.", ToolTipIcon.Warning);
            return;
        }
        var sel = Selected;
        if (sel == null)
        {
            Balloon("Устройство не спарено", $"{_settings.Name} больше не спарено с компьютером. Спарьте его в параметрах Windows.",
                ToolTipIcon.Warning);
            return;
        }
        if (sel.Connected) return;

        string addr = sel.Address;
        StartBusy(TimeSpan.FromSeconds(20));
        Task.Run(() =>
        {
            var r = Bluetooth.Connect(addr, out string err);
            _ui.Post(_ =>
            {
                if (r != KsResult.Ok)
                {
                    StopBusy();
                    Balloon("Не удалось подключиться", err, ToolTipIcon.Warning);
                }
                Poll();
            }, null);
        });
    }

    private void DisconnectSelected()
    {
        if (_busy || Selected is not { Connected: true } sel) return;
        string addr = sel.Address;
        Task.Run(() =>
        {
            var r = Bluetooth.Disconnect(addr, out string err);
            _ui.Post(_ =>
            {
                if (r != KsResult.Ok) Balloon("Не удалось отключить", err, ToolTipIcon.Warning);
                Poll();
            }, null);
        });
    }

    private void StartBusy(TimeSpan timeout)
    {
        _busy = true;
        _connectDeadline = DateTime.Now + timeout;
        _blinkOn = false;
        _blink.Start();
        _tray.Text = Truncate(TooltipText());
    }

    private void StopBusy()
    {
        _busy = false;
        _blink.Stop();
        _tray.Icon = _icons[_state];
        _tray.Text = Truncate(TooltipText());
    }

    private void OnBlink()
    {
        _blinkOn = !_blinkOn;
        _tray.Icon = _blinkOn ? _icons[LedState.Orange] : _dimIcon;
    }

    private void Balloon(string title, string text, ToolTipIcon icon) =>
        _tray.ShowBalloonTip(5000, title, text, icon);

    private void TrySave()
    {
        try { _settings.Save(); }
        catch (Exception ex) { Balloon("Не удалось сохранить настройки", ex.Message, ToolTipIcon.Error); }
    }

    // ---------- меню ----------

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var sel = Selected;

        var header = new ToolStripMenuItem(TooltipText()) { Enabled = false };
        _menu.Items.Add(header);
        _menu.Items.Add(new ToolStripSeparator());

        var connect = new ToolStripMenuItem("Подключить", null, (_, _) => ConnectSelected())
        {
            Enabled = !_busy && sel is { Connected: false },
            Font = new Font(_menu.Font, FontStyle.Bold),
        };
        _menu.Items.Add(connect);
        _menu.Items.Add(new ToolStripMenuItem("Отключить", null, (_, _) => DisconnectSelected())
        {
            Enabled = !_busy && sel is { Connected: true },
        });
        _menu.Items.Add(new ToolStripSeparator());

        var choose = new ToolStripMenuItem("Выбрать устройство");
        var devices = Bluetooth.GetPairedDevices() ?? _devices;
        if (devices == null || devices.Count == 0)
        {
            choose.DropDownItems.Add(new ToolStripMenuItem(
                devices == null ? "Bluetooth выключен" : "Нет спаренных устройств") { Enabled = false });
        }
        else
        {
            foreach (var d in devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new ToolStripMenuItem(d.Name.Length > 0 ? d.Name : d.Address)
                {
                    Checked = d.Address.Equals(_settings.Address, StringComparison.OrdinalIgnoreCase),
                    Image = (d.Connected ? _icons[LedState.Green] : _icons[LedState.Orange]).ToBitmap(),
                };
                var dev = d;
                item.Click += (_, _) => Select(dev);
                choose.DropDownItems.Add(item);
            }
        }
        if (_settings.Address != null)
        {
            choose.DropDownItems.Add(new ToolStripSeparator());
            choose.DropDownItems.Add(new ToolStripMenuItem("Сбросить выбор", null, (_, _) => Select(null)));
        }
        _menu.Items.Add(choose);

        string hk = _settings.Hotkey?.ToString() ?? "не назначена";
        _menu.Items.Add(new ToolStripMenuItem($"Кнопка подключения: {hk}…", null, (_, _) => EditHotkey()));
        _menu.Items.Add(new ToolStripMenuItem("Параметры Bluetooth Windows…", null, (_, _) => OpenWindowsSettings()));
        _menu.Items.Add(new ToolStripSeparator());

        var auto = new ToolStripMenuItem("Запускать вместе с Windows") { Checked = SafeAutostart() };
        auto.Click += (_, _) =>
        {
            try { Autostart.Enabled = !auto.Checked; }
            catch (Exception ex) { Balloon("Автозапуск", ex.Message, ToolTipIcon.Error); }
        };
        _menu.Items.Add(auto);

        var autoUpd = new ToolStripMenuItem("Обновлять автоматически") { Checked = _settings.AutoUpdate };
        autoUpd.Click += (_, _) => { _settings.AutoUpdate = !_settings.AutoUpdate; TrySave(); };
        _menu.Items.Add(autoUpd);
        if (_pendingUpdate is { } upd && Updater.IsNewer(upd))
            _menu.Items.Add(new ToolStripMenuItem($"Установить обновление {upd.Version.ToString(3)}", null,
                (_, _) => InstallUpdate(upd, manual: true)) { Enabled = !_updating });
        else
            _menu.Items.Add(new ToolStripMenuItem($"Проверить обновления (версия {Updater.Current.ToString(3)})", null,
                (_, _) => CheckUpdates(manual: true)) { Enabled = !_updating });

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => Exit()));
    }

    private static bool SafeAutostart()
    {
        try { return Autostart.Enabled; } catch { return false; }
    }

    private void Select(BtDevice? d)
    {
        _settings.Address = d?.Address;
        _settings.Name = d?.Name;
        TrySave();
        if (_busy) StopBusy();
        Apply(_devices);
        Poll();
    }

    private void EditHotkey()
    {
        if (_hotkeyForm != null) { _hotkeyForm.Activate(); return; }

        string? error = null;
        try
        {
            while (true)
            {
                using var form = _hotkeyForm = new HotkeyForm(_hotkeys, _settings.Hotkey);
                if (error != null) form.ShowError(error);
                var result = form.ShowDialog();
                _hotkeyForm = null;

                if (result != DialogResult.OK)
                {
                    _hotkeys.Apply(_settings.Hotkey, out _);   // вернуть прежнее назначение
                    return;
                }
                var chosen = form.Cleared ? null : form.Result;
                if (_hotkeys.Apply(chosen, out error))
                {
                    _settings.Hotkey = chosen;
                    TrySave();
                    if (chosen != null)
                        Balloon("Кнопка назначена", $"{chosen} — подключить {_settings.Name ?? "выбранное устройство"}.", ToolTipIcon.Info);
                    return;
                }
            }
        }
        finally { _hotkeyForm = null; }
    }

    private static void OpenWindowsSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch { /* нет обработчика ms-settings */ }
    }

    private void Exit()
    {
        _tray.Visible = false;
        Application.Exit();
    }

    public void Dispose()
    {
        _poll.Dispose();
        _blink.Dispose();
        _updateTimer.Dispose();
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
        _dimIcon.Dispose();
    }
}
