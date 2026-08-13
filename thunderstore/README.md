# DSPSwarmDrawFix

Fixes the invisible Dyson Swarm when playing Dyson Sphere Program on macOS
via CrossOver with the D3DMetal graphics backend.

Without the mod the swarm's solar sails just don't render, while the rest of the
game looks fine. With it, the swarm shows up as it should.

Harmless on other backends (DXVK/Windows) — it renders identically there.

If the mod ever fails (for example after a game update), it logs an error once and
falls back to the game's own rendering instead of breaking anything.

## Settings

`Rendering / NearSails` — how to draw sails you get close to, which the game renders
as a solid mesh rather than a flat sprite.

- `Auto` (default) — draw them when you're actually near the swarm
- `Off` — never draw them; sails stay flat up close
- `Always` — always draw them; costs a lot of frame rate on a large swarm

Leave this on `Auto` unless you have a reason not to.

**On 1.1.0 and your frame rate fell as the swarm grew? That's this setting — update.**

## Installation

Install via r2modman/Thunderstore Mod Manager, or drop `DSPSwarmDrawFix.dll` into `BepInEx/plugins/`.

Source: https://github.com/appxpy/DSPSwarmMac

## Changelog

- 1.2.0 — fix a large frame rate drop on big swarms introduced in 1.1.0; add the `NearSails` setting
- 1.1.0 — fix near sails as well
- 1.0.0 — initial release
