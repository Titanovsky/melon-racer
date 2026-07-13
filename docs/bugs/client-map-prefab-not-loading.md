# Client Map Prefab Not Loading

## Symptoms

- The host could see and collide with the active track prefab, but clients had no track visuals or collision.
- Clients could still activate checkpoint triggers even though the visible and physical track was missing.
- After a map vote, the host loaded the selected map while clients remained on the previous map.
- Some lightweight components, such as the 2D skybox, could update on clients despite the rest of the track remaining incorrect.

## Root Cause

The host changed `MapInstance.MapName` only on its own instance. `MapInstance` performs map loading locally, so clients never received an instruction to unload their current map and load the newly selected package.

The track prefab was also cloned only by the host and sent through `NetworkSpawn`. This treated a large hierarchy of static meshes, collision data, lighting, spawn points, and triggers as a runtime network object. Parts of the hierarchy could reach clients, but the complete static visual and physical representation was not reconstructed reliably.

These two problems produced the misleading combination of working triggers or skybox components with missing track geometry and stale client maps.

## Fix

`GameManager` now stores the selected package ident in the host-synchronized `ActiveMapName` property. Every client applies this value to its own `MapInstance`, allowing the component to perform the required local unload and asynchronous load.

When `OnMapLoaded` fires, every peer clones the matching track prefab locally. The cloned hierarchy is marked with `NetworkMode.Never` because its static content is deterministic and does not need runtime replication. Gameplay authority remains on the host: checkpoint triggers exist locally for consistent collision geometry, but `TriggerSegment` processes race progress only when `Networking.IsHost` is true.

This also supports late joiners because they receive `ActiveMapName`, load the current map locally, and create the correct track prefab after loading completes.
