# Volume Mixer

Volume Mixer is an out-of-process Macro Deck 3 plugin for changing the per-application volume of
Windows audio sessions. Its virtual profile is designed to show six applications at a time in a
6-column by 3-row grid. The middle row includes each application's name, current volume and, when
available, its executable icon. The tested Macro Deck host currently renders these virtual tiles as
blank placeholders, as described below.

Applications are discovered from active sessions on the default Windows multimedia render device.
Multiple sessions belonging to the same executable are displayed as one application and adjusted
together. Additional applications appear in numbered mixer folders. An empty column is inert.
The plugin does not change Windows master volume or persist audio settings itself.

The profile updates when the active session catalogue or a displayed session's volume changes. On
startup it refreshes the virtual-profile catalogue after audio discovery, so an initially empty
profile is not kept as a cached description. Variable IDs are persisted Macro Deck identifiers; do
not rename them without a migration plan. Applications appear when they have active audio sessions
on the default Windows multimedia output device.

## Requirements

- Windows x64
- .NET SDK 10
- Macro Deck 3 with the .NET 10 runtime
- `macrodeck-plugin` CLI for conformance tests and release packaging

## Build and test

From the repository root:

```powershell
dotnet build
dotnet test
macrodeck-plugin test --project src\VolumeMixer --report markdown --output conformance.md
```

Build and unit tests do not require a running Macro Deck installation. The conformance suite starts
a stub host and checks the plugin's protocol behavior.

## Run against Macro Deck

Open the solution in an IDE, select the **Macro Deck - Real Host** launch profile for
`src\VolumeMixer\VolumeMixer.csproj`, and start Macro Deck on the same machine. The plugin uses
self-registration with the host at `http://127.0.0.1:8193`. The launch profile contains no enrollment
token; if the host requests one, provide it through .NET User Secrets rather than a launch setting or
source file.

The integration and virtual profile appear after the plugin connects. Audio detection only includes
active sessions on the default multimedia render endpoint. If an application has no active audio
session, it is not listed yet.

If applications are still missing while audio is playing, check the Macro Deck log for the
`Windows audio session discovery found ... active applications` message. A count of zero means
Windows is not exposing an active session on the default multimedia render endpoint.

On the Macro Deck host build tested on 2026-10-04, the virtual profile's ActionButton tiles render
as blank placeholders, including their labels and application icons. The plugin supplies the widget
data, but the host does not create a live UI session for these virtual widgets. The manual volume
actions on ordinary Macro Deck buttons work independently. This is a host-side limitation; verify
again after an upstream host fix.

## Controls and variables

The intended **Volume Mixer** virtual profile has a `Mixer` folder and one folder per additional
six applications. In each column, the center tile identifies the application, the upper tile
increases its volume, and the lower tile decreases it. Pressing an application's center tile
selects that folder for the manual actions and slot variables. The host rendering limitation above
currently prevents these virtual tiles from displaying.

The integration provides two manual actions, **Increase application volume** and **Decrease
application volume**. Choose an application with an active audio session from the action's
application list. If the list is empty, start audio in the application and refresh the list. Actions
saved with the older mixer-column parameter continue to target that column on the selected mixer
page.

Read-only variables:

- `slot-1-app` through `slot-6-app`: application name and current volume, or `Empty`.
- `slot-1-volume` through `slot-6-volume`: volume percentage, or zero for an empty slot.
- `slot-1-muted` through `slot-6-muted`: mute state, or false for an empty slot.
- `slot-1-active` through `slot-6-active`: whether an active application occupies the slot.
- `page`, `page-count`, and `total`: selected page, number of pages, and detected application count.

## Package a release

The manifest currently declares only `win-x64`. Build and inspect the package with:

```powershell
macrodeck-plugin build --source src\VolumeMixer --rid win-x64 --output artifacts
macrodeck-plugin inspect --artifact artifacts\<plugin-id>-<version>-win-x64.macroDeckPlugin
```

The plugin is framework-dependent and uses the .NET 10 runtime shipped with Macro Deck. Release
artifacts must be built with `macrodeck-plugin build`; a regular `dotnet build -c Release` does not
create the manifest's `runtimes\win-x64` entrypoint. SDK package versions are centrally managed in
`Directory.Packages.props`; `MacroDeckSdkVersion` currently resolves every Macro Deck package to
`3.0.0`.

## Dependencies

- [Macro Deck Plugin SDK](https://github.com/Macro-Deck-App/Macro-Deck-3)
- [NAudio](https://github.com/naudio/NAudio), for Windows audio session enumeration and volume control
- `System.Drawing.Common`, for extracting executable icons on Windows

The plugin reads local Windows process and audio-session metadata. It does not send that data to a
remote service. The plugin icon is an AI-generated asset, disclosed in the manifest.

## License

MIT. See [LICENSE](LICENSE).
