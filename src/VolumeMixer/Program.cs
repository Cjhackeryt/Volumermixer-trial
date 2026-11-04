using MacroDeck.Plugin.Hosting;
using MacroDeck.Plugin.Serilog;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VolumeMixer.Audio;
using VolumeMixer.Mixer;
using VolumeMixer.Variables;

var builder = MacroDeckPlugin.CreatePlugin(args)
	.UseMacroDeckLogging()
	.UseLocalization(VolumeMixer.Strings.LocalizationCatalog);

builder.Services.AddSingleton<IAudioSessionController>(services =>
	OperatingSystem.IsWindows()
		? new WindowsAudioSessionController(services.GetRequiredService<ILogger>())
		: new UnavailableAudioSessionController());
builder.Services.AddSingleton<VolumeMixerService>();
builder.Services.AddSingleton<VolumeMixerVariableProvider>();

var plugin = builder
	.RegisterIntegration<VolumeMixer.PluginIntegration>()
	.Build();
await plugin.RunAsync();
