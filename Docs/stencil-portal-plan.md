# Stencil Portal Refactor Plan

## Goal

Keep the visual behavior:

- outside the portal window render the normal scene
- inside the portal window render the aligned "shadow" scene / scene mesh

At the same time remove the mobile XR performance spikes caused by the current stencil pipeline.

## Confirmed Findings

- The regular frame drops disappear when the `Stencil` `RenderObjects` feature in [Mobile_Renderer.asset](/d:/projects/test_room/Assets/Settings/Mobile_Renderer.asset) is disabled.
- The current mobile renderer writes stencil from layer `ground`.
- `ground` currently contains the scene mesh, which means the scene mesh is being used as the stencil writer.
- That makes the renderer redraw a large mesh just to populate the stencil buffer.
- Collision boxes look more stable because they are not routed through the same custom renderer path.
- The current setup mixes multiple stencil-related shaders/materials with inconsistent reference values (`1` vs `6`).
- `GrabbableBox` currently uses [StencilMask.mat](/d:/projects/test_room/Assets/Materials/StencilMask.mat), which is risky if the same prefab is reused as a generic collision box.

## Target Architecture

Separate the portal into three explicit roles:

1. `PortalMask`
   Only the portal cube/window mesh. Its only job is writing stencil.
2. `PortalContent`
   Only the scene mesh / shadow scene that should be visible through the portal.
3. `MainContent`
   Everything that should render normally outside the portal.

Rendering order:

1. Render the normal scene normally.
2. Render the portal mask with a very cheap stencil-write-only shader.
3. Render the portal content only where `stencil == ref`.

## Implementation Plan

### Phase 1: Untangle Responsibilities

1. Add dedicated layers for the portal system.
   Proposed:
   - `portalMask`
   - `portalContent`
2. Stop using `ground` as the stencil-writer layer.
3. Keep the scene mesh on a content layer dedicated to portal rendering, not on the mask-writing layer.
4. Make sure generic collision boxes do not use a stencil mask material unless they are intentionally portal masks.

## Phase 2: Simplify Renderer Features

1. Replace the current `Stencil` feature in [Mobile_Renderer.asset](/d:/projects/test_room/Assets/Settings/Mobile_Renderer.asset) so it targets only `portalMask`.
2. Make the stencil write pass use a minimal override material based on [StencilMask.shader](/d:/projects/test_room/Assets/Materials/Shaders/StencilMask.shader).
3. Add or update the content pass so it targets only `portalContent`.
4. Configure the content pass to render only where the portal stencil reference matches.
5. Use one stencil reference consistently across:
   - renderer features
   - [StencilMask.shader](/d:/projects/test_room/Assets/Materials/Shaders/StencilMask.shader)
   - [Stencil.shader](/d:/projects/test_room/Assets/Materials/Shaders/Stencil.shader)
   - any portal materials still in use

## Phase 3: Clean Up Materials and Shaders

1. Audit which of these assets are actually needed:
   - [StencilMask.mat](/d:/projects/test_room/Assets/Materials/StencilMask.mat)
   - [Stencil_reader.mat](/d:/projects/test_room/Assets/Materials/Stencil_reader.mat)
   - [Stencil_new.mat](/d:/projects/test_room/Assets/Materials/Stencil_new.mat)
   - [Custom_PostStencilClip.mat](/d:/projects/test_room/Assets/Materials/Custom_PostStencilClip.mat)
2. Remove duplicate or obsolete stencil paths once the new renderer setup works.
3. Ensure the final path is understandable:
   - one writer shader/material for the portal mask
   - one content rendering rule for portal content
   - no hidden second path doing the same thing differently

## Phase 4: Scene and Prefab Wiring

1. Create or identify the actual portal window object and put it on `portalMask`.
2. Put the scene mesh / shadow-scene root on `portalContent`.
3. Verify that `GameManager` layer assignment for the server scene mesh matches the new portal content layer.
4. Verify that MRUK room/scene mesh helpers are not reassigning the mesh back to an old layer.
5. Split portal-prefab usage from generic collision-box usage if they currently share [GrabbableBox.prefab](/d:/projects/test_room/Assets/Prefabs/GrabbableBox.prefab).

## Phase 5: Validation

1. In Editor with Mobile quality:
   - verify the portal effect still works
   - verify `RenderLoop` spikes are reduced or gone
2. On device:
   - confirm the portal still shows the shadow scene correctly
   - confirm FPS/frame time no longer drop periodically
   - confirm the previous visual "drift" of portal-rendered content is gone
3. If needed, test `m_UseNativeRenderPass` in [Mobile_Renderer.asset](/d:/projects/test_room/Assets/Settings/Mobile_Renderer.asset) both on and off after the refactor, but only after the layer/pass separation is fixed.

## Execution Order For Next Steps

1. Inspect and update project layers.
2. Rewire the scene mesh to a dedicated portal-content layer.
3. Reconfigure the mobile renderer features for `portalMask` and `portalContent`.
4. Fix stencil reference consistency.
5. Clean up prefabs/material assignments that still misuse the stencil mask.
6. Re-profile in Editor Mobile mode.
7. Re-test on device.

## Notes

- The main design principle is: the portal mask should be tiny and cheap, while the portal content can be large.
- The current setup appears inverted: the large scene mesh is participating in the stencil-write stage.
- The refactor should preserve the visual idea while reducing the number and cost of XR render passes.
