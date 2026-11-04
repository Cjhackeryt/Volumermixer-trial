using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using MacroDeck.Plugin.Protocol.Handshake;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Profiles;
using MacroDeck.Sdk.Variables;
using Serilog;
using VolumeMixer.Actions;
using VolumeMixer.Mixer;
using VolumeMixer.Profiles;
using VolumeMixer.Variables;

namespace VolumeMixer;

public sealed class PluginIntegration : IPluginIntegration, IProfileProvider, IVariableProvider
{
	private readonly VolumeMixerService _mixer;
	private readonly VolumeMixerVariableProvider _variables;
	private readonly IPluginCatalogNotifier _catalogs;
	private readonly ILogger _logger;
	private bool _attached;

	public PluginIntegration(
		VolumeMixerService mixer,
		VolumeMixerVariableProvider variables,
		IPluginCatalogNotifier catalogs,
		ILogger logger)
	{
		_mixer = mixer;
		_variables = variables;
		_catalogs = catalogs;
		_logger = logger.ForContext<PluginIntegration>();
		_logger.Information("[VolumeMixer] Registering VolumeUp action");
		_logger.Information("[VolumeMixer] Registering Application virtual widget");
		_logger.Information("[VolumeMixer] Registering VolumeDown action");
		Actions =
		[
			new VolumeUpAction(mixer, logger),
			new VolumeDownAction(mixer, logger),
		];
		_logger.Information("[VolumeMixer] Actions registered successfully");
	}

	public IReadOnlyList<IActionDefinition> Actions { get; }

	public string ProviderName => "Volume Mixer";

	public IReadOnlyList<VirtualProfileDescriptor> GetProfiles() => VolumeMixerProfile.Build(_mixer, _logger);

	public IReadOnlyList<VariableDefinition> Variables => _variables.Variables;

	public IReadOnlyList<VariableDefinition> DeclaredVariables => _variables.DeclaredVariables;

	public bool VariablesDependOnConfiguration => VolumeMixerVariableProvider.VariablesDependOnConfiguration;

	public bool SupportsCatalog => VolumeMixerVariableProvider.SupportsCatalog;

	public bool SupportsPush => VolumeMixerVariableProvider.SupportsPush;

	public bool SupportsSearch => VolumeMixerVariableProvider.SupportsSearch;

	public string CatalogName => VolumeMixerVariableProvider.CatalogName;

	public int? CatalogEntryCount => _variables.CatalogEntryCount;

	public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default) =>
		_variables.ReadAsync(localId, cancellationToken);

	public ValueTask<VariableWriteResult> SetValueAsync(
		string localId,
		object? value,
		CancellationToken cancellationToken = default) =>
		VolumeMixerVariableProvider.SetValueAsync(localId, value, cancellationToken);

	public ValueTask<VariableDefinition?> ResolveAsync(string localId, CancellationToken cancellationToken) =>
		_variables.ResolveAsync(localId, cancellationToken);

	public ValueTask<VariableCatalogPage> DiscoverAsync(VariableCatalogQuery query, CancellationToken cancellationToken) =>
		_variables.DiscoverAsync(query, cancellationToken);

	public ValueTask<IReadOnlyList<VariableValue>> SubscribeAsync(
		IReadOnlyCollection<string> localIds,
		CancellationToken cancellationToken) =>
		VolumeMixerVariableProvider.SubscribeAsync(localIds, cancellationToken);

	public Task OnAttachedAsync(IVariableSink sink, CancellationToken cancellationToken) =>
		VolumeMixerVariableProvider.OnAttachedAsync(sink, cancellationToken);

	public Task InitializeAsync(IIntegrationContext context)
	{
		_logger.Information("[VolumeMixer] Plugin initialized");
		if (!_attached)
		{
			_mixer.Changed += OnMixerChanged;
			_attached = true;
		}

		_mixer.Start();
		_catalogs.CatalogChanged(CapabilityKinds.VirtualProfiles, reason: "mixer-initialized");
		_logger.Information("[VolumeMixer] Virtual deck created: 6x3");
		return Task.CompletedTask;
	}

	public async Task ShutdownAsync()
	{
		if (_attached)
		{
			_mixer.Changed -= OnMixerChanged;
			_attached = false;
		}

		await _mixer.DisposeAsync().ConfigureAwait(false);
		_logger.Information("[VolumeMixer] Plugin shut down");
	}

	public Task HandleWidgetInteractionAsync(
		string profileId,
		string folderId,
		string widgetId,
		WidgetInteraction interaction)
	{
		if (!VolumeMixerProfile.TryParseWidget(folderId, widgetId, out var page, out var column, out var direction))
		{
			_logger.Warning("[VolumeMixer] Ignored unknown virtual widget {Folder}/{Widget}", folderId, widgetId);
			return Task.CompletedTask;
		}

		if (!string.Equals(interaction.TriggerType, "press", StringComparison.OrdinalIgnoreCase) &&
			!string.Equals(interaction.TriggerType, "onShortPress", StringComparison.OrdinalIgnoreCase))
		{
			_logger.Debug("[VolumeMixer] Ignored widget trigger {Trigger} on {Folder}/{Widget}",
				interaction.TriggerType, folderId, widgetId);
			return Task.CompletedTask;
		}

		if (direction == 0)
		{
			_mixer.SelectPage(page);
			_logger.Debug("[VolumeMixer] Application tile pressed at column {Column} on page {Page}", column, page + 1);
			return Task.CompletedTask;
		}

		try
		{
			if (!_mixer.TryAdjustSlotVolume(page, column, direction, out var application, out var volume, out var error) ||
				application is null)
			{
				if (application is null)
				{
					_logger.Debug("[VolumeMixer] Ignored volume press for an empty or inactive column {Column} on page {Page}: {Reason}",
						column, page + 1, error);
				}
				else
				{
					_logger.Warning("[VolumeMixer] Volume change failed for column {Column} on page {Page}: {Reason}",
						column, page + 1, error);
				}

				return Task.CompletedTask;
			}

			_logger.Information("[VolumeMixer] Volume changed for {Application} to {Volume}%",
				application.DisplayName, volume);
			_mixer.RefreshNow();
			return Task.CompletedTask;
		}
		catch (Exception exception)
		{
			_logger.Error(exception, "[VolumeMixer] Widget press failed for {Folder}/{Widget}", folderId, widgetId);
			throw;
		}
	}

	private void OnMixerChanged()
	{
		_logger.Debug("[VolumeMixer] Audio application catalogue changed");
		_catalogs.CatalogChanged(CapabilityKinds.VirtualProfiles, reason: "audio-applications-changed");
		_catalogs.CatalogChanged(CapabilityKinds.Variables, reason: "audio-applications-changed");
	}
}
