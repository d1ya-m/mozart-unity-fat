# Diminished Reality

## Concept

In this project, diminished reality means that the user still sees the real environment through passthrough, but selected real-world surfaces can be visually suppressed and replaced with virtual content.

The goal is not full replacement of the room. The goal is controlled blending between:

- the real environment
- the Alternate Scene revealed through Portal Windows
- always-visible virtual interaction content

For terminology, see [Glossary](glossary.md).

## Visual Building Blocks

### Passthrough Walls

Certain wall, floor, or ceiling proxy meshes are rendered with a passthrough material.

These objects live on the `passthroughWalls` layer and visually represent areas where the real camera feed should remain visible.

Relevant assets:

- `Assets/Materials/Passthrough.mat`
- `Assets/Materials/Shaders/SelectivePassthroughStencil.shader`
- `Assets/Prefabs/PlaneMeshWallBsckground.prefab`
- `Assets/Prefabs/PlaneMeshCeilingBackground.prefab`

### Portal Window

A Portal Window is the user-facing concept of the opening into the Alternate Scene.

At render level it is built from an invisible Portal Mask mesh, usually a box or window-like surface.

Its job is to:

- write to the depth buffer
- write a stencil reference
- remain visually invisible

This is implemented by:

- `Assets/Prefabs/PortalMask.prefab`
- `Assets/Materials/StencilMask.mat`
- `Assets/Materials/Shaders/StencilMask.shader`

### Alternate Scene

The Alternate Scene is the virtual representation of the room that becomes visible through the Portal Window.

In the current implementation this is primarily driven by:

- the server scene mesh
- MRUK room geometry after it is reassigned to `portalContent`

Relevant assets:

- `Assets/Materials/Shaders/PortalContentUnlit.shader`
- `Assets/Scripts/Mesh/LayerApplier.cs`

## Current Interaction Between These Parts

The effect works as follows:

1. Passthrough Walls render the real-world camera feed.
2. The Portal Window mask writes depth and stencil but does not draw color.
3. The passthrough wall shader skips pixels where the portal stencil is present.
4. The Alternate Scene renders with `Stencil == 6` and `ZTest Always`, so it appears through the Portal Window.

## Why Depth Matters

An important implementation detail is that the Portal Window mask writes to the depth buffer.

This is not only a stencil mask. It is also a depth blocker for Passthrough Walls.

This became necessary because on-device behavior showed that passthrough walls responded more reliably to depth blocking than to a purely custom `RenderObjects` stencil pass.

## Always-Visible Runtime Content

Objects such as `MAT` and `MatGrid` are conceptually different from the Alternate Scene:

- they are interactive tools or scene objects
- they may appear in front of or behind Portal Windows
- they are intended to stay visible independently of world depth

These objects now use a material-based overlay approach instead of a renderer-feature-based redraw.

That behavior is handled by:

- `Assets/Scripts/Managers/AlwaysVisibleContentRenderer.cs`
- `Assets/Materials/Shaders/AlwaysVisibleContentUnlit.shader`

## Current Practical Mental Model

A useful way to think about the scene is:

- Passthrough Walls define where the real world stays visible
- Portal Windows carve openings into those Passthrough Walls
- the Alternate Scene fills those openings with a virtual representation
- interactive content is rendered independently as Always-Visible Content
