# Debugging and Profiling

## Primary Device Symptom

The most important historical issue in this project was apparent drift on device.

What it looked like:

- virtual content seemed to detach from the world
- geometry reacted incorrectly to head movement
- perspective looked wrong
- after some time the content could snap back into place

The important conclusion was:

> In this case the main cause was render-path instability under load, not a pure anchoring error.

## Why This Was Misleading

The issue resembled tracking drift, but the strongest evidence pointed elsewhere:

- the effect correlated with FPS drops
- it affected content routed through special render paths
- content outside those paths looked more stable
- disabling the old stencil `RenderObjects` path removed the spikes

## On-Device Overlay

The project contains a simple performance overlay:

- `Assets/Scripts/Debug/PerformanceOverlay.cs`

Use it to monitor:

- FPS
- frame time

If drift appears together with frame-time spikes, investigate rendering first.

## Profiling Workflow

### In Editor

1. Switch the project to the mobile quality / URP setup.
2. Open Unity Profiler.
3. Focus on `CPU Usage`.
4. Inspect `RenderLoop` spikes.
5. Compare stable and unstable frames.

Useful additional modules:

- `Rendering`
- `GPU Usage` when available

### On Device

Recommended build settings for diagnosis:

- `Development Build`
- `Autoconnect Profiler`

Be careful with `Script Debugging`, because it adds overhead and can distort timings.

## What to Check First

### 1. Renderer Features

If a visual issue appears only on device, first inspect:

- `Assets/Settings/Mobile_Renderer.asset`

Historically, custom `RenderObjects` features were the main source of instability.

### 2. Stereo Correctness

If something appears only in one eye:

- inspect the shader for XR stereo macros
- confirm the shader is valid for single-pass instanced rendering

### 3. Shader Inclusion in Build

If a material works in editor but not on device:

- suspect shader stripping
- check whether a template material is needed in `Resources`

This was already relevant for the always-visible content overlay path.

### 4. Passthrough Blend Behavior

If passthrough walls look milky or washed out:

- inspect the passthrough shader and blend mode
- compare against the Oculus selective passthrough approach

The project intentionally moved away from the destructive passthrough shader path because it produced incorrect visual blending.

## Known Sensitive Areas

- portal mask depth behavior
- passthrough wall stencil behavior
- portal content shaders
- build-time shader stripping
- reintroduction of custom renderer features

## Practical Rule

When a future rendering issue appears, test in this order:

1. frame timing
2. renderer path differences between editor and device
3. shader inclusion / stripping
4. stereo correctness
5. only then anchoring or spatial alignment
