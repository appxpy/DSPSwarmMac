## 1.1.0
- Fix near solar sails (within 2.5 km) as well: `DysonSwarm.DrawModel` now draws all sails with a plain instanced call and an identity `_NearIdBuffer`, bypassing the append-buffer counter / `CopyCount` / indirect-args path that is unreliable on D3DMetal.

## 1.0.0
- Initial release: far solar sails drawn as chunked indexed triangles (fixes invisible swarm on CrossOver/D3DMetal).
