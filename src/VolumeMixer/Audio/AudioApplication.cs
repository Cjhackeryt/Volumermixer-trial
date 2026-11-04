namespace VolumeMixer.Audio;

public sealed record AudioApplication(
	string Key,
	int ProcessId,
	string ProcessName,
	string DisplayName,
	string ExecutablePath,
	string? IconDataUri,
	int VolumePercent,
	bool IsMuted,
	int SessionCount,
	bool IsActive);
