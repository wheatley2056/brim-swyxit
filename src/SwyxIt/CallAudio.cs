using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace Brim.SwyxIt;

/// <summary>
/// Pegel der Gegenseite für den Lichtschein: die Wiedergabe-Sitzungen von SwyxIt! (Core Audio,
/// <c>IAudioMeterInformation</c>). Den Ton spielt meist der Client Line Manager (CLMgr) ab, nicht SwyxIt! selbst.
/// Findet sich keine Sitzung, nimmt es das Standardgerät für Kommunikation – im Gespräch ist das der Anrufer.
/// </summary>
internal sealed class CallAudio : IDisposable
{
    private static readonly TimeSpan Rescan = TimeSpan.FromSeconds(2);
    private static readonly string[] Processes = ["SwyxIt!", "CLMgr"];
    private readonly MMDeviceEnumerator _devices = new();
    private readonly List<AudioSessionControl> _sessions = [];
    private MMDevice? _communications;
    private DateTime _scanned;

    /// <summary>Lautester Pegel (0–1), den SwyxIt! gerade abspielt: die Stimme des Anrufers.</summary>
    public double Peak()
    {
        if (DateTime.UtcNow - _scanned > Rescan)
        {
            Scan();
        }
        try
        {
            if (_sessions.Count == 0)
            {
                return _communications?.AudioMeterInformation.MasterPeakValue ?? 0;
            }
            var peak = 0.0;
            foreach (var session in _sessions)
            {
                peak = Math.Max(peak, session.AudioMeterInformation.MasterPeakValue);
            }
            return peak;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            // Sitzung oder Gerät inzwischen weg; beim nächsten Durchgang neu suchen.
            _scanned = DateTime.MinValue;
            return 0;
        }
    }

    private void Scan()
    {
        _scanned = DateTime.UtcNow;
        _sessions.Clear();
        _communications?.Dispose();
        _communications = null;
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
            foreach (var device in _devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (var i = 0; i < sessions.Count; i++)
                {
                    if (swyx.Contains(sessions[i].GetProcessID))
                    {
                        _sessions.Add(sessions[i]);
                    }
                }
            }
            if (_sessions.Count == 0)
            {
                _communications = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Console.Error.WriteLine($"Audio nicht lesbar: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _communications?.Dispose();
        _devices.Dispose();
    }
}
