using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using Serilog;

namespace VolumeMixer.Audio;

[SupportedOSPlatform("windows")]
public sealed class WindowsAudioSessionController : IAudioSessionController
{
	private readonly ILogger _logger;
	private readonly object _gate = new();
	private readonly Dictionary<string, string?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

	public WindowsAudioSessionController(ILogger logger)
	{
		_logger = logger.ForContext<WindowsAudioSessionController>();
	}

	public IReadOnlyList<AudioApplication> GetApplications()
	{
		lock (_gate)
		{
			var sessions = ReadActiveSessions();
			var currentExecutables = new HashSet<string>(
				sessions.Select(session => session.ExecutablePath).Where(path => path.Length > 0),
				StringComparer.OrdinalIgnoreCase);

			foreach (var stalePath in _iconCache.Keys.Where(path => !currentExecutables.Contains(path)).ToArray())
			{
				_iconCache.Remove(stalePath);
			}

			return sessions
				.GroupBy(session => session.Key, StringComparer.OrdinalIgnoreCase)
				.Select(group =>
				{
					var first = group.First();
					var volume = (int)Math.Round(group.Average(session => session.VolumePercent), MidpointRounding.AwayFromZero);
					return new AudioApplication(
						first.Key,
						first.ProcessId,
						first.ProcessName,
						first.DisplayName,
						first.ExecutablePath,
						first.ExecutablePath.Length > 0 ? GetIconDataUri(first.ExecutablePath) : null,
						Math.Clamp(volume, 0, 100),
						group.All(session => session.IsMuted),
						group.Count(),
						IsActive: true);
				})
				.OrderBy(application => application.DisplayName, StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}
	}

	public bool TryAdjustVolume(string applicationKey, int deltaPercent, out int newVolumePercent, out string? errorMessage)
	{
		newVolumePercent = 0;
		errorMessage = null;

		lock (_gate)
		{
			var matchingSessions = ReadActiveSessions()
				.Where(session => string.Equals(session.Key, applicationKey, StringComparison.OrdinalIgnoreCase))
				.ToArray();
			if (matchingSessions.Length == 0)
			{
				errorMessage = "The selected application's audio session is no longer active.";
				return false;
			}

			var current = (int)Math.Round(matchingSessions.Average(session => session.VolumePercent), MidpointRounding.AwayFromZero);
			newVolumePercent = Math.Clamp(current + deltaPercent, 0, 100);
			var applied = ApplyVolume(applicationKey, newVolumePercent);
			if (!applied)
			{
				errorMessage = "The selected application's audio session ended before its volume could be changed.";
			}

			return applied;
		}
	}

	private List<SessionSnapshot> ReadActiveSessions()
	{
		using var enumerator = new MMDeviceEnumerator();
		using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
		var sessionCollection = device.AudioSessionManager.Sessions;
		var snapshots = new List<SessionSnapshot>(sessionCollection.Count);

		for (var index = 0; index < sessionCollection.Count; index++)
		{
			using var session = sessionCollection[index];
			try
			{
				if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive || session.IsSystemSoundsSession)
				{
					continue;
				}

				var processId = session.GetProcessID;
				if (processId == 0 || processId > int.MaxValue)
				{
					continue;
				}

				var process = DescribeProcess(processId);
				using var volume = session.SimpleAudioVolume;
				snapshots.Add(new SessionSnapshot(
					process.Key,
					(int)processId,
					process.Name,
					ResolveDisplayName(session.DisplayName, process.Name),
					process.ExecutablePath,
					(int)Math.Round(Math.Clamp(volume.Volume, 0f, 1f) * 100f, MidpointRounding.AwayFromZero),
					volume.Mute));
			}
			catch (Exception exception) when (exception is COMException or InvalidOperationException or Win32Exception or ArgumentException)
			{
				_logger.Debug(exception, "A Windows audio session could not be read and was skipped.");
			}
		}

		return snapshots;
	}

	private bool ApplyVolume(string applicationKey, int targetPercent)
	{
		using var enumerator = new MMDeviceEnumerator();
		using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
		var sessionCollection = device.AudioSessionManager.Sessions;
		var applied = 0;

		for (var index = 0; index < sessionCollection.Count; index++)
		{
			using var session = sessionCollection[index];
			try
			{
				if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive || session.IsSystemSoundsSession)
				{
					continue;
				}

				var processId = session.GetProcessID;
				if (processId == 0 || processId > int.MaxValue)
				{
					continue;
				}

				var process = DescribeProcess(processId);
				if (!string.Equals(process.Key, applicationKey, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				using var volume = session.SimpleAudioVolume;
				volume.Volume = targetPercent / 100f;
				applied++;
			}
			catch (Exception exception) when (exception is COMException or InvalidOperationException or Win32Exception or ArgumentException)
			{
				_logger.Debug(exception, "An audio session could not be updated because it ended or became unavailable.");
			}
		}

		return applied > 0;
	}

	private ProcessDescription DescribeProcess(uint processId)
	{
		try
		{
			using var process = Process.GetProcessById((int)processId);
			var processName = process.ProcessName;
			var executablePath = string.Empty;
			try
			{
				executablePath = process.MainModule?.FileName ?? string.Empty;
			}
			catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
			{
				_logger.Debug(exception, "The executable path for process {ProcessId} could not be read.", processId);
			}

			var executableName = Path.GetFileNameWithoutExtension(executablePath);
			if (string.IsNullOrWhiteSpace(executableName))
			{
				executableName = processName;
			}

			var key = string.IsNullOrWhiteSpace(executablePath)
				? $"pid-{processId}"
				: Path.GetFullPath(executablePath);
			return new ProcessDescription(key, processName, executablePath, executableName);
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
		{
			var fallback = $"Process {processId}";
			_logger.Debug(exception, "Process {ProcessId} exited or could not be inspected.", processId);
			return new ProcessDescription($"pid-{processId}", fallback, string.Empty, fallback);
		}
	}

	private string? GetIconDataUri(string executablePath)
	{
		if (_iconCache.TryGetValue(executablePath, out var cached))
		{
			return cached;
		}

		try
		{
			using var icon = Icon.ExtractAssociatedIcon(executablePath);
			if (icon is null)
			{
				_iconCache[executablePath] = null;
				return null;
			}

			using var bitmap = icon.ToBitmap();
			using var stream = new MemoryStream();
			bitmap.Save(stream, ImageFormat.Png);
			var dataUri = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
			_iconCache[executablePath] = dataUri;
			return dataUri;
		}
		catch (Exception exception) when (exception is ArgumentException or ExternalException or IOException or UnauthorizedAccessException)
		{
			_logger.Debug(exception, "The application icon for {ExecutablePath} could not be extracted.", executablePath);
			_iconCache[executablePath] = null;
			return null;
		}
	}

	private static string ResolveDisplayName(string? sessionName, string fallback)
	{
		if (string.IsNullOrWhiteSpace(sessionName))
		{
			return fallback;
		}

		var value = sessionName.Trim();
		if (value.StartsWith('@') || value.StartsWith('{'))
		{
			return fallback;
		}

		return value.Length > 28 ? value[..28] : value;
	}

	private sealed record ProcessDescription(string Key, string Name, string ExecutablePath, string ExecutableName);

	private sealed record SessionSnapshot(
		string Key,
		int ProcessId,
		string ProcessName,
		string DisplayName,
		string ExecutablePath,
		int VolumePercent,
		bool IsMuted);
}
