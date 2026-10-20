using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Brim.SwyxIt;

/// <summary>
/// Pegel der Gegenseite für den Lichtschein: die Wiedergabe-Sitzungen von SwyxIt! (Core Audio,
/// <c>IAudioMeterInformation</c>). Den Ton spielt meist der Client Line Manager (CLMgr) ab, nicht SwyxIt! selbst.
/// Findet sich keine Sitzung, nimmt es das Standardgerät für Kommunikation – im Gespräch ist das der Anrufer.
/// <para>
/// Läuft auf einem eigenen Thread und nur während eines Gesprächs, damit es weder den STA-Thread (Ereignisse von
/// SwyxIt!) noch den Ton aufhält. Es liest nur die beiden Standard-Wiedergabegeräte, nicht alle Geräte.
/// </para>
/// </summary>
internal sealed class CallAudio : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(66);
    private static readonly TimeSpan Rescan = TimeSpan.FromSeconds(5);
    private static readonly string[] Processes = ["SwyxIt!", "CLMgr"];

    private readonly Lock _gate = new();
    private readonly System.Threading.Timer _timer;
    private readonly List<IDisposable> _owned = [];
    private readonly List<AudioMeterInformation> _meters = [];
    private MMDeviceEnumerator? _devices;
    private DateTime _scanned;
    private bool _running;
    private bool _disposed;
    private double _level;

    public CallAudio() => _timer = new System.Threading.Timer(_ => Tick());

    /// <summary>Neuer geglätteter Pegel (0–1); kommt auf einem Hintergrund-Thread.</summary>
    public event Action<double>? LevelChanged;

    /// <summary>Messen ein- oder ausschalten (nur während eines Gesprächs).</summary>
    public bool Running
    {
        get => _running;
        set
        {
            lock (_gate)
            {
                if (_running == value || _disposed)
                {
                    return;
                }
                _running = value;
                _level = 0;
                // Auch beim Ausschalten einmal laufen: Der Durchgang gibt die Geräte auf seinem Thread frei.
                _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (!_running || _disposed)
            {
                Release();
                return;
            }
            var level = CallTracker.Glow(_level, Peak());
            if (Math.Abs(level - _level) >= 0.02)
            {
                _level = level;
                LevelChanged?.Invoke(level);
            }
            // Nächster Durchgang erst nach diesem: Nie zwei Messungen gleichzeitig.
            _timer.Change(Interval, Timeout.InfiniteTimeSpan);
        }
    }

    private double Peak()
    {
        if (DateTime.UtcNow - _scanned > Rescan)
        {
            Scan();
        }
        try
        {
            var peak = 0.0;
            foreach (var meter in _meters)
            {
                peak = Math.Max(peak, meter.MasterPeakValue);
            }
            return peak;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Gerät inzwischen weg; beim nächsten Durchgang neu suchen.
            _scanned = DateTime.MinValue;
            return 0;
        }
    }

    private void Scan()
    {
        Release();
        _scanned = DateTime.UtcNow;
        var swyx = Processes.SelectMany(Process.GetProcessesByName).Select(p =>
        {
            using (p)
            {
                return (uint)p.Id;
            }
        }).ToHashSet();
        if (swyx.Count == 0)
        {
            return;
        }
        try
        {
            _devices ??= new MMDeviceEnumerator();
            MMDevice? fallback = null;
            var seen = new HashSet<string>();
            foreach (var role in new[] { Role.Communications, Role.Multimedia })
            {
                var device = _devices.GetDefaultAudioEndpoint(DataFlow.Render, role);
                if (!seen.Add(device.ID))
                {
                    device.Dispose();
                    continue;
                }
                _owned.Add(device);
                fallback ??= device;
                var manager = device.AudioSessionManager;
                _owned.Add(manager);
                var sessions = manager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    _owned.Add(session);
                    if (swyx.Contains(session.GetProcessID))
                    {
                        _meters.Add(session.AudioMeterInformation);
                    }
                }
            }
            if (_meters.Count == 0 && fallback is not null)
            {
                _meters.Add(fallback.AudioMeterInformation);
            }
        }
        catch (COMException ex)
        {
            Console.Error.WriteLine($"Audio nicht lesbar: {ex.Message}");
        }
    }

    private void Release()
    {
        _meters.Clear();
        foreach (var owned in _owned)
        {
            try
            {
                owned.Dispose();
            }
            catch (COMException)
            {
            }
        }
        _owned.Clear();
        _scanned = DateTime.MinValue;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _running = false;
            _timer.Dispose();
            Release();
            _devices?.Dispose();
            _devices = null;
        }
    }
}
