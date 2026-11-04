using System.Text.Json;
using MacroDeck.Sdk.Profiles;
using VolumeMixer.Mixer;
using Serilog;

namespace VolumeMixer.Profiles;

internal static class VolumeMixerProfile
{
	public const string ProfileId = "volume-mixer";

	private const string FirstFolderId = "mixer";
	private const string FolderPrefix = "mixer-page-";
	private const string ActiveColor = "#FFFFFF";
	private const string EmptyColor = "#A0AEC0";
	private const string ApplicationBackground = "#20242B";
	private const string UpBackground = "#2F855A";
	private const string DownBackground = "#C53030";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static IReadOnlyList<VirtualProfileDescriptor> Build(VolumeMixerService mixer, ILogger logger)
	{
		var pageCount = mixer.GetPageCount();
		var folders = new List<VirtualFolderDescriptor>(pageCount);

		for (var page = 0; page < pageCount; page++)
		{
			var widgets = new List<VirtualWidgetDescriptor>(VolumeMixerService.SlotCount * 3);
			var slots = mixer.GetSlotsForPage(page);
			for (var column = 0; column < VolumeMixerService.SlotCount; column++)
			{
				var application = slots[column];
				AddWidget(widgets, logger, column, 0, "VolumeUp",
					$"up-{column}", UpBackground, "+", application is not null, null);
				AddWidget(widgets, logger, column, 1, "Application",
					$"app-{column}", ApplicationBackground, ApplicationLabel(application), application is not null,
					application?.IconDataUri);
				AddWidget(widgets, logger, column, 2, "VolumeDown",
					$"down-{column}", DownBackground, "-", application is not null, null);
			}

			var folderId = page == 0 ? FirstFolderId : FolderPrefix + (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
			var folderName = page == 0 ? "Mixer" : "Mixer " + (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
			folders.Add(new VirtualFolderDescriptor(folderId, folderName, widgets, Order: page));
		}

		return
		[
			new VirtualProfileDescriptor(
				ProfileId,
				"Volume Mixer",
				ProfileLayout.Grid(rows: 3, columns: VolumeMixerService.SlotCount),
				folders),
		];
	}

	public static bool TryParseWidget(string folderId, string widgetId, out int page, out int column, out int direction)
	{
		page = 0;
		column = -1;
		direction = 0;

		if (!string.Equals(folderId, FirstFolderId, StringComparison.Ordinal))
		{
			if (!folderId.StartsWith(FolderPrefix, StringComparison.Ordinal) ||
				!int.TryParse(folderId[FolderPrefix.Length..], out var oneBasedPage) ||
				oneBasedPage < 2)
			{
				return false;
			}

			page = oneBasedPage - 1;
		}

		var separator = widgetId.IndexOf('-');
		if (separator < 0 ||
			!int.TryParse(widgetId[(separator + 1)..], out column) ||
			column is < 0 or >= VolumeMixerService.SlotCount)
		{
			return false;
		}

		if (widgetId.StartsWith("up-", StringComparison.Ordinal))
		{
			direction = 1;
			return true;
		}

		if (widgetId.StartsWith("down-", StringComparison.Ordinal))
		{
			direction = -1;
			return true;
		}

		if (widgetId.StartsWith("app-", StringComparison.Ordinal))
		{
			return true;
		}

		return false;
	}

	private static void AddWidget(
		List<VirtualWidgetDescriptor> widgets,
		ILogger logger,
		int column,
		int row,
		string kind,
		string id,
		string backgroundColor,
		string label,
		bool active,
		string? imageUrl)
	{
		try
		{
			var widgetData = JsonSerializer.Serialize(new
			{
				mode = "single",
				label,
				imageUrl,
				backgroundColor,
				labelColor = active ? ActiveColor : EmptyColor,
				labelPosition = row == 1 ? "bottom" : "center",
				offState = new
				{
					label,
					backgroundColor,
					labelColor = active ? ActiveColor : EmptyColor,
					labelPosition = row == 1 ? "bottom" : "center",
				},
			}, JsonOptions);
			widgets.Add(new VirtualWidgetDescriptor(id, "ActionButton", column, row, Data: widgetData));
			logger.Debug("[VolumeMixer] Creating widget: Column={Column} Row={Row} Type={Type} Id={WidgetId} created and assigned",
				column, row, kind, id);
		}
		catch (Exception exception)
		{
			logger.Error(exception, "[VolumeMixer] Widget creation or assignment failed: Column={Column} Row={Row} Type={Type} Id={WidgetId}",
				column, row, kind, id);
			throw;
		}
	}

	private static string ApplicationLabel(Audio.AudioApplication? application)
	{
		if (application is null)
		{
			return "Empty";
		}

		var volume = application.VolumePercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
		return application.IsMuted
			? $"{application.DisplayName} ({volume}%, muted)"
			: $"{application.DisplayName} {volume}%";
	}
}
