using NAudio.CoreAudioApi;

namespace SdrCapture;

// A slow audio driver must never hold the tray/UI thread or accumulate one worker per window.
static class SettingsAudioDiscovery
{
    static readonly object gate=new();
    static Task<DiscordDevices.Endpoint[]>? pending;
    public static Task<DiscordDevices.Endpoint[]> Load()
    {
        lock(gate)
            return pending is {IsCompleted:false}?pending:pending=Task.Run(()=>DiscordDevices.Endpoints().ToArray());
    }
}
