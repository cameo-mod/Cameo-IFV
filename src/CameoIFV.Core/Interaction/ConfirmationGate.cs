namespace CameoIFV.Core.Interaction;

public sealed class ConfirmationGate
{
    private string? _key;
    private DateTimeOffset _armedAt;
    private int _version;

    public ConfirmationGate(TimeSpan window)
    {
        Window = window;
    }

    public TimeSpan Window { get; }

    public int Arm(string key, DateTimeOffset now)
    {
        _key = key;
        _armedAt = now;
        return ++_version;
    }

    public bool Confirm(string key, DateTimeOffset now)
    {
        if (!IsArmed(key, now))
            return false;

        Clear();
        return true;
    }

    public bool IsArmed(string key, DateTimeOffset now, int? version = null)
    {
        if (_key is null || !StringComparer.OrdinalIgnoreCase.Equals(_key, key))
            return false;

        if (version is not null && version != _version)
            return false;

        return now - _armedAt <= Window;
    }

    public bool IsExpired(string key, DateTimeOffset now, int? version = null)
        => _key is not null &&
           StringComparer.OrdinalIgnoreCase.Equals(_key, key) &&
           (version is null || version == _version) &&
           now - _armedAt > Window;

    public void Clear()
    {
        _key = null;
        _version++;
    }
}
