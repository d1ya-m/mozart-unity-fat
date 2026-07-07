# DYNAMIC_MESH_DEMO_AUDIT

Audit date: 2026-07-07 · Branch: `object-shaped-portals`
Method: every claim re-derived from source. Evidence tags — **FACT** (proven from a
line read), **PARTIAL** (one inferred but constrained link), **FALSE** (contradicts
code), **UNKNOWN** (missing evidence named). No implementation code written this pass.

Goal being assessed: a ONE-session headset demo — scan room → segment → place/remove
object-shaped portals — driven by the **headset's own MRUK/Meta scene mesh** instead
of the external server mesh or the broken depth-frame reconstruction.

---

## TL;DR verdict

- The headset **already runs MRUK** (`MozartSpatialBridge`, MRUK component in scene,
  `LoadSceneOnStartup: 1`, `DataSource: DeviceWithPrefabFallback`) and **can produce a
  room mesh with no external server and no Depth API** — but the scene's `EffectMesh`
  is configured for `Labels: 7` = FLOOR|CEILING|WALL_FACE, **not** `GLOBAL_MESH`. So a
  single dense room Mesh is not being instantiated today; enabling it is a one-flag
  change (§D).
- The original object-shaped-portal segmentation ran on the **external/server textured
  mesh** `mesh-3hz-4.obj` (Meshlab-generated, 270 972 verts, `mtllib` + 407 228 `vt` +
  a 9.96 MB `.jpg`). It is **NOT** the headset MRUK mesh (§B). **FACT.**
- Clusters are **mask-only** geometry (0 texcoords, no material); the portal *content*
  is a separate textured mesh via `Custom/PortalContentUnlit`. Segmentation can run on
  a colourless mesh (§C). **FACT.**
- The depth-capture (Stage 1 `KeyframeCaptureManager`) + Python reconstruction (Stage 2)
  are **isolated**: KeyframeCapture writes files nothing in-app reads; deleting the
  whole pipeline would not break the app or an MRUK demo (§E). **FACT** (one caveat).
- For a 2-day demo: **on-device MRUK GLOBAL_MESH → server `POST /segment` → load
  clusters through ObjectPicker's existing preload path** is the minimal path (§F/§G).

---

## SECTION A — Every room-mesh source in the running app

| # | Source | Loader (file:method) | Layer / material | Mask or Content | Active in current.unity | Evidence |
|---|---|---|---|---|---|---|
| A-1 | **External server scene mesh** (`http://butcluster.ddns.net:8000`, per-scene binding) | `MeshDownloadManager.LoadSceneMeshAsync` → `LoadObjMesh` → GameManager.`AttachServerSceneMesh` names it `ServerSceneMesh` | URP/Lit textured material (`CreateRuntimeMeshMaterial`); layer set to `sceneMeshDefaultLayer=10`; colliders disabled after attach | Portal **CONTENT** source (materials swapped to `Custom/PortalContentUnlit` via `CacheSceneMeshMaterials`/`CreatePortalMaterialSet`) | Yes (loads when a scene opens) | MeshDownloadManager.cs:20,33,569,637; GameManager.cs:849-866,961-984 — **FACT** |
| A-2 | **Cluster meshes** (`StreamingAssets/clusters/clusterN.obj`) | `ObjectPicker.PreloadClustersCoroutine` → `MeshDownloadManager.LoadMeshFromServer("cluster_preload_{id}", file://…)` | Parented under `ServerSceneMesh`; renderer OFF; dedicated child `ClusterPick_{id}` collider on layer 11; on Add → `stencilMaskMaterial` on layer 9 | Portal **MASK** (stencil writer) | Yes (preloads after scene mesh) | ObjectPicker.cs:152-218,460-489 — **FACT** |
| A-3 | **MRUK EffectMesh** (headset scene) | MRUK `EffectMesh` prefab (guid `37c47f99…`), `LayerApplier.GetRoomObjectAndApplyLayer` can relabel MRUKRoom → `portalContent` | `Labels: 7` = FLOOR\|CEILING\|WALL_FACE; `MeshMaterial` overridden (guid `e74ecbb4…`) | Plane visuals; **not** wired as portal mask/content in code today | Yes (prefab instance `m_IsActive:1`) | current.unity:44544-44551,44592-44604; LayerApplier.cs:6-12; EffectMesh SceneLabels decode below — **FACT** |
| A-4 | **Action-object meshes** (ARCOR2 per-object models) | `GameManager.SceneOpened` → `MeshImporter.LoadModel(ao…Mesh, id)` | Parented to `Origin`; own materials | Neither (robot/action-object props) | Yes | GameManager.cs:174-177,222-225,330-353 — **FACT** |

**A1 — origin of the "server scene mesh".**
Host/port: `meshServerBaseUrl = "http://butcluster.ddns.net:8000"` (default; SerializeField).
Keyed by `sceneId = CommunicationManager.Arcor2Session.NavigationId`. Bound via
`GET /v1/scenes/{sceneId}/binding` → `download_url` → cached OBJ.
MeshDownloadManager.cs:20,178-188,64,392-473 — **FACT**.

**A2 — where does that mesh physically ORIGINATE (external camera vs headset)?**
The server serves a pre-built OBJ per scene; the Unity code only downloads it. Whether
the server built it from an external camera rig or a headset scan is **not determined by
any code in this repo** — the URL and binding contract are all the client sees.
**UNKNOWN.** Resolve by: inspecting the mesh server (`butcluster.ddns.net:8000`) or the
provenance of `mesh-3hz-4.obj` on the server side. (Strong circumstantial evidence it is
an external/offline reconstruction: it is Meshlab-generated and textured — see §B.)

---

## SECTION B — What the ORIGINAL segmentation ran on (headset vs external camera)

| Claim | Evidence | Tag |
|---|---|---|
| B1. `mesh-3hz-4.obj` exists with accompanying `.mtl` + `.jpg` | `ls` shows `mesh-3hz-4.obj` (61 MB), `mesh-3hz-4.mtl` (292 B), `mesh-3hz-4.jpg` (9.96 MB) | **FACT** |
| B1. The OBJ references a texture (is TEXTURED) | OBJ header: `mtllib ./mesh-3hz-4.mtl`; `vt` count = 407 228; `.mtl` has `map_Kd mesh-3hz-4.jpg` | **FACT** |
| B1. Generated by Meshlab, 270 972 verts / 519 146 faces | OBJ header comment `# OBJ File Generated by Meshlab`, `# Vertices: 270972`, `# Faces: 519146` | **FACT** |
| B2. This is the EXTERNAL/server (fine, textured) mesh — NOT the headset MRUK (coarse, colourless) mesh | (a) It is textured with a 9.96 MB photo-texture; the MRUK GLOBAL_MESH is an untextured triangle soup with no UVs/photo. (b) It is Meshlab-authored offline, not a runtime MRUK artifact. (c) The segmenter loads exactly this file: `export_clusters_normals.py:133 o3d.io.read_triangle_mesh("mesh-3hz-4.obj")` | **FACT** for "textured/offline/Meshlab"; **PARTIAL** for "== the server-served mesh" (name matches `MeshDownloadManager` conventions but not byte-verified against the server download) |
| B3. Clusters define the portal MASK shape ONLY; the cluster's own texture is never used | `cluster0.obj`: `vt` count = 0, `mtllib` count = 0 (pure geometry). ObjectPicker assigns `stencilMaskMaterial`/debug colour, never a texture (ObjectPicker.cs:472-483) | **FACT** |

**B2 definitive statement:** The original segmentation ran on the **external/offline
textured reconstruction** `mesh-3hz-4.obj` (Meshlab, photo-textured). It is not the
headset's MRUK scene mesh. The only residual UNKNOWN is byte-identity between this local
copy and what the server serves at runtime (naming + loader conventions strongly imply
they are the same asset family; resolve by diffing a server download against this file).

---

## SECTION C — Mask vs Content vs Texture (how colour works)

| Claim | Evidence | Tag |
|---|---|---|
| C1. Portal CONTENT (inside stencil==6) is rendered by `Custom/PortalContentUnlit` on the **server scene mesh** | `GameManager.CreatePortalMaterialSet` builds materials with `Shader.Find("Custom/PortalContentUnlit")` and swaps them onto the `ServerSceneMesh` renderers (`ApplySceneMeshRenderMode`) | **FACT** |
| C1. PortalContentUnlit samples a colour texture `_BaseMap` copied from the source material | Shader: `_BaseMap` property + `SAMPLE_TEXTURE2D(_BaseMap,…)`; `CopyPortalMaterialProperties` copies `_BaseMap`/`_MainTex` + `_BaseColor` | **FACT** |
| C1. What is visible inside a portal today = the textured **server room mesh** rendered only where the mask stencil wrote 6 | StencilMask writes `Ref 6 … Pass Replace` (Geometry-1, ZWrite On, ColorMask 0 → invisible); PortalContentUnlit `Stencil Ref 6 Comp Equal` (Geometry+500) | **FACT** |
| C2. CONTENT is decoupled from MASK (segmentation can run on a colourless mesh while content comes from a different textured mesh) | Mask = cluster geometry on layer 9 (no texture). Content = server mesh with PortalContentUnlit. They are different GameObjects/renderers linked only by the shared stencil value 6 | **FACT** |
| C3. Does the current CONTENT geometrically match a freshly-scanned room? | The content mesh is the **pre-baked server scan** (`mesh-3hz-4` family). A newly-scanned MRUK room is a DIFFERENT geometry/pose. Unless the content mesh is the same room, portals cut in live MRUK clusters would reveal a **mismatched** pre-baked interior | **PARTIAL** (mismatch is logically certain if the live room ≠ the pre-baked scan; not runtime-verified) |

**C3 — minimal content for a live demo (recommendation).** To avoid needing a
texture-matched background, show inside the live clusters one of:
- a **virtual scene / skybox** placed behind the mask (a single quad or a small virtual
  environment on the portalContent layer), or
- **passthrough-through** (SelectivePassthroughStencil already renders passthrough where
  stencil != 6; an "inverse" portal that shows passthrough inside the mask is a
  content-free option), or
- a solid/emissive colour fill.

The existing `SelectivePassthroughStencil.shader` (Ref 6, Comp NotEqual) and
`PortalContentUnlit` (Ref 6, Comp Equal) mean the plumbing already supports "content
inside, passthrough outside." A demo can ship with a **virtual environment** as content
so the clusters look like windows into a virtual world — decoupled from the real room's
texture. — **FACT** (shaders exist) / recommendation.

---

## SECTION D — Does the headset already scan a mesh (MRUK), and can we get it?

| Claim | Evidence | Tag |
|---|---|---|
| D1. MRUK is present and configured to run | MRUK MonoBehaviour (guid `de27d7f2…`) `m_Enabled: 1` in scene; `SceneSettings.DataSource: 2` = `DeviceWithPrefabFallback`; `LoadSceneOnStartup: 1`; `requestScenePermissionOnStartup` present | **FACT** |
| D1. Scene permission requested at runtime | `MozartSpatialBridge.WaitForScenePermissionAsync` polls `com.oculus.permission.USE_SCENE`; `EnsureDeviceSceneLoadedAsync` calls `MRUK.Instance.LoadSceneFromDevice(requestSceneCaptureIfNoDataFound:true,…)` | **FACT** |
| D1. An MRUK prefab is present AND active | MRUK settings block live in scene; `EffectMesh` prefab instance `m_IsActive: 1`; `MozartSpatialBridge` registers `MRUK.Instance` events | **FACT** |
| D2. The MRUK global/scene mesh is instantiated today | `EffectMesh.Labels = 7` (FLOOR\|CEILING\|WALL_FACE). `GLOBAL_MESH = 1<<14`, absent from the mask, so `EffectMesh.CreateGlobalMeshObject` is **not** triggered by this config. Only plane anchors get meshes | **FACT** (GLOBAL_MESH not enabled) |
| D2. Who calls `LayerApplier.GetRoomObjectAndApplyLayer`, and on what | The method finds the first `MRUKRoom` and relabels its GameObject tree to `portalContent`. No in-repo caller found (invoked from scene UnityEvent or unused) | **PARTIAL** — caller not located in code; **UNKNOWN** whether wired in a UnityEvent. Resolve: search scene for `GetRoomObjectAndApplyLayer` persistent calls |
| D3. The MRUK room mesh can be obtained as a Unity Mesh at runtime | `EffectMesh.CreateGlobalMeshObject` adds a `MeshFilter` and sets `meshCollider.sharedMesh = effectMeshObject.mesh` → the GLOBAL_MESH triangle mesh is a standard Unity `Mesh` (vertices+triangles) accessible via `MeshFilter.sharedMesh` | **FACT** (code path exists; requires GLOBAL_MESH enabled) |
| D4. Headset can produce a room mesh WITHOUT the server AND WITHOUT the Depth API | **YES.** MRUK loads scene geometry from the device (`LoadSceneFromDevice`) independent of `butcluster` and independent of `EnvironmentDepthManager`. Enabling `GLOBAL_MESH` on EffectMesh yields a dense Unity Mesh | **FACT** (with the GLOBAL_MESH-enable caveat) |

**Decoding `Labels: 7`:** `MRUKAnchor.SceneLabels` uses `1 << OVRSemanticLabels.Classification.X`.
`Floor=0→1`, `Ceiling=1→2`, `WallFace=2→4` ⇒ `1|2|4 = 7`. `SceneMesh(GLOBAL_MESH)=14→16384`,
not set. (MRUKAnchor.cs:59-73; OVRSemanticLabels.cs:45-59) — **FACT**.

---

## SECTION E — Depth capture / reconstruction / where data goes

| Claim | Evidence | Tag |
|---|---|---|
| E1. No Meta Environment-Depth code exists in the MAIN app scripts (only Debug capture work) | `grep EnvironmentDepth Assets/Scripts` returns only `Debug/EnvDepthProbe.cs` + `Debug/KeyframeCaptureManager.cs`. No manager/mesh/portal script uses the Depth API | **FACT** |
| E2. Stage-1 capture (`KeyframeCaptureManager`) is wired into current.unity | GameObject `KeyframeCapture` `m_IsActive:1`, component `m_Enabled:1`, guid `82a7cc03…`; fields serialized (`_scanToggle` fileID 971085031, `captureDepth:1`) | **FACT** |
| E2. Stage-2 reconstruction is in-app? | **No.** Reconstruction is offline Python (`captures/reconstruct_clean.py`, `unproject_clean.py`) — not referenced by any C# | **FACT** |
| E3. On-device storage location | `Application.persistentDataPath/captures/<sessionId>/`. With `applicationIdentifier.Android: cz.fitvut.fat`, this resolves to `/storage/emulated/0/Android/data/cz.fitvut.fat/files/captures/<sessionId>/` (matches the logged path and the `adb pull` path used) | **FACT** |
| E3. Files written per session | `frame_XXXX.json` (pose+intrinsics; Open3D `PinholeCameraParameters` schema + `unity_position/rotation`, `head_view`, `depth_proj/view`, `depth_fov_tangents`, `depth_near_far`), `frame_XXXX.depth.png` (**16-bit** grayscale, mm), `manifest.json` (frame list, width/height 512, `depth_scale=0.001`) | **FACT** (KeyframeCaptureManager.cs:374-465,610-688,811-837) |
| E3. Any RGB/screen captures written? | **No RGB.** KeyframeCaptureManager writes only depth PNG + JSON. (Separate `ScreenCamera_*.json`/`ScreenCapture_*.png` files exist under `clusters/` but are unrelated debug artifacts, not written by this script) | **FACT** |
| E3. What CONSUMES the captures? | Nothing in-app. `grep captures Assets/Scripts` finds no reader; only offline Python consumes them | **FACT** |
| E4. Can Stage 1 + Stage 2 be DELETED without breaking the app or an MRUK demo? | **YES.** `KeyframeCaptureManager` + `EnvDepthProbe` are self-contained Debug components; nothing references their output. Removing them leaves ObjectPicker/GameManager/MRUK untouched | **FACT** — one caveat below |

**E4 caveat / discrepancy (FACT):** The committed scene serializes `capturing: 1` on
`KeyframeCaptureManager` (current.unity:613), which **overrides** the C# field
initializer `capturing = false`. So *as committed*, capture auto-starts on launch
despite the source default. If Stage 1 is kept, set the scene value to 0 (or disable the
`KeyframeCapture` GameObject) so scanning is button-driven only. If deleted, remove the
GameObject and its `_scanToggle` wiring.

---

## SECTION F — Runtime segmentation for the live demo

| Claim | Evidence | Tag |
|---|---|---|
| F1. Existing network endpoints | **Mesh server** `http://butcluster.ddns.net:8000`: `GET /v1/meshes`, `GET/POST /v1/scenes/{id}/binding`, `/bind-mesh`, `/rebuild-from-boxes` (POST), `/mesh-transform` (PUT), plus `download_url` GET (MeshDownloadManager.cs:94,107,156,180,230). **Legacy** `http://butcluster.ddns.net:6789/mesh/{name}` (LoadMeshFromServer default). **ARCOR2 WebSocket** `ws://butcluster.ddns.net:6789` (CommunicationManager.cs:18,56) | **FACT** |
| F2. Existing segment / mesh-processing endpoint? | **None.** No `/segment` route and no server-side `export_clusters*.py` invocation in the client. `rebuild-from-boxes` is a box-cut op, not segmentation | **FACT** |
| F3. Client change to POST an MRUK mesh + load returned clusters | ObjectPicker preloads clusters from **local** StreamingAssets via `file://` (ObjectPicker.cs:172-175,224-226). To load from a server response, the two reads that must change are `LoadManifest` (the `clusters.json` URL) and the per-index path in `PreloadClustersCoroutine` (the `clusterN.obj` URL) — point both at the server's returned URLs instead of `Application.streamingAssetsPath` | **FACT** (exact lines identified) |
| F4. On-device in-app segmentation feasible? | Possible but heavy: a C# RANSAC+DBSCAN on ~100k points is CPU-bound and would hitch the Quest; MRUK `DestructibleGlobalMesh`/Spawner chunks the global mesh **geometrically** (grid pieces), not into semantic objects — loses object-aware clustering | **PARTIAL** (perf not measured on-device; quality trade-off is structural) |

**F2 — where a new `POST /segment` slots in (design).** Client uploads a mesh OBJ (body
or multipart); server runs `export_clusters_normals.py` on it; responds with
`clusters.json` + a base URL for `clusterN.obj`. This mirrors the existing
`rebuild-from-boxes` request/response shape (POST JSON in, `download_url`-style out), so
it fits the current server contract style. — **PARTIAL** (server is out of repo scope).

---

## SECTION G — Integration design (map only)

**G1. Exact runtime hook for the MRUK global mesh.**
Enable `GLOBAL_MESH` on the scene `EffectMesh` (`Labels` includes `1<<14`). MRUK's
`EffectMesh.CreateGlobalMeshObject` then creates a GameObject (suffix from the anchor
name) with a `MeshFilter` whose `sharedMesh` is the room mesh, and a `MeshCollider`.
Access it at runtime via that `MeshFilter.sharedMesh` (or `MRUKRoom` → the GLOBAL_MESH
anchor). (EffectMesh.cs:690-692,1042-1058,781,817) — **FACT**.

**G2. Server-call vs on-device segmentation for a 2-day demo — recommendation.**
Use the **server `POST /segment`** path. Rationale: it reuses the proven
`export_clusters_normals.py` (object-aware clusters, already produces the exact
`clusterN.obj` + `clusters.json` contract ObjectPicker consumes), avoids Quest CPU
hitches, and requires no new on-device geometry code. On-device C# RANSAC/DBSCAN is the
fallback only if a server round-trip is unacceptable. — recommendation (grounded in
F2/F4).

**G3. Where clusters re-enter ObjectPicker + the coordinate-space requirement.**
Re-entry point: `PreloadClustersCoroutine` (ObjectPicker.cs:152) via `LoadManifest`
(:222) and the per-index `LoadMeshFromServer` (:175). Coordinate space: each cluster is
parented to `_sceneMeshTransform` (the `ServerSceneMesh`) at **local identity**
(`localPosition=0, localRotation=identity, localScale=1`, :181-183). Picking is by
`MeshCollider` on the *rendered* cluster child, so **match-space == render-space by
construction** — the Bug F (match ≠ render) class of error is avoided **as long as
clusters are parented under the same transform the mesh renders in**. — **FACT**.

> ⚠️ **Risk for the MRUK variant:** today clusters parent under `ServerSceneMesh`. If the
> demo's clusters come from the **MRUK** mesh, they must parent under the **same MRUK
> mesh transform** (GLOBAL_MESH object) — NOT `ServerSceneMesh` — and be produced in that
> mesh's coordinate space. Also note `MeshDownloadManager.flipObjXAxisForUnity = true`
> mirrors imported OBJ on X (MeshDownloadManager.cs:812-820); the MRUK mesh is already in
> Unity space, so if MRUK clusters are re-imported through the same OBJ path they would be
> X-flipped and land mirrored. Either export the segmentation result in the OBJ basis the
> loader expects, or bypass the X-flip for MRUK-sourced clusters. **This is the single
> biggest coordinate risk.** — **FACT** (flip exists) / **PARTIAL** (interaction not runtime-tested).

**G4. Content inside live clusters (from C3).**
Ship a **virtual environment / skybox** on the portalContent layer as the content, so
portals are windows into a virtual world independent of the real room's texture. Change
needed: place a content GameObject behind the masks and ensure its material uses
`Custom/PortalContentUnlit` (stencil Ref 6 Comp Equal). No change to the mask path. —
recommendation.

---

## SECTION H — Minimal demo build plan (smallest ordered path)

1. **Enable the headset global mesh.** In `current.unity`, set the `EffectMesh.Labels`
   to include `GLOBAL_MESH` (`1<<14`). Confirm a MeshFilter GLOBAL_MESH object appears at
   runtime. *(files: current.unity EffectMesh instance; verify via EffectMesh.cs:1042.)*
2. **Grab the MRUK mesh at runtime.** Add a small accessor that finds the GLOBAL_MESH
   `MeshFilter.sharedMesh` (via `MRUKRoom`/EffectMesh) and serializes it to OBJ in memory.
   *(new tiny helper; hook: MozartSpatialBridge room-loaded event.)*
3. **Stand up `POST /segment`** on the mesh server running `export_clusters_normals.py`;
   returns `clusters.json` + `clusterN.obj` URLs. *(server-side, out of repo.)*
4. **Point ObjectPicker at the server clusters.** Change the two URLs in
   `LoadManifest` (ObjectPicker.cs:224) and `PreloadClustersCoroutine` (ObjectPicker.cs:172-175)
   from `Application.streamingAssetsPath` to the server response, and **parent clusters
   under the MRUK global-mesh transform** (ObjectPicker.cs:180). *(files: ObjectPicker.cs.)*
5. **Handle the X-flip.** Ensure MRUK-sourced clusters are NOT double-mirrored by the OBJ
   loader (`flipObjXAxisForUnity`) — export in the loader's basis or bypass the flip for
   this source. *(files: MeshDownloadManager.cs:812-820; ObjectPicker preload.)*
6. **Pick content.** Add a virtual-environment content mesh (portalContent layer,
   PortalContentUnlit) so portals show something coherent (§G4).
7. **Button flow.** Wire "Scan Room" to (re)load MRUK from device → POST /segment →
   preload clusters; keep Add/Remove Portal toggles unchanged (they already work with any
   cluster set — ObjectPicker.cs:416-513).

**Single biggest risk:** coordinate-space / X-flip mismatch between the MRUK mesh space,
the segmentation output basis, and the OBJ loader's `flipObjXAxisForUnity` mirror (§G3).
If clusters parent under the wrong transform or get double-flipped, picks land on empty
space or portals render mirrored — the exact "match-space ≠ render-space" failure class
the current design otherwise avoids.

---

## Recommended demo pipeline

```
[Quest, one session]
  press "Scan Room"
    → MRUK.LoadSceneFromDevice (already wired) + EffectMesh GLOBAL_MESH enabled
    → obtain GLOBAL_MESH MeshFilter.sharedMesh  (headset dense mesh, no server, no Depth API)
    → serialize mesh → POST /segment (server runs export_clusters_normals.py)
    → receive clusters.json + clusterN.obj URLs
    → ObjectPicker preloads clusters, parented under the MRUK global-mesh transform
  Add/Remove Portal toggles (unchanged) → stencil-mask portals in cluster silhouettes
  content inside = virtual environment (portalContent layer)  [avoids texture-match need]
```
Delete/disable Stage-1 capture + Stage-2 reconstruction (they are unused by this path;
§E4). Keep the external server mesh path only if you still want the pre-baked demo.

---

## Remaining UNKNOWNs (each with the resolving test)

1. **Provenance of the server scene mesh / `mesh-3hz-4.obj` (external camera vs headset).**
   Resolve: inspect the mesh server at `butcluster.ddns.net:8000` (or ask its owner) and
   diff a live `download_url` OBJ against the local `mesh-3hz-4.obj`. (A2, B2)
2. **Is `LayerApplier.GetRoomObjectAndApplyLayer` actually invoked?** Resolve: search
   `current.unity` for a persistent UnityEvent call to it, or a code caller; if none, it is
   dead. (D2)
3. **On-device perf of any in-app segmentation.** Resolve: prototype a C# RANSAC/DBSCAN on
   a ~100k-point MRUK mesh and profile on Quest 3. (F4)
4. **Exact server `/segment` contract** (payload format, async job vs sync). Resolve:
   define + test against the mesh server; out of repo scope. (F2)
5. **MRUK mesh ↔ OBJ-loader X-flip interaction.** Resolve: run the demo path once and
   verify a pick lands on the aimed object (not mirrored). (G3, H-5)
```
