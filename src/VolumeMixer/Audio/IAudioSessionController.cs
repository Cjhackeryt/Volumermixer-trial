namespace VolumeMixer.Audio;

public interface IAudioSessionController
{
	IReadOnlyList<AudioApplication> GetApplications();

	bool TryAdjustVolume(string applicationKey, int deltaPercent, out int newVolumePercent, out string? errorMessage);
}

public sealed class UnavailableAudioSessionController : IAudioSessionController
{
	public IReadOnlyList<AudioApplication> GetApplications() => [];

	public bool TryAdjustVolume(string applicationKey, int deltaPercent, out int newVolumePercent, out string? errorMessage)
	{
		newVolumePercent = 0;
		errorMessage = "Windows audio sessions are unavailable on this platform.";
		return false;
	}
}
