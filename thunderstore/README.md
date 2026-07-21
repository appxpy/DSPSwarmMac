# DSPSwarmDrawFix

Fixes the invisible Dyson Swarm when playing Dyson Sphere Program on macOS
via CrossOver with the D3DMetal graphics backend.

## The bug

The game draws far solar sails with a single
`Graphics.DrawProceduralNow(MeshTopology.Quads, sailCount * 12)` call — hundreds of
thousands of vertices in one draw. Direct3D 11 has no quad primitive, so Unity
emulates quads with an internal index buffer, and D3DMetal silently drops this giant
emulated draw call (small quad draws — sail bullets, power lines, warning icons —
work fine, which is why only the swarm disappears).

## The fix

Same shader, same GPU buffers, but the draw is issued as indexed triangles in chunks
of 5460 sails (65,520 vertices, under the 16-bit index limit), using the shader's own
instancing support (`sailIndex = SV_InstanceID * _Stride + SV_VertexID / 12`).

If the replacement ever fails (e.g. after a game update changes `DysonSwarm.DrawPost`),
the mod logs an error and falls back to the vanilla draw path instead of breaking the game.

Harmless on other backends (DXVK/Windows) — it renders identically.

## Installation

Install via r2modman/Thunderstore Mod Manager, or drop `DSPSwarmDrawFix.dll` into `BepInEx/plugins/`.

Source: https://github.com/appxpy/DSPSwarmMac

## Changelog

- 1.0.0 — initial release
