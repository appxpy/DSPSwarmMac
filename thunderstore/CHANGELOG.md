## 1.2.0
- **Fix major performance regression introduced in 1.1.0.** The near-sail path drew the *entire* swarm as full solar-sail mesh instances every frame, so frame cost grew linearly with swarm size — large swarms dropped from 60 to 30 FPS. Near sails are now gated: the pass runs only when the camera is actually within 2500 units of an active sail orbit, which in practice is almost never.
- New config option `Rendering / NearSails`: `Auto` (default, gated), `Off` (never draw near sails), `Always` (old 1.1.0 behaviour).
- Replaced per-frame `FieldInfo.GetValue` reflection with Harmony `FieldRef` accessors — no more boxing of `sizeInMat` every frame.
- Cache shader property IDs via `Shader.PropertyToID` instead of re-hashing name strings on every `SetBuffer`/`SetVector`.
- Release GPU buffers on plugin unload instead of leaking them across save reloads.
- The identity buffer is no longer padded to a 65536 minimum, and is not allocated at all while the near pass is gated off.

## 1.1.0
- Fix near solar sails (within 2.5 km) as well: `DysonSwarm.DrawModel` now draws all sails with a plain instanced call and an identity `_NearIdBuffer`, bypassing the append-buffer counter / `CopyCount` / indirect-args path that is unreliable on D3DMetal.

## 1.0.0
- Initial release: far solar sails drawn as chunked indexed triangles (fixes invisible swarm on CrossOver/D3DMetal).
