# Glossary

## Why This Exists

This project contains several terms that were inherited from older implementations or from scene metadata. This glossary defines the preferred language for the documentation.

## Preferred Terms

### Portal Window

Preferred meaning:

- a runtime object that defines an opening into the alternate scene
- visually, this is the window through which the user sees the alternate representation

Implementation note:

- these objects currently come from ARCOR collision object data
- in code and scene metadata they may still appear as `collision objects` or `collision boxes`
- in rendering they are backed by `PortalMask.prefab`

Documentation rule:

- use `Portal Window` when talking about the concept or user-facing behavior
- use `collision object` only when talking about the original scene metadata or code path that spawns it

### Alternate Scene

Preferred meaning:

- the alternative virtual representation of the room shown through a Portal Window

Implementation note:

- at runtime this is currently driven mainly by the server scene mesh and MRUK room geometry
- in code this still often appears as `scene mesh` or `serverSceneMesh`

Documentation rule:

- use `Alternate Scene` when explaining the feature conceptually
- use `scene mesh` only when referring to the concrete implementation or code variables

### Portal Mask

Preferred meaning:

- the invisible rendering mask that writes depth and stencil for a Portal Window

Implementation note:

- this is handled by `PortalMask.prefab`, `StencilMask.mat`, and `StencilMask.shader`

### Portal Content

Preferred meaning:

- the rendered geometry that is visible only inside the Portal Window

Implementation note:

- the current implementation uses layer `portalContent`
- the Alternate Scene is the main content rendered in this path

### Passthrough Walls

Preferred meaning:

- wall, floor, or ceiling proxy geometry rendered with passthrough materials to keep the real world visible

Implementation note:

- this uses layer `passthroughWalls`

### Always-Visible Content

Preferred meaning:

- virtual objects that should remain visible independently of normal world depth

Implementation note:

- examples include `MAT` and `MatGrid`
- this is implemented by `AlwaysVisibleContentRenderer` and `AlwaysVisibleContentUnlit.shader`

### Legacy `content` Layer

Preferred meaning:

- deprecated layer slot kept only to preserve Unity layer indices

Documentation rule:

- do not use this layer for new systems
- mention it only when discussing legacy history or compatibility
