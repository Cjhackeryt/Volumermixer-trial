using System.Globalization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Variables;
using VolumeMixer.Mixer;

namespace VolumeMixer.Variables;

public sealed class VolumeMixerVariableProvider
{
	private readonly VolumeMixerService _mixer;

	public VolumeMixerVariableProvider(VolumeMixerService mixer)
	{
		_mixer = mixer;
		Variables = BuildDefinitions();
	}

	public IReadOnlyList<VariableDefinition> Variables { get; }

	public IReadOnlyList<VariableDefinition> DeclaredVariables => Variables;

	public static bool VariablesDependOnConfiguration => false;

	public static bool SupportsCatalog => false;

	public static bool SupportsPush => false;

	public static bool SupportsSearch => false;

	public static string CatalogName => "VolumeMixer";

	public int? CatalogEntryCount => Variables.Count;

	public ValueTask<VariableReading> ReadAsync(string localId, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!TryParseLocalId(localId, out var kind, out var slot))
		{
			return ValueTask.FromResult(VariableReading.Unavailable);
		}

		if (kind == "page")
		{
			return ValueTask.FromResult(VariableReading.Of(_mixer.GetPage() + 1));
		}

		if (kind == "page-count")
		{
			return ValueTask.FromResult(VariableReading.Of(_mixer.GetPageCount()));
		}

		if (kind == "total")
		{
			return ValueTask.FromResult(VariableReading.Of(_mixer.GetTotalCount()));
		}

		var application = _mixer.GetSlotsForPage(_mixer.GetPage())[slot];
		if (application is null)
		{
			return kind switch
			{
				"app" => ValueTask.FromResult(VariableReading.Of("Empty")),
				"volume" => ValueTask.FromResult(VariableReading.Of(0.0, 0, 100, 1)),
				"muted" => ValueTask.FromResult(VariableReading.Of(false)),
				"active" => ValueTask.FromResult(VariableReading.Of(false)),
				_ => ValueTask.FromResult(VariableReading.Unavailable),
			};
		}

		return kind switch
		{
			"app" => ValueTask.FromResult(VariableReading.Of(
				application.DisplayName + " " + application.VolumePercent.ToString(CultureInfo.InvariantCulture) + "%")),
			"volume" => ValueTask.FromResult(VariableReading.Of((double)application.VolumePercent, 0, 100, 1)),
			"muted" => ValueTask.FromResult(VariableReading.Of(application.IsMuted)),
			"active" => ValueTask.FromResult(VariableReading.Of(application.IsActive)),
			_ => ValueTask.FromResult(VariableReading.Unavailable),
		};
	}

	public static ValueTask<VariableWriteResult> SetValueAsync(string localId, object? value, CancellationToken cancellationToken) =>
		ValueTask.FromResult(VariableWriteResult.NotWritable(Strings.Variables.ReadOnly.Message()));

	public ValueTask<VariableDefinition?> ResolveAsync(string localId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult(Variables.FirstOrDefault(definition =>
			string.Equals(definition.Id, localId, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(definition.ResolvedId, localId, StringComparison.OrdinalIgnoreCase) ||
			string.Equals(definition.Name, localId, StringComparison.OrdinalIgnoreCase)));
	}

	public ValueTask<VariableCatalogPage> DiscoverAsync(VariableCatalogQuery query, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return ValueTask.FromResult(new VariableCatalogPage { Items = Variables, ContinuationToken = null });
	}

	public static ValueTask<IReadOnlyList<VariableValue>> SubscribeAsync(
		IReadOnlyCollection<string> localIds,
		CancellationToken cancellationToken) =>
		ValueTask.FromResult<IReadOnlyList<VariableValue>>([]);

	public static Task OnAttachedAsync(IVariableSink sink, CancellationToken cancellationToken) => Task.CompletedTask;

	private static bool TryParseLocalId(string localId, out string kind, out int slot)
	{
		kind = string.Empty;
		slot = -1;
		if (string.Equals(localId, "page", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(localId, "page-count", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(localId, "total", StringComparison.OrdinalIgnoreCase))
		{
			kind = localId.ToLowerInvariant();
			return true;
		}

		if (!localId.StartsWith("slot-", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var separator = localId.IndexOf('-', 5);
		if (separator < 0 ||
			!int.TryParse(localId[5..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var oneBased) ||
			oneBased is < 1 or > VolumeMixerService.SlotCount)
		{
			return false;
		}

		slot = oneBased - 1;
		kind = localId[(separator + 1)..].ToLowerInvariant();
		return kind is "app" or "volume" or "muted" or "active";
	}

	private static List<VariableDefinition> BuildDefinitions()
	{
		var definitions = new List<VariableDefinition>();
		for (var index = 1; index <= VolumeMixerService.SlotCount; index++)
		{
			var slot = index.ToString(CultureInfo.InvariantCulture);
			definitions.Add(VariableDefinition.Eager($"mixer_slot{slot}_app", VariableType.Text) with
			{
				Id = $"slot-{slot}-app",
				DisplayName = Strings.Variables.SlotApp.Display(slot),
				Description = Strings.Variables.SlotApp.Description(),
			});
			definitions.Add(VariableDefinition.Eager($"mixer_slot{slot}_volume", VariableType.Numeric, 0, TimeSpan.FromSeconds(2)) with
			{
				Id = $"slot-{slot}-volume",
				DisplayName = Strings.Variables.SlotVolume.Display(slot),
				Description = Strings.Variables.SlotVolume.Description(),
				Unit = "%",
			});
			definitions.Add(VariableDefinition.Eager($"mixer_slot{slot}_muted", VariableType.Boolean) with
			{
				Id = $"slot-{slot}-muted",
				DisplayName = Strings.Variables.SlotMuted.Display(slot),
				Description = Strings.Variables.SlotMuted.Description(),
			});
			definitions.Add(VariableDefinition.Eager($"mixer_slot{slot}_active", VariableType.Boolean) with
			{
				Id = $"slot-{slot}-active",
				DisplayName = Strings.Variables.SlotActive.Display(slot),
				Description = Strings.Variables.SlotActive.Description(),
			});
		}

		definitions.Add(VariableDefinition.Eager("mixer_page", VariableType.Numeric) with
		{
			Id = "page",
			DisplayName = Strings.Variables.Page.Display(),
			Description = Strings.Variables.Page.Description(),
		});
		definitions.Add(VariableDefinition.Eager("mixer_page_count", VariableType.Numeric) with
		{
			Id = "page-count",
			DisplayName = Strings.Variables.PageCount.Display(),
			Description = Strings.Variables.PageCount.Description(),
		});
		definitions.Add(VariableDefinition.Eager("mixer_total_apps", VariableType.Numeric) with
		{
			Id = "total",
			DisplayName = Strings.Variables.Total.Display(),
			Description = Strings.Variables.Total.Description(),
		});
		return definitions;
	}
}
