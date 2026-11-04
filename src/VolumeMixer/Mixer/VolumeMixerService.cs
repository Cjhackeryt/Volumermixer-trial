using System.ComponentModel;
using System.Runtime.InteropServices;
using Serilog;
using VolumeMixer.Audio;

namespace VolumeMixer.Mixer;

public sealed class VolumeMixerService : IAsyncDisposable
{
	public const int SlotCount = 6;
	public const int VolumeStep = 5;

	private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

	private readonly IAudioSessionController _audio;
	private readonly ILogger _logger;
	private readonly object _gate = new();
	private AudioApplication[] _applications = [];
	private Timer? _timer;
	private bool _started;
	private int _page;
	private int _polling;
	private DateTimeOffset _lastFailureLog = DateTimeOffset.MinValue;

	public VolumeMixerService(IAudioSessionController audio, ILogger logger)
	{
		_audio = audio;
		_logger = logger.ForContext<VolumeMixerService>();
	}

	public event Action? Changed;

	public IReadOnlyList<AudioApplication?> GetSlotsForPage(int page)
	{
		lock (_gate)
		{
			var slots = new AudioApplication?[SlotCount];
			var start = Math.Clamp(page, 0, GetPageCountLocked() - 1) * SlotCount;
			for (var column = 0; column < SlotCount && start + column < _applications.Length; column++)
			{
				slots[column] = _applications[start + column];
			}

			return slots;
		}
	}

	public int GetPageCount()
	{
		lock (_gate)
		{
			return GetPageCountLocked();
		}
	}

	public int GetTotalCount()
	{
		lock (_gate)
		{
			return _applications.Length;
		}
	}

	public IReadOnlyList<AudioApplication> GetApplications()
	{
		lock (_gate)
		{
			return [.. _applications];
		}
	}

	public int GetPage()
	{
		lock (_gate)
		{
			return _page;
		}
	}

	public void SelectPage(int page)
	{
		lock (_gate)
		{
			_page = Math.Clamp(page, 0, GetPageCountLocked() - 1);
		}
	}

	public void Start()
	{
		lock (_gate)
		{
			if (_started)
			{
				return;
			}

			_started = true;
		}

		RefreshNow();
		_logger.Information(
			"Windows audio session discovery found {ApplicationCount} active applications.",
			GetTotalCount());
		_timer = new Timer(
			_ => RefreshNow(),
			null,
			RefreshInterval,
			RefreshInterval);
	}

	public void RefreshNow()
	{
		if (Interlocked.Exchange(ref _polling, 1) != 0)
		{
			return;
		}

		try
		{
			IReadOnlyList<AudioApplication> current;
			try
			{
				current = _audio.GetApplications().ToArray();
			}
			catch (Exception exception) when (
				exception is COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or Win32Exception)
			{
				var now = DateTimeOffset.UtcNow;
				if (now - _lastFailureLog >= TimeSpan.FromMinutes(1))
				{
					_lastFailureLog = now;
					_logger.Warning(exception, "Windows audio applications could not be refreshed.");
				}

				return;
			}

			var changed = false;
			lock (_gate)
			{
				var ordered = current
					.OrderBy(application => application.DisplayName, StringComparer.OrdinalIgnoreCase)
					.ThenBy(application => application.Key, StringComparer.OrdinalIgnoreCase)
					.ToArray();
				if (!_applications.SequenceEqual(ordered))
				{
					_applications = ordered;
					_page = Math.Clamp(_page, 0, GetPageCountLocked() - 1);
					changed = true;
				}
			}

			if (changed)
			{
				_logger.Information(
					"Active Windows audio application count changed to {ApplicationCount}.",
					GetTotalCount());
				Changed?.Invoke();
			}
		}
		finally
		{
			Volatile.Write(ref _polling, 0);
		}
	}

	public bool TryAdjustSlotVolume(
		int page,
		int column,
		int direction,
		out AudioApplication? application,
		out int newVolumePercent,
		out string? error)
	{
		application = null;
		newVolumePercent = 0;
		error = null;
		if (column is < 0 or >= SlotCount || direction is not (-1 or 1))
		{
			error = "The mixer column or volume direction is invalid.";
			return false;
		}

		SelectPage(page);
		var slots = GetSlotsForPage(page);
		application = slots[column];
		if (application is null || !application.IsActive)
		{
			error = "No active audio application is assigned to this mixer column.";
			return false;
		}

		return TryAdjustApplicationVolume(application.Key, direction, out application, out newVolumePercent, out error);
	}

	public bool TryAdjustApplicationVolume(
		string applicationKey,
		int direction,
		out AudioApplication? application,
		out int newVolumePercent,
		out string? error)
	{
		application = null;
		newVolumePercent = 0;
		error = null;
		if (direction is not (-1 or 1))
		{
			error = "The volume direction is invalid.";
			return false;
		}

		lock (_gate)
		{
			application = _applications.FirstOrDefault(candidate =>
				string.Equals(candidate.Key, applicationKey, StringComparison.OrdinalIgnoreCase));
		}

		if (application is null || !application.IsActive)
		{
			error = "The selected application has no active audio session.";
			return false;
		}

		return _audio.TryAdjustVolume(
			application.Key,
			direction * VolumeStep,
			out newVolumePercent,
			out error);
	}

	public async ValueTask DisposeAsync()
	{
		Timer? timer;
		lock (_gate)
		{
			timer = _timer;
			_timer = null;
			_started = false;
		}

		if (timer is not null)
		{
			await timer.DisposeAsync().ConfigureAwait(false);
		}
	}

	private int GetPageCountLocked() => Math.Max(1, (_applications.Length + SlotCount - 1) / SlotCount);
}
