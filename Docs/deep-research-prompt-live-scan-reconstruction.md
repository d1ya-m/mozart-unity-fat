# Deep-Research Prompt — Quest 3 Live Room Scan → Mesh → Object Segmentation

> Paste the section under **"PROMPT BEGINS"** into Claude or ChatGPT deep-research
> mode. Everything above it is context for the human. The prompt is self-contained.

---

## How to use
- Copy from **PROMPT BEGINS** to **PROMPT ENDS**.
- If the tool supports attachments, also attach:
  `Docs/object-shaped-portals-implementation.md` (Part Two, §S0–§S9),
  `captures/unproject_clean.py`, `captures/reconstruct_clean.py`,
  and one example `frame_XXXX.json`.
- Ask it to prioritise **concrete, testable recommendations** over generic surveys.

---

## PROMPT BEGINS

### Role
You are a senior computer-vision / 3D-reconstruction engineer. I need a rigorous,
implementation-oriented deep-research review of a real project. Prioritise
**specific, testable fixes and named methods/libraries with rationale** over broad
surveys. Where you recommend an algorithm, state its exact assumptions, inputs,
failure modes, GPU/CPU cost, and how it maps onto the data I actually have. Cite
sources where possible. If you think my whole approach is wrong, say so and propose
the better architecture.

### Project goal (one sentence)
On a **Meta Quest 3**, let a user press a "Scan Room" button, walk around, and from
that scan reconstruct a **3D mesh of the room** that is then **segmented into
individual objects** (floor, walls, and separable objects like chairs, boxes, cups)
so each object's silhouette can become an AR "portal."

### End-to-end pipeline (as designed)
```
Quest capture (per keyframe: depth image + camera pose)
  -> unproject each keyframe's depth to 3D world points
  -> merge all keyframes into one point cloud
  -> Poisson surface reconstruction -> mesh.obj
  -> offline segmentation (RANSAC plane strip + normals-augmented DBSCAN)
  -> clusterN.obj per object + clusters.json manifest
  -> Unity runtime picks a cluster and renders it as a stencil-masked portal
```
The **downstream half** (segmentation → cluster meshes → runtime portal picking via
per-cluster MeshCollider raycast + stencil shader) **already works** with a
pre-baked mesh. The **new, unfinished half** is producing a good mesh *live from the
user's own scan* and then segmenting it well.

### The exact data I capture (per keyframe)
- **Depth image**: 512×512, 16-bit grayscale PNG, value = metric depth in
  millimetres (0–65535 → 0–65.5 m). Source: Quest 3 `EnvironmentDepthManager`
  `_EnvironmentDepthTexture` (Texture2DArray), left-eye slice, linearised to metres.
  The environment-depth sensor is roughly **~30 Hz** and noisy at edges
  ("flying pixels"), unreliable on thin/metallic/reflective surfaces.
- **Depth camera intrinsics**: obtained via C# reflection on the SDK's internal
  `DepthFrameDesc`. I have the frustum **FOV tangents** `[tanL, tanR, tanT, tanD] ≈
  [1.376, 0.839, 0.966, 1.428]` — note the frustum is **asymmetric / off-centre**
  (optical axis lands at pixel ~(194,306), not the image centre (256,256)). Also the
  SDK's own `proj` and `view` matrices, and `near/far = [0.1, Infinity]` (infinite
  far plane / reversed-Z style).
- **Camera pose**, in several redundant forms (recorded because I didn't know which
  convention would reconstruct correctly): Unity head position + rotation
  quaternion; a `head_view` world→eye matrix built from the Unity **head** pose; the
  SDK depth camera's own `depth_view` (from `DepthFrameDesc.createPose`); and the
  SDK's combined `depth_reprojection = proj * view * trackingWorldToLocal`.
- **NO RGB image** is captured (depth + pose only).
- Keyframes are selected by novelty: a new one is written once the head moves
  ≥ 0.3 m OR turns ≥ 20° from every existing keyframe. A typical scan = **80–130
  keyframes** over ~1–2 minutes. Tracking space is identity (= world).

### Unprojection math currently used
For pixel (u,v) with metric depth z, using the FOV tangents (I deliberately avoid
the SDK `proj` matrix — see failed attempts):
```
ray_x = -tanL + (u+0.5)/W * (tanL + tanR)
ray_y = -tanD + (v+0.5)/H * (tanD + tanT)
eye   = (z*ray_x, z*ray_y, -z)          # right-handed, camera looks down -Z
world = inv(view) @ eye                  # view = head_view (preferred) or depth_view
```
Reconstruction then: per-frame voxel-downsample (3 cm) → merge → statistical outlier
removal → normal estimation → Poisson (depth=9) → trim low-density verts → mesh.obj.

### WHAT IS PROVEN CORRECT
1. **Single-frame geometry is correct.** One keyframe's depth unprojects to clean
   flat surfaces: the dominant RANSAC plane covers 31–46% of a frame's points, with
   sensible normals (a frame facing a wall gives a plane whose normal is along that
   wall's axis). So the **per-pixel depth → world math is right.**
2. **Camera positions are correct.** `inv(view) @ [0,0,0,1]` matches the logged head
   position to within a few centimetres per frame.
3. **Room HEIGHT is correct.** The merged cloud's vertical (Y) extent is
   consistently ~3.5–4.3 m — every frame agrees on floor-to-ceiling distance.

### THE CORE UNSOLVED PROBLEM (please focus here)
**Multi-frame horizontal alignment drifts.** When I merge all keyframes:
- The merged bounding box is **~13 m × ~4 m × ~12 m** for a small (~4×3 m) room —
  the X and Z extents are 3–4× too large.
- Adjacent-frame overlap (median nearest-neighbour distance between two consecutive
  keyframes' clouds) is **inconsistent**: some pairs align to 1–2 cm ✅, others are
  40–60 cm off ❌.
- On the merged cloud, RANSAC finds **only floor/ceiling planes** (normal ≈
  (0,1,0)); walls do NOT survive because each frame places the same wall at a
  different world X/Z.

**My diagnosis:** the per-frame **rotation (yaw/heading)** used for unprojection has
a small error that varies frame-to-frame. The vertical axis (gravity) is stable
(hence correct height), but a few degrees of heading error per frame, at 2–4 m
range, smears each wall across metres in world X/Z, and the errors don't cancel.
Camera *position* is fine; camera *orientation* is slightly wrong and inconsistent.

### Fix attempts so far (chronological, with why each failed)
1. **Decode the SDK composite `depth_reprojection` matrix** to recover camera centre
   + ray directions. Failed: the **infinite far plane** makes the standard w=0
   direction-vector trick degenerate; forwards collapsed to −Y.
2. **Use SDK `depth_proj` + `depth_view` separately** (clean matrices via
   reflection). Failed: the `proj` principal-point offset terms `a=proj[0,2]`,
   `b=proj[1,2]` have a sign/convention I could not pin down — even the centre pixel
   came out 34 cm off, because the frustum is **asymmetric** so NDC (0,0) is NOT the
   optical axis.
3. **Raw FOV tangents + `depth_view`** (current base). Fixed the per-pixel error
   (single-frame planarity became correct, height correct), and many adjacent pairs
   now overlap to 1–2 cm — **but** some pairs still 40–60 cm and merged XZ still
   ~13 m. Suspicion: `depth_view` (from `DepthFrameDesc.createPose`) is not
   perfectly time-synced with head motion; its **rotation drifts**.
4. **`head_view` (Unity head pose) instead of `depth_view`** — just added, not yet
   verified. Hypothesis: the head pose rotation is smoother/more reliable than the
   depth camera's create-pose rotation.

### Band-aids currently masking the problem (I want to remove these)
- A hard clamp discarding world points with Y < −0.1 m or Y > 4.5 m (a few bad
  frames cast rays far below the floor / above the ceiling and blow up the bbox).
- Position-based glitch rejection (drop frames whose head pos is < 10 cm from origin
  = tracking lost, or that jump > 2 m from the previous frame).

### Known limitations / constraints
- **No RGB** — depth + pose only. So image-based methods (SAM, Mask R-CNN,
  photometric bundle adjustment, RGB-D ORB-SLAM) are not directly usable unless I
  also capture the passthrough/RGB camera (possible in principle — is it worth it?).
- **~30 Hz noisy depth**, edge flying-pixels, unreliable on thin/metallic surfaces.
- **Asymmetric, infinite-far depth frustum** (unusual projection).
- **Sparse keyframes** (80–130), not a dense continuous depth stream — I select by
  novelty, so there can be large baseline/rotation between consecutive frames.
- Everything downstream is fixed and must not change: segmentation must output
  `clusterN.obj` + `clusters.json`; the runtime picks clusters by MeshCollider
  raycast. So the segmentation input is a **mesh** (or a point cloud I mesh myself).
- Target: runs offline on a PC (Python 3.11, Open3D, NumPy; GPU available if needed).
  Real-time on-headset reconstruction is NOT required — a ~seconds-to-minutes
  offline pass after the scan is acceptable.

### Segmentation, for context (downstream, currently "good enough")
After a mesh exists, I segment with **RANSAC plane removal ×6** (strip
floor/walls/ceiling) then **DBSCAN on a 6-D [x,y,z, w·nx,w·ny,w·nz] feature** so the
cluster boundary lands on normal-discontinuity seams (this splits *touching* objects
that plain position-DBSCAN would merge). It produces ~30 clusters on the pre-baked
dense mesh. Its known weakness: it needs a reasonably **clean, dense** mesh — on a
noisy or misaligned reconstruction it will over-fragment or merge wrongly.

---

### WHAT I NEED FROM YOU (please answer all, in order)

**1. The alignment problem (top priority).**
   - Is my diagnosis (per-frame heading/rotation drift, position fine) the most
     likely cause given the symptoms? What else could produce "height correct, XZ
     3–4× too large, inconsistent adjacent overlap"? (e.g. depth/pose timestamp
     mismatch, per-eye reprojection, a systematic FOV-tangent error, tracking-space
     vs world confusion, a left/right-handed flip that only bites in rotation.)
   - Concretely, how should I fix it? Compare and rank these, with expected effort
     and residual error:
     (a) prefer `head_view` over `depth_view` (rotation from head pose);
     (b) **pairwise + sequential ICP** (point-to-plane) seeding from the captured
         pose, then merge (KinectFusion-style);
     (c) **pose-graph global registration** (Open3D multiway registration /
         `global_optimization`, loop closure) to stop sequential drift accumulating;
     (d) **TSDF fusion** (Open3D `ScalableTSDFVolume`) instead of point-merge +
         Poisson — does it tolerate this drift better or worse?
     (e) something else (bundle adjustment on depth-only correspondences,
         Open3D Tensor `slam` pipeline, etc.).
   - Should I refine poses *before* meshing (register frames) or mesh first and
     accept the drift? Which gives a cleaner segmentable mesh?

**2. Is there information I should be extracting/capturing but am not?**
   - Would capturing the **RGB / passthrough** frame per keyframe materially help
     (enables photometric ICP, RGB-D SLAM, learned features, and image-based
     segmentation like SAM projected back to 3D)? Is Quest 3 passthrough camera
     access available/allowed, and worth the effort here?
   - Are there **per-frame timestamps** I should record to correct depth/pose
     time-sync? Should I capture the **right-eye** depth slice too and use stereo?
   - Would recording the raw device depth + the SDK's per-frame reprojection at
     higher rate (not just novelty keyframes) improve registration?

**3. Is the reconstruction approach itself right?**
   - Point-merge + Poisson vs TSDF vs Open3D's newer Tensor reconstruction/SLAM —
     which is most robust for **sparse, noisy, pose-drifted** Quest depth keyframes?
   - Given the noisy 30 Hz depth, what denoising/temporal-fusion should happen
     before meshing (voxel size, outlier removal params, normal estimation radius,
     Poisson depth)? My current values: 3 cm voxel, statistical outlier (20, 2.0),
     Poisson depth 9 — are these sane for a room-scale scan?

**4. Segmentation — should I change the algorithm for a live-scanned mesh?**
   - My current RANSAC + normals-DBSCAN assumes a clean dense mesh. On a live Quest
     reconstruction (noisier, possibly holes, lower density), will it break? What is
     the most robust **geometry-only (no RGB)** object-instance segmentation for a
     room-scale mesh/point cloud? Rank: region-growing, **LCCP/CPC** (convex/concave
     partition), learned instance seg (**Mask3D, SoftGroup, Point Transformer v3,
     PointGroup**), promptable (**SAM3D / Point-SAM**). For each: input format, does
     it split touching objects, GPU need, pretrained-model availability, and how well
     it tolerates Quest-quality geometry.
   - If a learned model is best, which pretrained checkpoint (ScanNet? S3DIS?)
     generalises to an arbitrary user room, and what preprocessing (voxelization,
     coordinate frame, units) does it expect?
   - Could I **segment on the point cloud directly** (skip Poisson meshing) and only
     mesh each cluster afterward, to avoid meshing artifacts corrupting segmentation?

**5. Alternative architectures — is there a fundamentally better path?**
   - Should I bypass my own reconstruction entirely and use the **Quest's built-in
     Scene Mesh / MRUK room model + scene understanding** (planes, volumes, labelled
     anchors) as the segmentation source? Trade-offs vs a custom dense scan? (The
     built-in scene mesh is coarse and fuses touching objects — but is it "good
     enough" for object-portals, and far simpler?)
   - Is there an off-the-shelf **on-device RGB-D SLAM / spatial mapping** (e.g.
     integrating with ARCore/ARKit-style depth, Open3D SLAM, RTAB-Map, or a Unity
     asset) that would replace my hand-rolled capture+reconstruct with something
     more robust?

**6. Concrete next-experiment plan.**
   - Give me an ordered, minimal list of experiments to run **this week** to
     isolate the alignment cause and pick a fix, each with the exact metric to watch
     (e.g. "run sequential point-to-plane ICP seeded from pose; success = adjacent
     overlap < 3 cm for >90% of pairs AND merged bbox within 20% of tape-measured
     room size AND RANSAC finds ≥2 wall planes with |n·y|<0.2").
   - Tell me what would convince you the reconstruction is "good enough to segment."

### Output format
- Lead with a **direct verdict**: is the current plan fixable as-is, or should the
  architecture change? One paragraph.
- Then a **ranked action list** (do-this-first), each item: what, why, expected
  effort, expected result, how to measure success.
- Then the detailed answers to Q1–Q6.
- Then **risks / unknowns** you couldn't resolve and what data I'd need to resolve
  them.
- Cite methods/libraries by exact name with links where possible.

## PROMPT ENDS
