# Technical Documentation

This directory contains the internal technical documentation for the MOZART Feasibility Analysis Tool.

The documentation is maintained as part of the repository so that architectural decisions stay versioned together with code, shaders, scenes, and renderer settings.

## Recommended Reading Order

1. [Glossary](glossary.md)
2. [Setup and External Dependencies](setup-and-external-dependencies.md)
3. [Architecture Overview](architecture-overview.md)
4. [Diminished Reality](diminished-reality.md)
5. [Render Pipeline and Stencil](render-pipeline-and-stencil.md)
6. [Runtime Object Lifecycle](runtime-object-lifecycle.md)
7. [Debugging and Profiling](debugging-and-profiling.md)
8. [ADR-001: Portal Rendering on Quest](adr/adr-001-portal-rendering-on-quest.md)

## Scope

These documents focus on:

- how the application is structured
- how scene content is spawned and aligned
- how diminished reality is implemented on Meta Quest 3
- how the portal / stencil pipeline works
- why the current implementation avoids `RenderObjects` on mobile XR

## Source of Truth

The documentation in `Docs/` is the primary technical reference.

Useful entry points in the codebase:

- `Assets/Scripts/Managers/GameManager.cs`
- `Assets/Scripts/Managers/CommunicationManager.cs`
- `Assets/Scripts/MozartSpatialBridge.cs`
- `Assets/Scripts/Mesh/LayerApplier.cs`
- `Assets/Scripts/Mesh/MeshDownloadManager.cs`
- `Assets/Settings/Mobile_Renderer.asset`
- `Assets/Materials/Shaders/StencilMask.shader`
- `Assets/Materials/Shaders/PortalContentUnlit.shader`
- `Assets/Materials/Shaders/SelectivePassthroughStencil.shader`
- `Assets/Scripts/Managers/AlwaysVisibleContentRenderer.cs`
