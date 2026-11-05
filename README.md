# Tree Weak Spots

A client-side BepInEx mod for Valheim: a glowing band wraps fallen logs when you aim with an axe, and chopping on it deals critical damage. Logs also get the sledgehammer's overhead swing.

Mod para Valheim (BepInEx, client-side): uma faixa de luz contorna os troncos caídos ao mirar com o machado; golpear nela dá dano crítico. Nos troncos, o golpe vira a martelada da marreta.

For players, see [package/README.md](package/README.md). That file is also the Thunderstore page.
Para jogadores, veja [package/README.md](package/README.md).

Sister mod / mod irmão: [Ore Weak Spots](https://github.com/Felihens/OreWeakSpots).

## Project layout

```
src/                C# source (Plugin.cs = config + Harmony patches, WeakSpotManager.cs = logic and visuals)
package/            Thunderstore package contents (manifest.json, icon.png, README.md, CHANGELOG.md)
docs/screenshots/   Images used by the Thunderstore page
build.sh            Compiles the DLL and copies it into the local game
pack.ps1            Builds dist/TreeWeakSpots-<version>.zip from package/
```

## Building

No Visual Studio or .NET SDK is needed. The build uses the C# compiler that ships with Windows (.NET Framework 4.x), so the code is limited to C# 5.

1. Install Valheim with BepInExPack.
2. Edit `GAME=` in `build.sh` to point at your Valheim folder.
3. Run `sh build.sh` from Git Bash (or `sh build.sh nocopy` to compile without installing).
4. Run `powershell -File pack.ps1` to build the release zip in `dist/`.

When you release a new version, update `VERSION` in `src/Plugin.cs`, `version_number` in `package/manifest.json`, and `package/CHANGELOG.md`.

## How it works

- A Harmony prefix on `TreeLog.Damage` multiplies the hit's `m_chop` damage before it is sent over the network, so nothing is needed on the server. Axe hits on trees carry the `WoodCutting` skill, not `Axes`.
- The band is a hand-built tube mesh parented to the log's mesh. The log's long axis and radius come from the mesh bounds, and a hit counts as a crit when its distance along that axis to the band is within the tolerance.
- The overhead swing patches `Humanoid.StartAttack`: for that one call, the axe's shared `Attack` borrows the sledge's animation (read from the `SledgeStagbreaker` prefab at runtime) and the vertical attack type, then gets restored.
- With ComboChopping installed, its `TreeLog.Damage` prefix is removed at startup, so it keeps working on standing trees only.

## License

MIT. See [LICENSE](LICENSE).
