# DSPSwarmMac (DSPSwarmDrawFix)

Fixes the invisible Dyson Swarm when playing **Dyson Sphere Program** on macOS
via CrossOver/Wine with the **D3DMetal** graphics backend.

Without the mod the swarm's solar sails are simply not rendered, while everything
else in the game looks fine. With it, the swarm shows up as it should.

Harmless on other backends (DXVK, Windows) — it renders identically there.

If the mod ever fails (for example after a game update), it logs an error once and
falls back to the game's own rendering instead of breaking anything.

## Settings

`Rendering / NearSails` — how to draw sails you get close to, which the game renders
as a solid mesh rather than a flat sprite.

| Value | Behaviour |
|---|---|
| `Auto` (default) | Draw them when you're actually near the swarm |
| `Off` | Never draw them; sails stay flat up close |
| `Always` | Always draw them — costs a lot of frame rate on a large swarm |

Leave this on `Auto` unless you have a reason not to.

## Installation

**Thunderstore (recommended):** install [appxpy/DSPSwarmDrawFix](https://thunderstore.io/c/dyson-sphere-program/p/appxpy/DSPSwarmDrawFix/) via r2modman — search for `DSPSwarmDrawFix` in the online mod list.

Manual: drop `DSPSwarmDrawFix.dll` into `BepInEx/plugins/`.

Requires BepInEx 5.4.x.

## Building

```
dotnet build -c Release -p:GameLibs=<dir>
```

where `<dir>` contains two symlinks/folders:

- `core/` → your `BepInEx/core` (BepInEx.dll, 0Harmony.dll)
- `managed/` → the game's `DSPGAME_Data/Managed`

## Packaging for Thunderstore/r2modman

Zip the contents of `thunderstore/` together with the built
`DSPSwarmDrawFix.dll` (flat, no top-level folder).
