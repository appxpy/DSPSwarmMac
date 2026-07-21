# DSPSwarmMac (DSPSwarmDrawFix)

Fixes the invisible Dyson Swarm when playing **Dyson Sphere Program** on macOS
via CrossOver/Wine with the **D3DMetal** graphics backend.

## The bug

The game draws far solar sails with a single
`Graphics.DrawProceduralNow(MeshTopology.Quads, sailCount * 12)` call — hundreds of
thousands of vertices in one draw. Direct3D 11 has no quad primitive, so Unity
emulates quads with an internal index buffer, and D3DMetal silently drops this giant
emulated draw call. Small quad draws (sail bullets, power lines, warning icons) work
fine — which is why only the swarm disappears while everything else renders.

DXVK handles the same draw correctly, so the swarm is visible there — but DXVK on
macOS is typically much slower than D3DMetal.

## The fix

Same shader, same GPU buffers, but the draw is issued as **indexed triangles in
chunks of 5460 sails** (65,520 vertices, under the 16-bit index limit), using the
shader's own instancing support:

```
sailIndex = SV_InstanceID * _Stride + SV_VertexID / 12
```

A Harmony prefix on `DysonSwarm.DrawPost` replaces the vanilla draw. If the
replacement ever throws (e.g. a game update changes `DrawPost`), the mod logs an
error once and falls back to the vanilla path instead of breaking the game.

Harmless on other backends (DXVK, Windows) — the result is visually identical.

## Installation

r2modman: **Settings → Import local mod** → select the release zip.

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
