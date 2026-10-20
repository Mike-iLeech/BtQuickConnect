namespace BtQuickConnect;

/// <summary>
/// «Идёт отключение» для одного устройства. Bluetooth.Disconnect по очереди рвёт все профили
/// (A2DP, Hands-Free), а огонёк может оставаться зелёным ещё пару секунд — всё это время
/// новое нажатие не должно ни подключать, ни снова отключать. Оба срока конечны: зависший
/// драйвер или потерянный ответ не оставят кнопку в ожидании навсегда.
/// </summary>
internal sealed class DisconnectState
{
    public static readonly TimeSpan InFlightLimit = TimeSpan.FromSeconds(15);   // сам вызов Disconnect
    public static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(10);     // огонёк ещё зелёный после него

    private string? _address;
    private int _generation;
    private bool _inFlight;
    private DateTime _inFlightUntil = DateTime.MinValue;
    private DateTime _settleUntil = DateTime.MinValue;

    /// <summary>Возвращает номер операции: ответ Finished с чужим номером (прошлое отключение) игнорируется.</summary>
    public int Begin(string address, DateTime now)
    {
        _address = address;
        _inFlight = true;
        _inFlightUntil = now + InFlightLimit;
        _settleUntil = now + InFlightLimit + SettleLimit;
        return ++_generation;
    }

    /// <summary>Disconnect вернулся (успешно или нет). failed — ждать погасания огонька незачем.</summary>
    public void Finished(int operation, DateTime now, bool failed)
    {
        if (operation != _generation) return;   // ответ давно начатого отключения — у текущего свои сроки
        if (!InFlight(now))
        {
            // ответ пришёл позже лимита: ожидание уже сняли, устройство могли подключить заново —
            // не вешаем «отключение…» на подключённое устройство
            _inFlight = false;
            return;
        }
        _inFlight = false;
        _settleUntil = failed ? DateTime.MinValue : now + SettleLimit;
    }

    /// <summary>Опрос увидел устройство отключённым — после возврата Disconnect ждать больше нечего.</summary>
    public void Observe(string? address, bool connected, DateTime now)
    {
        if (!connected && Matches(address) && !InFlight(now)) _settleUntil = DateTime.MinValue;
    }

    public bool InFlight(DateTime now) => _inFlight && now < _inFlightUntil;

    /// <summary>Идёт ли отключение именно этого устройства.</summary>
    public bool Active(string? address, DateTime now) =>
        Matches(address) && (InFlight(now) || now < _settleUntil);

    private bool Matches(string? address) =>
        address != null && string.Equals(address, _address, StringComparison.OrdinalIgnoreCase);
}
