# Reference assemblies

The plugin compiles against the game's managed assemblies. No game DLL is ever committed.

## Option A (default, what CI does): NuGet `ValheimGameLibs`

`src/RlValheimLink/RlValheimLink.csproj` references the community package
[`ValheimGameLibs`](https://www.nuget.org/packages/ValheimGameLibs) (author `artumino`,
publicized and stripped game assemblies). Version pinned by `GameLibsVersion` in the csproj
(`0.221.4`, published 2025-09-10). Operator ruling 2026-10-17: CI compiles against this package.

The package ships two Steamworks assemblies with the same types; the csproj drops
`assembly_steamworks.dll` and keeps `com.rlabrecque.steamworks.net.dll` (the one the game loads).

When the game updates past the package: bump `GameLibsVersion` if a newer package exists, or
build with Option B until it does. The mod reads `Version.CurrentVersion` at runtime, so a build
against an older reference set still reports the installed game version.

## Option B: your own install (`VALHEIM_INSTALL`)

```bash
export VALHEIM_INSTALL="$HOME/Library/Application Support/Steam/steamapps/common/Valheim"   # macOS
# or  VALHEIM_INSTALL="C:\Program Files (x86)\Steam\steamapps\common\Valheim"               # Windows
# or  VALHEIM_INSTALL=/path/to/valheim_server                                               # dedicated server install
dotnet build src/RlValheimLink/RlValheimLink.csproj -c Release
```

The csproj looks for `valheim_Data/Managed` (client) or `valheim_server_Data/Managed` (server)
under that directory and, when `assembly_valheim.dll` is there, references
`assembly_valheim`, `assembly_guiutils`, `assembly_utils` and `com.rlabrecque.steamworks.net`
from it instead of NuGet. The **publicizer step** is
[`BepInEx.AssemblyPublicizer.MSBuild`](https://github.com/BepInEx/BepInEx.AssemblyPublicizer):
`Publicize="true"` on each game reference makes private members (`Minimap.m_explored`,
`Player.m_trophies`, `Humanoid.m_helmetItem`, …) visible at compile time. Unity's Mono runtime
does not enforce visibility, so the built DLL runs against the real, unpublicized game.

The build prints which option it used:
`RlValheimLink: game assemblies from NuGet ValheimGameLibs 0.221.4` or
`… from local install at <path>`.

## BepInEx and Unity

`BepInEx.Core 5.4.21` (+ `HarmonyX`, `BepInEx.BaseLib`) and `UnityEngine.Modules 2022.3.50`
come from `https://nuget.bepinex.dev/v3/index.json` (see `NuGet.config`; source mapping keeps
nuget.org packages off that feed so a feed outage cannot break the contract library or tests).
The Thunderstore runtime dependency is `denikson-BepInExPack_Valheim-5.4.2351`.

## Newtonsoft.Json

Compiled against `Newtonsoft.Json 13.0.3` from nuget.org; at runtime the game loads the copy from
`ValheimModding-JsonDotNET` (13.0.x), listed as a Thunderstore dependency. Not shipped in our zip.
