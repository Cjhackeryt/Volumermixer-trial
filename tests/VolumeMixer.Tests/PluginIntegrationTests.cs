using System.Collections.Concurrent;
using System.Text.Json;
using MacroDeck.Plugin.Hosting.Integrations.HostApis;
using MacroDeck.Plugin.Protocol.Capabilities.VirtualProfiles;
using MacroDeck.Plugin.Testing;
using MacroDeck.Plugin.Testing.Fakes;
using MacroDeck.Plugin.Protocol.Handshake;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Profiles;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VolumeMixer.Audio;
using VolumeMixer.Mixer;
using VolumeMixer.Variables;

namespace VolumeMixer.Tests;

internal sealed class FakeAudioSessionController : IAudioSessionController
{
	private readonly Dictionary<string, AudioApplication> _applications = new(StringComparer.OrdinalIgnoreCase);

	public void SetApplications(params AudioApplication[] applications)
	{
		_applications.Clear();
		foreach (var application in applications)
		{
			_applications.Add(application.Key, application);
		}
	}

	public IReadOnlyList<AudioApplication> GetApplications() => _applications.Values.ToArray();

	public bool TryAdjustVolume(string applicationKey, int deltaPercent, out int newVolumePercent, out string? errorMessage)
	{
		if (!_applications.TryGetValue(applicationKey, out var application))
		{
			newVolumePercent = 0;
			errorMessage = "The selected application's audio session is no longer active.";
			return false;
		}

		newVolumePercent = Math.Clamp(application.VolumePercent + deltaPercent, 0, 100);
		_applications[applicationKey] = application with { VolumePercent = newVolumePercent };
		errorMessage = null;
		return true;
	}
}

internal sealed class FakeCatalogNotifier : IPluginCatalogNotifier
{
	public List<string> Kinds { get; } = [];

	public List<string?> Reasons { get; } = [];

	public void CatalogChanged(string kind, string? localId = null, string? reason = null)
	{
		Kinds.Add(kind);
		Reasons.Add(reason);
	}
}

internal sealed class TestLogSink : ILogEventSink
{
	private readonly ConcurrentQueue<LogEvent> _events = new();

	public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

	public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}

internal sealed class MixerTestContext : IAsyncDisposable
{
	public MixerTestContext(params AudioApplication[] applications)
	{
		Audio = new FakeAudioSessionController();
		Audio.SetApplications(applications);
		LogSink = new TestLogSink();
		Logger = new LoggerConfiguration()
			.MinimumLevel.Verbose()
			.WriteTo.Sink(LogSink)
			.CreateLogger();
		Mixer = new VolumeMixerService(Audio, Logger);
		Variables = new VolumeMixerVariableProvider(Mixer);
		Catalogs = new FakeCatalogNotifier();
		Integration = new PluginIntegration(Mixer, Variables, Catalogs, Logger);
	}

	public FakeAudioSessionController Audio { get; }

	public ILogger Logger { get; }

	public TestLogSink LogSink { get; }

	public VolumeMixerService Mixer { get; }

	public VolumeMixerVariableProvider Variables { get; }

	public FakeCatalogNotifier Catalogs { get; }

	public PluginIntegration Integration { get; }

	public async ValueTask DisposeAsync()
	{
		await Integration.ShutdownAsync();
		(Logger as IDisposable)?.Dispose();
	}
}

[TestFixture]
public sealed class PluginIntegrationTests
{
	private static readonly string[] RegisteredActionIds = ["volume-up", "volume-down"];
	private static readonly string[] ExpectedApplicationKeys = ["app-0", "app-1"];

	[Test]
	public async Task Startup_invalidates_an_empty_cached_profile_after_initial_audio_discovery()
	{
		await using var context = new MixerTestContext(CreateApplication(0, 40));

		var beforeInitialization = context.Integration.GetProfiles()[0].Folders[0].Widgets;
		Assert.That(GetLabel(beforeInitialization.Single(widget => widget.Id == "app-0").Data), Is.EqualTo("Empty"));

		await context.Integration.InitializeAsync(new FakeIntegrationContext());

		var afterInitialization = context.Integration.GetProfiles()[0].Folders[0].Widgets;
		var appData = afterInitialization.Single(widget => widget.Id == "app-0").Data;
		Assert.That(GetLabel(appData), Is.EqualTo("App0 40%"));
		Assert.That(context.Catalogs.Kinds, Does.Contain(CapabilityKinds.VirtualProfiles));
		Assert.That(context.Catalogs.Reasons, Does.Contain("mixer-initialized"));
	}

	[Test]
	public async Task Profile_is_a_6_by_3_grid_with_18_renderable_widgets()
	{
		await using var context = new MixerTestContext(CreateApplication(0, 40), CreateApplication(1, 70));
		await context.Integration.InitializeAsync(new FakeIntegrationContext());

		var profile = context.Integration.GetProfiles().Single();
		Assert.That(profile.Layout.Rows, Is.EqualTo(3));
		Assert.That(profile.Layout.Columns, Is.EqualTo(6));
		var widgets = profile.Folders.Single().Widgets;
		Assert.That(widgets, Has.Count.EqualTo(18));

		for (var column = 0; column < VolumeMixerService.SlotCount; column++)
		{
			Assert.Multiple(() =>
			{
				var upData = widgets.Single(widget => widget.Id == $"up-{column}").Data;
				var appData = widgets.Single(widget => widget.Id == $"app-{column}").Data;
				var downData = widgets.Single(widget => widget.Id == $"down-{column}").Data;
				Assert.That(GetLabel(upData), Is.EqualTo("+"));
				Assert.That(GetOffStateLabel(upData), Is.EqualTo("+"));
				Assert.That(widgets.Single(widget => widget.Id == $"app-{column}").PositionY, Is.EqualTo(1));
				Assert.That(GetLabel(downData), Is.EqualTo("-"));
				Assert.That(GetOffStateLabel(downData), Is.EqualTo("-"));
				Assert.That(GetOffStateLabel(appData), Is.EqualTo(GetLabel(appData)));
			});
		}

		Assert.That(GetLabel(widgets.Single(widget => widget.Id == "app-0").Data), Is.EqualTo("App0 40%"));
		Assert.That(GetLabel(widgets.Single(widget => widget.Id == "app-1").Data), Is.EqualTo("App1 70%"));
		Assert.That(GetImageUrl(widgets.Single(widget => widget.Id == "app-0").Data),
			Is.EqualTo("data:image/png;base64,dGVzdA=="));
		Assert.That(GetLabel(widgets.Single(widget => widget.Id == "app-5").Data), Is.EqualTo("Empty"));
		Assert.That(widgets.All(widget => !string.IsNullOrWhiteSpace(widget.Data)), Is.True);
	}

	[TestCase(0, 1)]
	[TestCase(1, 1)]
	[TestCase(3, 1)]
	[TestCase(6, 1)]
	[TestCase(7, 2)]
	public async Task Application_count_creates_the_expected_number_of_pages(int count, int expectedPages)
	{
		await using var context = new MixerTestContext(
			Enumerable.Range(0, count).Select(index => CreateApplication(index, 50)).ToArray());
		await context.Integration.InitializeAsync(new FakeIntegrationContext());

		var profile = context.Integration.GetProfiles().Single();
		Assert.That(profile.Folders, Has.Count.EqualTo(expectedPages));
		Assert.That(profile.Folders.All(folder => folder.Widgets.Count == 18), Is.True);
		Assert.That(profile.Folders.Sum(folder => folder.Widgets.Count), Is.EqualTo(expectedPages * 18));
	}

	[Test]
	public async Task Up_and_down_only_adjust_the_application_assigned_to_the_pressed_column()
	{
		await using var context = new MixerTestContext(CreateApplication(0, 50), CreateApplication(1, 70));
		await context.Integration.InitializeAsync(new FakeIntegrationContext());

		await context.Integration.HandleWidgetInteractionAsync(
			string.Empty,
			"mixer",
			"up-1",
			new WidgetInteraction("press"));
		Assert.That(context.Audio.GetApplications().Single(application => application.Key == "app-1").VolumePercent, Is.EqualTo(75));
		Assert.That(context.Audio.GetApplications().Single(application => application.Key == "app-0").VolumePercent, Is.EqualTo(50));

		await context.Integration.HandleWidgetInteractionAsync(
			string.Empty,
			"mixer",
			"down-1",
			new WidgetInteraction("press"));
		Assert.That(context.Audio.GetApplications().Single(application => application.Key == "app-1").VolumePercent, Is.EqualTo(70));
	}

	[Test]
	public async Task Volume_changes_are_clamped_and_empty_slots_are_inert()
	{
		await using var context = new MixerTestContext(CreateApplication(0, 2));
		await context.Integration.InitializeAsync(new FakeIntegrationContext());

		await context.Integration.HandleWidgetInteractionAsync(
			string.Empty,
			"mixer",
			"down-0",
			new WidgetInteraction("press"));
		await context.Integration.HandleWidgetInteractionAsync(
			string.Empty,
			"mixer",
			"up-5",
			new WidgetInteraction("press"));

		Assert.That(context.Audio.GetApplications().Single().VolumePercent, Is.EqualTo(0));
	}

	[Test]
	public async Task Empty_slot_volume_press_is_debug_not_warning()
	{
		await using var context = new MixerTestContext();
		await context.Integration.InitializeAsync(new FakeIntegrationContext());

		await context.Integration.HandleWidgetInteractionAsync(
			string.Empty,
			"mixer",
			"up-0",
			new WidgetInteraction("press"));

		Assert.That(context.LogSink.Events.Any(logEvent =>
			logEvent.Level == LogEventLevel.Debug &&
			logEvent.MessageTemplate.Text.StartsWith(
				"[VolumeMixer] Ignored volume press for an empty or inactive column",
				StringComparison.Ordinal)), Is.True);
		Assert.That(context.LogSink.Events.Any(logEvent =>
			logEvent.Level == LogEventLevel.Warning &&
			logEvent.MessageTemplate.Text.StartsWith(
				"[VolumeMixer] Volume change failed",
				StringComparison.Ordinal)), Is.False);
	}

	[Test]
	public async Task Application_tile_and_non_press_triggers_do_not_change_volume()
	{
		await using var context = new MixerTestContext(CreateApplication(0, 40));
		await context.Integration.InitializeAsync(new FakeIntegrationContext());

		await context.Integration.HandleWidgetInteractionAsync(
			string.Empty,
			"mixer",
			"app-0",
			new WidgetInteraction("press"));
		await context.Integration.HandleWidgetInteractionAsync(
			string.Empty,
			"mixer",
			"up-0",
			new WidgetInteraction("longPress"));

		Assert.That(context.Audio.GetApplications().Single().VolumePercent, Is.EqualTo(40));
	}

	[Test]
	public async Task More_than_six_applications_are_reachable_on_a_second_folder()
	{
		var applications = Enumerable.Range(0, 7).Select(index => CreateApplication(index, 50)).ToArray();
		await using var context = new MixerTestContext(applications);
		await context.Integration.InitializeAsync(new FakeIntegrationContext());

		await context.Integration.HandleWidgetInteractionAsync(
			string.Empty,
			"mixer-page-2",
			"app-0",
			new WidgetInteraction("press"));
		Assert.That(context.Mixer.GetPage(), Is.EqualTo(1));
		Assert.That((await context.Integration.ReadAsync("page")).Value, Is.EqualTo(2));

		await context.Integration.HandleWidgetInteractionAsync(
			string.Empty,
			"mixer-page-2",
			"up-0",
			new WidgetInteraction("press"));

		Assert.That(context.Audio.GetApplications().Single(application => application.Key == "app-6").VolumePercent, Is.EqualTo(55));
		Assert.That((await context.Integration.ReadAsync("slot-1-app")).Value, Does.Contain("App6"));
		Assert.That((await context.Integration.ReadAsync("page-count")).Value, Is.EqualTo(2));
		Assert.That(context.Audio.GetApplications().Where(application => application.Key != "app-6")
			.All(application => application.VolumePercent == 50), Is.True);
	}

	[Test]
	public async Task Newly_detected_applications_invalidate_the_profile_and_variable_catalogues()
	{
		await using var context = new MixerTestContext(CreateApplication(0, 40));
		await context.Integration.InitializeAsync(new FakeIntegrationContext());
		var notificationCount = context.Catalogs.Kinds.Count;

		context.Audio.SetApplications(CreateApplication(0, 40), CreateApplication(1, 60));
		context.Mixer.RefreshNow();

		Assert.That(context.Catalogs.Kinds.Count, Is.GreaterThan(notificationCount));
		Assert.That(context.Catalogs.Kinds, Does.Contain(CapabilityKinds.VirtualProfiles));
		Assert.That(context.Catalogs.Kinds, Does.Contain(CapabilityKinds.Variables));
		var appData = context.Integration.GetProfiles()[0].Folders[0].Widgets.Single(widget => widget.Id == "app-1").Data;
		Assert.That(GetLabel(appData), Is.EqualTo("App1 60%"));
	}

	[Test]
	public async Task The_plugin_registers_manual_volume_actions_and_keeps_slot_variables()
	{
		await using var context = new MixerTestContext(CreateApplication(0, 42));
		await context.Integration.InitializeAsync(new FakeIntegrationContext());

		Assert.That(context.Integration.Actions.Select(action => action.Id),
			Is.EquivalentTo(RegisteredActionIds));
		Assert.That(context.Integration.DeclaredVariables.Select(variable => variable.Id),
			Does.Contain("slot-1-app").And.Contain("slot-1-volume").And.Contain("slot-1-muted").And
				.Contain("slot-1-active").And.Contain("page").And.Contain("page-count").And.Contain("total"));
		Assert.That((await context.Integration.ReadAsync("slot-1-app")).Equals(MacroDeck.Sdk.Variables.VariableReading.Unavailable),
			Is.False);
	}

	[Test]
	public async Task Manual_action_options_list_detected_active_applications()
	{
		await using var context = new MixerTestContext(CreateApplication(0, 42), CreateApplication(1, 70));
		await context.Integration.InitializeAsync(new FakeIntegrationContext());
		var action = (IDynamicOptionsActionDefinition)context.Integration.Actions
			.Single(definition => definition.Id == "volume-up");

		var result = await action.GetDynamicOptionsAsync(
			new DynamicOptionsContext
			{
				ParameterName = "application",
				CurrentParameters = new Dictionary<string, object?>(),
			},
			CancellationToken.None);

		Assert.That(result.Options.Select(option => option.Value), Is.EquivalentTo(ExpectedApplicationKeys));
		Assert.That(GetLocalizedKey(result.Options.Single(option => option.Value == "app-0").Label),
			Is.EqualTo("Actions.Application.Option"));
	}

	[Test]
	public async Task Manual_action_options_explain_how_to_populate_an_empty_application_list()
	{
		await using var context = new MixerTestContext();
		await context.Integration.InitializeAsync(new FakeIntegrationContext());
		var action = (IDynamicOptionsActionDefinition)context.Integration.Actions
			.Single(definition => definition.Id == "volume-up");

		var result = await action.GetDynamicOptionsAsync(
			new DynamicOptionsContext
			{
				ParameterName = "application",
				CurrentParameters = new Dictionary<string, object?>(),
			},
			CancellationToken.None);

		Assert.That(result.Options, Is.Empty);
		Assert.That(GetLocalizedKey(result.Error), Is.EqualTo("Actions.Application.NoActiveApplications"));
	}

	[Test]
	public async Task Virtual_profiles_are_registered_through_the_beta_15_capability_handler()
	{
		var audio = new FakeAudioSessionController();
		audio.SetApplications(CreateApplication(0, 62));
		var catalogs = new FakeCatalogNotifier();
		await using var harness = PluginTestHarness.Create(builder =>
		{
			builder.Services.AddSingleton<IAudioSessionController>(audio);
			builder.Services.AddSingleton<VolumeMixerService>();
			builder.Services.AddSingleton<VolumeMixerVariableProvider>();
			builder.Services.AddSingleton<IPluginCatalogNotifier>(catalogs);
			builder.UseLocalization(Strings.LocalizationCatalog);
			builder.RegisterIntegration<PluginIntegration>();
		});
		await harness.InitializeIntegrationsAsync();

		var outcome = await harness.VirtualProfiles.GetProfilesAsync();

		Assert.That(outcome.Succeeded, Is.True);
		var profiles = outcome.DataAs<VirtualProfilesResult>();
		Assert.That(profiles, Is.Not.Null);
		Assert.That(profiles!.Profiles.Single().Folders.Single().Widgets, Has.Count.EqualTo(18));
		var appData = profiles.Profiles.Single().Folders.Single().Widgets.Single(widget => widget.Id == "app-0").Data;
		Assert.That(GetLabel(appData), Is.EqualTo("App0 62%"));
		Assert.That(GetImageUrl(appData), Is.EqualTo("data:image/png;base64,dGVzdA=="));

		var interaction = await harness.VirtualProfiles.SendWidgetInteractionAsync(new WidgetInteractionArguments
		{
			ProfileId = string.Empty,
			FolderId = "mixer",
			WidgetId = "up-0",
			TriggerType = "press",
		});
		Assert.That(interaction.Succeeded, Is.True);
		Assert.That(audio.GetApplications().Single().VolumePercent, Is.EqualTo(67));

		var action = await harness.Actions.ExecuteAsync(
			"volume-down",
			new Dictionary<string, object?> { ["application"] = "app-0" });
		Assert.That(action.Succeeded, Is.True);
		Assert.That(audio.GetApplications().Single().VolumePercent, Is.EqualTo(62));

		var legacyAction = await harness.Actions.ExecuteAsync(
			"volume-down",
			new Dictionary<string, object?> { ["slot"] = "1" });
		Assert.That(legacyAction.Succeeded, Is.True);
		Assert.That(audio.GetApplications().Single().VolumePercent, Is.EqualTo(57));

		audio.SetApplications();
		var staleAction = await harness.Actions.ExecuteAsync(
			"volume-down",
			new Dictionary<string, object?> { ["application"] = "app-0" });
		Assert.That(staleAction.Succeeded, Is.False);
		Assert.That(audio.GetApplications(), Is.Empty);
	}

	[Test]
	public void The_catalog_is_scoped_to_the_plugin_id()
	{
		Assert.That(Strings.LocalizationCatalog.Scope, Is.EqualTo("plugin:com.cjhackeryt.volumemixer"));
	}

	private static AudioApplication CreateApplication(int index, int volume) =>
		new(
			$"app-{index}",
			1000 + index,
			$"app{index}",
			$"App{index}",
			$@"C:\Fake\app{index}.exe",
			"data:image/png;base64,dGVzdA==",
			volume,
			IsMuted: false,
			SessionCount: 1,
			IsActive: true);

	private static string GetLabel(string? data)
	{
		Assert.That(data, Is.Not.Null.And.Not.Empty);
		using var document = JsonDocument.Parse(data!);
		var label = document.RootElement.GetProperty("label");
		Assert.That(label.ValueKind, Is.EqualTo(JsonValueKind.String));
		return label.GetString() ?? string.Empty;
	}

	private static string GetOffStateLabel(string? data)
	{
		Assert.That(data, Is.Not.Null.And.Not.Empty);
		using var document = JsonDocument.Parse(data!);
		var label = document.RootElement.GetProperty("offState").GetProperty("label");
		Assert.That(label.ValueKind, Is.EqualTo(JsonValueKind.String));
		return label.GetString() ?? string.Empty;
	}

	private static string? GetImageUrl(string? data)
	{
		Assert.That(data, Is.Not.Null.And.Not.Empty);
		using var document = JsonDocument.Parse(data!);
		return document.RootElement.GetProperty("imageUrl").GetString();
	}

	private static string GetLocalizedKey(object localizedText)
	{
		using var document = JsonDocument.Parse(JsonSerializer.Serialize(localizedText));
		return document.RootElement.GetProperty("$localized").GetProperty("key").GetString() ?? string.Empty;
	}
}
