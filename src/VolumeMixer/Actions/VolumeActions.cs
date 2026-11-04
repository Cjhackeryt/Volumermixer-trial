using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using Serilog;
using VolumeMixer.Mixer;

namespace VolumeMixer.Actions;

public sealed class VolumeUpAction : VolumeActionDefinition
{
	public VolumeUpAction(VolumeMixerService mixer, ILogger logger)
		: base(mixer, logger, +1)
	{
	}

	public override string Id => "volume-up";

	public override LocalizedText Name => Strings.Actions.VolumeUp.Name();

	public override LocalizedText Description => Strings.Actions.VolumeUp.Description();
}

public sealed class VolumeDownAction : VolumeActionDefinition
{
	public VolumeDownAction(VolumeMixerService mixer, ILogger logger)
		: base(mixer, logger, -1)
	{
	}

	public override string Id => "volume-down";

	public override LocalizedText Name => Strings.Actions.VolumeDown.Name();

	public override LocalizedText Description => Strings.Actions.VolumeDown.Description();
}

public abstract class VolumeActionDefinition : IDynamicOptionsActionDefinition
{
	private const string ApplicationParameter = "application";
	private const string LegacySlotParameter = "slot";

	private readonly VolumeMixerService _mixer;
	private readonly ILogger _logger;
	private readonly int _direction;

	protected VolumeActionDefinition(VolumeMixerService mixer, ILogger logger, int direction)
	{
		_mixer = mixer;
		_logger = logger.ForContext(GetType());
		_direction = direction;
	}

	public abstract string Id { get; }

	public abstract LocalizedText Name { get; }

	public abstract LocalizedText Description { get; }

	public IReadOnlyList<ActionParameter> Parameters =>
	[
		ActionParameter.DynamicChoice(
			ApplicationParameter,
			Strings.Actions.Application.Label(),
			Strings.Actions.Application.Description()),
	];

	public MacroDeckPlatform Platforms => MacroDeckPlatform.Windows;

	public IActionExecutor CreateExecutor() => new Executor(_mixer, _logger, _direction);

	public Task<DynamicOptionsResult> GetDynamicOptionsAsync(
		DynamicOptionsContext context,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!string.Equals(context.ParameterName, ApplicationParameter, StringComparison.Ordinal))
		{
			return Task.FromResult(new DynamicOptionsResult { Options = [] });
		}

		var applications = _mixer.GetApplications();
		var options = applications
			.Where(application => application.IsActive)
			.Select(application => new ActionParameterOption
			{
				Value = application.Key,
				Label = application.IsMuted
					? Strings.Actions.Application.MutedOption(application.DisplayName, application.VolumePercent)
					: Strings.Actions.Application.Option(application.DisplayName, application.VolumePercent),
			})
			.ToArray();

		return Task.FromResult(new DynamicOptionsResult
		{
			Options = options,
			CacheSeconds = 2,
			Error = options.Length == 0 ? Strings.Actions.Application.NoActiveApplications() : null,
		});
	}

	private sealed class Executor : IActionExecutor
	{
		private readonly VolumeMixerService _mixer;
		private readonly ILogger _logger;
		private readonly int _direction;

		public Executor(VolumeMixerService mixer, ILogger logger, int direction)
		{
			_mixer = mixer;
			_logger = logger.ForContext<Executor>();
			_direction = direction;
		}

		public Task<ActionResult> ExecuteAsync(ActionExecutionContext context)
		{
			context.CancellationToken.ThrowIfCancellationRequested();
			var hasApplication = context.Parameters.TryGetValue(ApplicationParameter, out var value);
			var isLegacySlot = !hasApplication &&
				context.Parameters.TryGetValue(LegacySlotParameter, out value);
			if ((!hasApplication && !isLegacySlot) ||
				string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
			{
				return Task.FromResult(ActionResult.Failed(
					ActionErrorCodes.InvalidParameter,
					Strings.Actions.Errors.ApplicationRequired()));
			}

			var applicationKey = Convert.ToString(value, CultureInfo.InvariantCulture)!;
			try
			{
				if (int.TryParse(applicationKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacySlot) &&
					legacySlot is >= 1 and <= VolumeMixerService.SlotCount &&
					!_mixer.GetApplications().Any(application =>
						string.Equals(application.Key, applicationKey, StringComparison.OrdinalIgnoreCase)))
				{
					if (!_mixer.TryAdjustSlotVolume(
						_mixer.GetPage(),
						legacySlot - 1,
						_direction,
						out var legacyApplication,
						out var legacyVolume,
						out var legacyError) ||
						legacyApplication is null)
					{
						return ApplicationNotFound(legacyError);
					}

					_logger.Information("Changed {Application} to {Volume}%", legacyApplication.DisplayName, legacyVolume);
					_mixer.RefreshNow();
					return ActionResult.SucceededTask;
				}

				if (!_mixer.TryAdjustApplicationVolume(applicationKey, _direction, out var application, out var volume, out var error) ||
					application is null)
				{
					return ApplicationNotFound(error);
				}

				_logger.Information("Changed {Application} to {Volume}%", application.DisplayName, volume);
				_mixer.RefreshNow();
				return ActionResult.SucceededTask;
			}
			catch (Exception exception) when (
				exception is COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or Win32Exception or ArgumentException)
			{
				_logger.Error(exception, "Application volume control failed for {ApplicationKey}.", applicationKey);
				return Task.FromResult(ActionResult.Failed(
					ActionErrorCodes.ProviderError,
					Strings.Actions.Errors.Unexpected()));
			}
		}

		private Task<ActionResult> ApplicationNotFound(string? error)
		{
			_logger.Warning("The volume action could not find an active application: {Reason}", error);
			return Task.FromResult(ActionResult.Failed(
				ActionErrorCodes.NotFound,
				Strings.Actions.Errors.NoApplication()));
		}
	}
}
