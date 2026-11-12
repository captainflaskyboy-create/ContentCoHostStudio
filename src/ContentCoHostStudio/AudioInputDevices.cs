using NAudio.CoreAudioApi;

namespace ContentCoHostStudio;

public enum AudioInputKind { Microphone, ComputerAudio }
public sealed record AudioInputSelection(AudioInputKind Kind = AudioInputKind.Microphone, string? DeviceId = null);

// The worker snapshots this immutable selection when it opens capture.
public sealed class AudioInputSettings
{
    private AudioInputSelection current = new();
    public AudioInputSelection Current { get => Volatile.Read(ref current); set => Volatile.Write(ref current, value); }
}

public sealed record AudioDeviceOption(string? Id, string Name, AudioInputKind Kind, bool Available = true)
{
    public override string ToString() => Name;
    public static AudioDeviceOption Default(AudioInputKind kind) => new(null,
        kind == AudioInputKind.Microphone ? "Windows default microphone" : "Windows default playback device", kind);
}

public interface IAudioDeviceCatalog
{
    Task<IReadOnlyList<AudioDeviceOption>> ListAsync(CancellationToken cancellationToken);
}

public sealed class WindowsAudioDeviceCatalog : IAudioDeviceCatalog
{
    public Task<IReadOnlyList<AudioDeviceOption>> ListAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<AudioDeviceOption>>(() =>
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDeviceOption>();
        foreach (var kind in new[] { AudioInputKind.Microphone, AudioInputKind.ComputerAudio })
        {
            var endpoints = enumerator.EnumerateAudioEndPoints(kind == AudioInputKind.Microphone ? DataFlow.Capture : DataFlow.Render, DeviceState.Active);
            foreach (var device in endpoints)
            {
                using (device)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Add(new(device.ID, device.FriendlyName, kind));
                }
            }
        }
        return result;
    }, cancellationToken);
}
