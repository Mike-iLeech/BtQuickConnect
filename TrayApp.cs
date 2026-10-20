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
    private readonly Font _boldFont;

    private readonly Settings _settings = Settings.Load();
    private List<BtDevice>? _devices = new();
    private LedState _state = LedState.Red;
    private int _polling;
    private int _pollPeriod = 2000;
    private (string? Address, AudioState? Audio) _lastAudio;   // последний замер звука выбранного устройства
    private DateTime _firstUpAt = DateTime.MinValue;            // когда при подключении поднялся первый профиль
    internal static readonly TimeSpan AllProfilesWait = TimeSpan.FromSeconds(3);
    private bool _busy;               // идёт подключение/отключение
    private DateTime _connectDeadline;
    private readonly DisconnectState _disconnect = new();
    private bool _hotkeyActive;   // кнопка назначена и зарегистрирована
    // время — UtcNow: перевод часов (зимнее время) не растягивает и не обрывает ожидания
    private bool Disconnecting => _disconnect.Active(_settings.Address, DateTime.UtcNow);
    /// <summary>Идёт отключение другого устройства: отключать выбранное пока нельзя, иначе состояние первого потеряется.</summary>
    private bool OtherDisconnecting => !Disconnecting && _disconnect.InFlight(DateTime.UtcNow);
    private bool _blinkOn;

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan UpdateNoticeDelay = TimeSpan.FromSeconds(8);   // успеть прочитать «найдено обновление»
    private string? _balloonUrl;   // куда ведёт щелчок по текущему уведомлению
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 30_000 };
    private ReleaseInfo? _pendingUpdate;
    private bool _updating;
    private readonly Version? _rolledBack;
    private string? _lastAutoError;   // фоновая ошибка показывается один раз, а не каждые 6 часов

    /// <summary>Запущенная новая версия; Program дождётся её отклика после выхода из цикла сообщений.</summary>
    public UpdateHandoff? Handoff { get; private set; }

    public TrayApp(string? updatedFrom = null, string? rolledBack = null)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
        _icons[LedState.Red] = IconArt.MakeIcon(s, IconArt.Red);
        _icons[LedState.Orange] = IconArt.MakeIcon(s, IconArt.Orange);
        _icons[LedState.Green] = IconArt.MakeIcon(s, IconArt.Green);
        _dimIcon = IconArt.MakeIcon(s, IconArt.Dim);
        _boldFont = new Font(_menu.Font, FontStyle.Bold);

        _menu.Opening += (_, _) => BuildMenu();
        _tray.ContextMenuStrip = _menu;
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleSelected(); };
        _tray.Icon = _icons[LedState.Red];
        _tray.Text = "BT Quick Connect";
        _tray.Visible = true;
        _tray.BalloonTipClicked += (_, _) => OpenUrl(_balloonUrl);

        _blink.Tick += (_, _) => OnBlink();
        _poll = new System.Threading.Timer(_ => Poll(), null, 0, 2000);

        _rolledBack = Updater.ParseVersion(rolledBack);
        if (rolledBack != null)
        {
            // новая версия не запустилась — автоматически больше её не ставим (вручную из меню можно)
            _settings.SkippedUpdate = rolledBack;
            TrySave();
        }

        _hotkeys = new HotkeyManager(_ui);
        _hotkeys.Pressed += ToggleSelected;
        _hotkeyActive = _hotkeys.Apply(_settings.Hotkey, out string hkErr) && _settings.Hotkey != null;
        if (_settings.Hotkey != null && !_hotkeyActive)
            Balloon("Кнопка подключить/отключить не работает", hkErr + " Назначьте другую через меню.", ToolTipIcon.Warning);
        else if (rolledBack != null)
            Balloon("Обновление отменено",
                $"Версия {_rolledBack?.ToString(3) ?? rolledBack} не запустилась — возвращена {Updater.Current.ToString(3)}.",
                ToolTipIcon.Warning);
        // заметки забираем всегда: после отката или ручного запуска они лежали бы до следующего обновления
        var notes = Updater.TakeNotes(Updater.Current);
        if (updatedFrom != null && (_settings.Hotkey == null || _hotkeyActive) && rolledBack == null)
        {
            string what = notes is { } n && Updater.BalloonText(n.Notes) is { Length: > 0 } t ? t : "Щёлкните, чтобы посмотреть, что нового.";
            Balloon($"BT Quick Connect обновлён до {Updater.Current.ToString(3)}", what, ToolTipIcon.Info,
                notes?.PageUrl is { Length: > 0 } url ? url : $"https://github.com/{Updater.Repo}/releases/latest");
        }

        // первая проверка через 30 секунд после старта, дальше раз в час
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
        if (_updating || Handoff != null) return;
        if (Updater.ExePath == null)
        {
            if (manual) Balloon("Обновление", "Обновляется только собранный BtQuickConnect.exe.", ToolTipIcon.Info);
            return;
        }
        ReleaseInfo? r;
        try { r = await Updater.FetchLatestAsync(); }
        catch (Exception e)   // async void: всё, что вылетит отсюда, уронит приложение окном ошибки
        {
            string msg = e is UpdateException ? e.Message : "Не удалось проверить обновления: " + e.Message;
            if (manual) Balloon("Не удалось проверить обновления", msg, ToolTipIcon.Warning);
            return;
        }

        if (r == null || !Updater.IsNewer(r))
        {
            _pendingUpdate = null;
            if (manual) Balloon("Обновлений нет", $"Установлена последняя версия {Updater.Current.ToString(3)}.", ToolTipIcon.Info);
            return;
        }

        _pendingUpdate = r;
        // в памяти тоже: если настройки не сохранились, откатившаяся версия всё равно не ставится по кругу
        bool skipped = Updater.ParseVersion(_settings.SkippedUpdate) == r.Version || _rolledBack == r.Version;
        if (manual || (_settings.AutoUpdate && !skipped)) InstallUpdate(r, manual);
        else if (_lastAutoError != "offer:" + r.Tag)
        {
            _lastAutoError = "offer:" + r.Tag;
            Balloon("Доступно обновление", $"Версия {r.Version.ToString(3)}: ПКМ по значку → «Установить обновление».", ToolTipIcon.Info, r.PageUrl);
        }
    }

    private async void InstallUpdate(ReleaseInfo r, bool manual)
    {
        // не перезапускаемся посреди подключения или записи кнопки — дождёмся следующей проверки
        if (_updating || Handoff != null || Updater.ExePath is not { } exe) return;
        if (!manual && (_busy || Disconnecting || _hotkeyForm != null)) return;

        _updating = true;
        try
        {
            Balloon("Найдено обновление BT Quick Connect",
                $"Версия {r.Version.ToString(3)}. Устанавливаю — программа перезапустится через несколько секунд.",
                ToolTipIcon.Info, r.PageUrl);
            string file = await Updater.DownloadAsync(r, exe);
            await Task.Delay(UpdateNoticeDelay);   // иначе уведомление исчезнет вместе со значком почти сразу
            if (!manual && (_busy || Disconnecting || _hotkeyForm != null)) return;   // пока ждали, начали подключать
            Updater.SaveNotes(r);
            Handoff = Updater.InstallAndStart(file, exe, r.Version);
            Exit();
        }
        catch (Exception e)
        {
            string msg = e is UpdateException ? e.Message : "Не удалось установить обновление: " + e.Message;
            if (manual || _lastAutoError != r.Tag + msg)
            {
                _lastAutoError = r.Tag + msg;
                Balloon("Обновление не установлено", msg, ToolTipIcon.Warning);
            }
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
            string? addr = _settings.Address;
            AudioState? audio = null;
            if (addr != null && devices != null)
            {
                bool radio = devices.Any(d => d.Connected && d.Address.Equals(addr, StringComparison.OrdinalIgnoreCase));
                try { audio = Bluetooth.ReadAudio(addr, radio); }
                catch { /* аудиоподсистема недоступна — судим по радиосоединению */ }
            }
            _ui.Post(_ => { _lastAudio = (addr, audio); Apply(WithAudioState(devices, addr, audio?.Connected)); }, null);
        }
        catch
        {
            // опрос упал — всё равно пересчитать сроки (мигание, быстрый опрос) по последним данным;
            // Post при выходе может бросить, а исключение в таймере убило бы процесс (и откат обновления)
            try { _ui.Post(_ => Apply(_devices), null); } catch { }
        }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    /// <summary>
    /// Для выбранного аудиоустройства «подключено» = есть звук (активно аудиоустройство Windows),
    /// а не радиосоединение: оно держится ~3 с после отключения и опаздывает при подключении.
    /// </summary>
    internal static List<BtDevice>? WithAudioState(List<BtDevice>? devices, string? address, bool? audio)
    {
        if (devices == null || address == null || audio == null) return devices;
        return devices.Select(d => d.Address.Equals(address, StringComparison.OrdinalIgnoreCase)
            ? d with { Connected = audio.Value } : d).ToList();
    }

    /// <summary>
    /// Подключение закончено: подняты все профили (стерео тоже), либо звук не определить по
    /// аудиоустройствам, либо с первого профиля прошло 3 с (какой-то профиль так и не поднимется).
    /// </summary>
    internal static bool ConnectDone(AudioState? audio, TimeSpan sinceFirstUp) =>
        audio?.Connected != true || audio.AllUp || sinceFirstUp >= AllProfilesWait;

    /// <summary>
    /// Отключение прошло успешно, а звук по аудиоустройствам не понять (выключены в панели звука):
    /// показываем «отключено» сразу, не дожидаясь, пока Windows разорвёт радиосоединение (~3 с).
    /// </summary>
    internal static bool ShowAsDisconnected(bool connected, bool? audio, bool settling) =>
        connected && audio == null && settling;

    /// <summary>
    /// Цвет огонька: не выбрано / не спарено / BT выключен — красный; подключено (и не «уже отключено»
    /// после успешной команды) — зелёный; иначе оранжевый. sel.Connected уже учитывает звук (WithAudioState).
    /// </summary>
    internal static LedState LedFor(BtDevice? sel, bool? audio, bool settling) =>
        sel == null ? LedState.Red
        : sel.Connected && !ShowAsDisconnected(sel.Connected, audio, settling) ? LedState.Green
        : LedState.Orange;

    /// <summary>Пока идёт подключение или отключение, опрашиваем часто — огонёк меняется сразу.</summary>
    private void SetFastPoll(bool fast)
    {
        int period = fast ? 250 : 2000;
        if (period == _pollPeriod) return;
        _pollPeriod = period;
        _poll.Change(fast ? 0 : period, period);
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

        var now = DateTime.UtcNow;
        var audio = _lastAudio.Address != null && sel != null
            && _lastAudio.Address.Equals(sel.Address, StringComparison.OrdinalIgnoreCase) ? _lastAudio.Audio : null;
        _state = LedFor(sel, audio?.Connected, _disconnect.Settling(_settings.Address, now));

        if (_busy && _state == LedState.Green)
        {
            if (_firstUpAt == DateTime.MinValue) _firstUpAt = now;
            if (ConnectDone(audio, now - _firstUpAt)) StopBusy();
        }
        // по настоящему состоянию, а не по показанному: иначе оптимистичный оранжевый сам снимал бы ожидание
        _disconnect.Observe(_settings.Address, sel?.Connected == true, now);
        if (_busy && DateTime.UtcNow > _connectDeadline)
        {
            StopBusy();
            // срок вышел, пока ждали стерео, а звук уже есть — это не провал подключения
            if (_state != LedState.Green)
                Balloon("Не удалось подключиться", $"{_settings.Name} не ответило. Проверьте, что устройство включено и рядом.",
                ToolTipIcon.Warning);
        }

        if (!_busy) _tray.Icon = _icons[_state];
        _tray.Text = Truncate(TooltipText());
        SetFastPoll(_busy || Disconnecting || _disconnect.InFlight(DateTime.UtcNow));
    }

    private string TooltipText()
    {
        if (_devices == null) return "Bluetooth выключен или адаптер не найден";
        if (_settings.Address == null) return "Устройство не выбрано — ПКМ → «Выбрать устройство»";
        string name = _settings.Name ?? _settings.Address;
        if (_busy) return $"{name}: подключение…";
        if (Disconnecting) return $"{name}: отключение…";
        if (OtherDisconnecting && _state == LedState.Green) return $"{name}: подключено (ждём, пока отключится прежнее устройство)";
        return _state switch
        {
            LedState.Green => $"{name}: подключено (двойной клик" + (_hotkeyActive ? " или кнопка" : "") + " — отключить)",
            LedState.Orange => $"{name}: не подключено (двойной клик" + (_hotkeyActive ? " или кнопка" : "") + " — подключить)",
            _ => $"{name}: не спарено с компьютером",
        };
    }

    private static string Truncate(string s) => s.Length <= 127 ? s : s[..127];

    // ---------- действия ----------

    internal enum ToggleAction { Connect, Disconnect, Wait }

    /// <summary>Что делает кнопка: подключено — отключить, иначе — подключить; пока идёт прошлая команда — ничего.</summary>
    internal static ToggleAction DecideToggle(bool connected, bool connecting, bool disconnecting) =>
        connecting || disconnecting ? ToggleAction.Wait
        : connected ? ToggleAction.Disconnect : ToggleAction.Connect;

    /// <summary>Двойной клик и назначенная кнопка переключают: подключают отключённое устройство и отключают подключённое.</summary>
    private void ToggleSelected()
    {
        bool connected = Selected?.Connected == true;
        switch (DecideToggle(connected, _busy, Disconnecting || (connected && OtherDisconnecting)))
        {
            case ToggleAction.Connect: ConnectSelected(); break;
            case ToggleAction.Disconnect: DisconnectSelected(); break;
        }
    }

    private void ConnectSelected()
    {
        if (_busy || Disconnecting) return;   // не подключаем, пока отключение ещё рвёт профили
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
            KsResult r;
            string err;
            try { r = Bluetooth.Connect(addr, out err); }
            catch (Exception e) { r = KsResult.Failed; err = e.Message; }   // иначе 20 с ложного «подключение…»
            _ui.Post(_ =>
            {
                if (r != KsResult.Ok)
                {
                    StopBusy();
                    Balloon("Не удалось подключиться", err, ToolTipIcon.Warning);
                }
                Task.Run(Poll);
            }, null);
        });
    }

    private void DisconnectSelected()
    {
        if (_busy || Disconnecting || OtherDisconnecting || Selected is not { Connected: true } sel) return;
        string addr = sel.Address;
        // пока Windows рвёт связь, огонёк ещё зелёный — повторное нажатие не должно снова слать отключение
        int op = _disconnect.Begin(addr, DateTime.UtcNow);
        SetFastPoll(true);
        _tray.Text = Truncate(TooltipText());
        Task.Run(() =>
        {
            KsResult r;
            string err;
            try { r = Bluetooth.Disconnect(addr, out err); }
            catch (Exception e) { r = KsResult.Failed; err = e.Message; }   // ответ в UI должен прийти всегда
            _ui.Post(_ =>
            {
                _disconnect.Finished(op, DateTime.UtcNow, failed: r != KsResult.Ok);
                _tray.Text = Truncate(TooltipText());
                if (r != KsResult.Ok)
                {
                    Balloon("Не удалось отключить", err, ToolTipIcon.Warning);
                }
                Task.Run(Poll);
            }, null);
        });
    }

    private void StartBusy(TimeSpan timeout)
    {
        _firstUpAt = DateTime.MinValue;
        SetFastPoll(true);
        _busy = true;
        _connectDeadline = DateTime.UtcNow + timeout;
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

    private void Balloon(string title, string text, ToolTipIcon icon, string? url = null)
    {
        _balloonUrl = url;
        _tray.ShowBalloonTip(5000, title, text, icon);
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* нет браузера по умолчанию */ }
    }

    private void TrySave()
    {
        try { _settings.Save(); }
        catch (Exception ex) { Balloon("Не удалось сохранить настройки", ex.Message, ToolTipIcon.Error); }
    }

    // ---------- меню ----------

    private void BuildMenu()
    {
        // пункты и их картинки пересоздаются при каждом открытии — старые освобождаем сразу
        var old = _menu.Items.Cast<ToolStripItem>().ToList();
        _menu.Items.Clear();
        foreach (var item in old) DisposeItem(item);
        var sel = Selected;

        var header = new ToolStripMenuItem(TooltipText()) { Enabled = false };
        _menu.Items.Add(header);
        _menu.Items.Add(new ToolStripSeparator());

        var connect = new ToolStripMenuItem("Подключить", null, (_, _) => ConnectSelected())
        {
            Enabled = !_busy && !Disconnecting && sel is { Connected: false },
            Font = _boldFont,
        };
        _menu.Items.Add(connect);
        _menu.Items.Add(new ToolStripMenuItem("Отключить", null, (_, _) => DisconnectSelected())
        {
            Enabled = !_busy && !Disconnecting && !OtherDisconnecting && sel is { Connected: true },
        });
        _menu.Items.Add(new ToolStripSeparator());

        var choose = new ToolStripMenuItem("Выбрать устройство");
        List<BtDevice>? devices;
        try { devices = WithAudioState(Bluetooth.GetPairedDevices(), _lastAudio.Address, _lastAudio.Audio?.Connected) ?? _devices; }
        catch { devices = _devices; }   // стек Bluetooth недоступен — показываем последний опрос
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
                    // выбранное устройство — тем же цветом, что огонёк в трее (в т.ч. «уже отключено»)
                    Image = ((d.Address.Equals(_settings.Address, StringComparison.OrdinalIgnoreCase)
                                ? _state == LedState.Green : d.Connected)
                        ? _icons[LedState.Green] : _icons[LedState.Orange]).ToBitmap(),
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
        _menu.Items.Add(new ToolStripMenuItem($"Кнопка подключить/отключить: {hk}…", null, (_, _) => EditHotkey()));
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

    private static void DisposeItem(ToolStripItem item)
    {
        if (item is ToolStripMenuItem mi)
        {
            var subs = mi.DropDownItems.Cast<ToolStripItem>().ToList();
            mi.DropDownItems.Clear();
            foreach (var s in subs) DisposeItem(s);
        }
        var img = item.Image;
        item.Image = null;
        img?.Dispose();
        item.Dispose();
    }

    private void Select(BtDevice? d)
    {
        Bluetooth.ForgetContainer(d?.Address);
        _lastAudio = default;   // прежний замер звука относился к другому устройству
        _settings.Address = d?.Address;
        _settings.Name = d?.Name;
        TrySave();
        if (_busy) StopBusy();
        Apply(_devices);
        Task.Run(Poll);
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
                _hotkeyActive = false;   // окно записи сняло регистрацию — подсказка не должна обещать кнопку
                _tray.Text = Truncate(TooltipText());
                var result = form.ShowDialog();
                _hotkeyForm = null;

                if (result != DialogResult.OK)
                {
                    _hotkeyActive = _hotkeys.Apply(_settings.Hotkey, out string restoreErr) && _settings.Hotkey != null;
                    _tray.Text = Truncate(TooltipText());
                    if (_settings.Hotkey != null && !_hotkeyActive)   // вернуть прежнее назначение не удалось
                        Balloon("Кнопка подключить/отключить не работает", restoreErr, ToolTipIcon.Warning);
                    return;
                }
                var chosen = form.Cleared ? null : form.Result;
                if (_hotkeys.Apply(chosen, out error))
                {
                    _settings.Hotkey = chosen;
                    _hotkeyActive = chosen != null;
                    _tray.Text = Truncate(TooltipText());
                    TrySave();
                    if (chosen != null)
                        Balloon("Кнопка назначена", $"{chosen} — подключить или отключить {_settings.Name ?? "выбранное устройство"}.", ToolTipIcon.Info);
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
        _boldFont.Dispose();
    }
}
