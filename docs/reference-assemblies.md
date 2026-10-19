# Reference assemblies

The plugin compiles against the game's managed assemblies. No game DLL is ever committed.

## Option A (default, what CI does): NuGet reference packages matching the game build

`src/RlValheimLink/RlValheimLink.csproj` references
[`Digitalroot.Valheim.Common.References`](https://www.nuget.org/packages/Digitalroot.Valheim.Common.References)
(publicized game assemblies with their real names, versioned like the game: `GameRefsVersion`
= `1.0.20`) and `UnityEngine.Modules` from the BepInEx feed at the game's Unity version
(`UnityRefsVersion` = `6000.0.75`). The operator's server logs
`Valheim Version: l-1.0.20` and `Detected Unity version: v6000.0.75f1`; keep both properties in
step with that log when the game updates (the community `ValheimGameLibs` package stopped at 0.221.4
and is no longer used). Operator ruling 2026-10-17: CI compiles against NuGet references, no game DLLs
are committed.

No Steamworks reference is shipped: the Steam id is read by reflection into the game's own
`com.rlabrecque.steamworks.net` at runtime.

When the game updates past the packages: bump the two versions if newer packages exist, or build
with Option B until they do. The mod reads `Version.CurrentVersion` at runtime, so a build against an
older reference set still reports the installed game version — but members can go missing, which
surfaces as `MissingMethodException` / `MissingFieldException` in the BepInEx log (the captures catch
and log those; the console command registration is reflection-based for this reason).

## Option B: your own install (`VALHEIM_INSTALL`)

```bash
export VALHEIM_INSTALL="$HOME/Library/Application Support/Steam/steamapps/common/Valheim"   # macOS
# or  VALHEIM_INSTALL="C:\Program Files (x86)\Steam\steamapps\common\Valheim"               # Windows
# or  VALHEIM_INSTALL=/path/to/valheim_server                                               # dedicated server install
dotnet build src/RlValheimLink/RlValheimLink.csproj -c Release
```

The csproj looks for `valheim_Data/Managed` (client) or `valheim_server_Data/Managed` (server)
under that directory and, when `assembly_valheim.dll` is there, references
`assembly_valheim`, `assembly_guiutils` and `assembly_utils` from it instead of NuGet. The **publicizer step** is
[`BepInEx.AssemblyPublicizer.MSBuild`](https://github.com/BepInEx/BepInEx.AssemblyPublicizer):
`Publicize="true"` on each game reference makes private members (`Minimap.m_explored`,
`Player.m_trophies`, `Humanoid.m_helmetItem`, …) visible at compile time. Unity's Mono runtime
does not enforce visibility, so the built DLL runs against the real, unpublicized game.

The build prints which option it used:
`RlValheimLink: game assemblies from NuGet ValheimGameLibs 0.221.4` or
`… from local install at <path>`.

## BepInEx and Unity

`BepInEx.Core 5.4.21` (+ `HarmonyX`, `BepInEx.BaseLib`) and `UnityEngine.Modules 6000.0.75`
come from `https://nuget.bepinex.dev/v3/index.json` (see `NuGet.config`; source mapping keeps
nuget.org packages off that feed so a feed outage cannot break the contract library or tests).
The Thunderstore runtime dependency is `denikson-BepInExPack_Valheim-5.4.2351`.

## Newtonsoft.Json

Compiled against `Newtonsoft.Json 13.0.3` from nuget.org; at runtime the game loads the copy from
`ValheimModding-JsonDotNET` (13.0.x), listed as a Thunderstore dependency. Not shipped in our zip.
