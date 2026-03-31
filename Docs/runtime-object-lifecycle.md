# Runtime Object Lifecycle

## Overview

Scene content is mostly created at runtime from the ARCOR2 session state.

The main orchestration happens in:

- `Assets/Scripts/Managers/GameManager.cs`

For terminology, see [Glossary](glossary.md).

## Scene Opening Flow

When navigation changes into scene mode:

1. `GameManager.SceneOpened()` is called.
2. Existing runtime content is cleaned up.
3. The active `SceneManager` is taken from the ARCOR2 session.
4. The current Alternate Scene implementation is loaded asynchronously.
5. Action objects are spawned from the scene definition.

## Action Object Types

Important spawned object types:

- `Mat`
- `MatGrid`
- Portal Windows
- optional imported meshes attached to action objects

### `Mat`

Spawn path:

- instantiated from `MATPrefab`
- parented under `Origin`
- initialized with action-object data
- receives `AlwaysVisibleContentRenderer`

### `MatGrid`

Spawn path:

- instantiated from `MatGridPrefab`
- parented under `Origin`
- initialized with action-object data
- internally spawns tile content
- receives `AlwaysVisibleContentRenderer`

### Portal Windows

Portal Windows are backed by ARCOR collision object data.

Current path:

- a collision object arrives from the scene definition
- it is instantiated from `CollisionBoxPrefab`
- by current project convention this prefab is `PortalMask.prefab`
- it is parented under `Origin`
- its transform and scale are driven by scene metadata

This means collision-object scene data currently doubles as Portal Window placement data.

## Alternate Scene Lifecycle

The Alternate Scene is currently implemented mainly through the scene mesh path.

Once available:

1. the scene mesh is parented under `SceneMeshOrigin`
2. local transform offsets are applied
3. colliders are disabled
4. original materials are cached
5. portal materials are generated from the original materials

Important methods:

- `AttachServerSceneMesh`
- `CacheSceneMeshMaterials`
- `ApplySceneMeshRenderMode`

## Scene Mesh Material Strategy

The scene mesh keeps two logical material sets:

- original materials
- portal materials based on `PortalContentUnlit`

`GameManager.CopyPortalMaterialProperties` copies texture and base color information from the original materials into the lightweight portal material set.

## MRUK Layer Reassignment

MRUK room content is pushed onto the `portalContent` layer by:

- `Assets/Scripts/Mesh/LayerApplier.cs`

This keeps scanned-room geometry in the same rendering path as the scene-mesh-based Alternate Scene.

## Cleanup

When leaving scene mode:

- Portal Windows are destroyed
- the Alternate Scene implementation is cleaned up
- action-object collection subscriptions are removed
- cached state is reset

This is handled by `GameManager.CleanupSceneObjects()`.
