# ADR-001: Portal Rendering on Quest

## Status

Accepted

## Context

The project needed a Portal Window based diminished reality effect on Meta Quest 3:

- passthrough remains visible outside the Portal Window
- an Alternate Scene is visible through the Portal Window
- additional runtime content stays visible as Always-Visible Content

An earlier implementation used URP `RenderObjects` features for stencil writing and content redraw on mobile.

That implementation caused:

- periodic render-loop spikes
- FPS drops
- device-only visual instability that looked like drift

## Decision

The project will use a material-driven Portal Window implementation instead of URP `RenderObjects` features on mobile XR.

The chosen design is:

- Portal Window objects use `StencilMask.shader`
- Passthrough Walls use `SelectivePassthroughStencil.shader`
- the Alternate Scene uses `PortalContentUnlit.shader`
- Always-Visible Content uses `AlwaysVisibleContentUnlit.shader`
- `Mobile_Renderer.asset` keeps renderer features disabled

## Consequences

### Positive

- more stable on Quest
- simpler mobile renderer asset
- easier reasoning about render order
- fewer hidden renderer-side interactions

### Negative

- portal behavior is now spread across several shaders
- documentation is required to keep the design understandable
- future developers may be tempted to reintroduce `RenderObjects`

## Guardrail

Any future return to renderer-feature-based portal rendering must be treated as a new experiment and validated on real hardware, not only in editor or Meta Link.
