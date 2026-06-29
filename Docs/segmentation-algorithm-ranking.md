# Segmentation Algorithms — Ranked Best-to-Worst for This Repo

> Scope: choosing the offline segmenter that turns `mesh-3hz-4.obj` into
> `clusterN.obj` masks. Ranked by **fit to this project's constraints**, not by
> benchmark score — those two orderings are nearly *inverse*, which is the point.

## The constraints that drive the ranking
- **Offline** preprocessing (no runtime/latency pressure).
- **No semantic labels needed** — instance separation only.
- **A merge is catastrophic:** one cluster = one selectable object = one portal,
  unrecoverable in-app. Over-splitting is tolerable (multi-pick / merge post-step).
- **Environment:** Windows, **pip-only**, one dev, no Conda/CI/Docker.
- **Output contract is fixed:** `clusterN.obj` + `clusters.json`; Unity untouched.
- **Objects are lab/industrial**, not ScanNet residential furniture.

A high ScanNet mAP measures *semantic instance segmentation of common furniture*.
Our success metric is: *did two **touching** objects end up in separate clusters,
with no merges, on a lab scan?* mAP does not predict that.

---

## Tier 1 — Try these first

### 1. Normals-augmented DBSCAN (RANSAC pre-strip + position+normal clustering)
*Report calls this "DBSCAN-like (dist+norm)" and dismisses it; best fit for us.*
- **Pros:** Reuses the current pipeline; **zero new dependencies** (Open3D+numpy
  already present); deterministic; runs in seconds; the **normal discontinuity is
  the right signal to split touching objects** (the only fatal failure mode);
  fully under our control to tune.
- **Cons:** Over-splits thin/curved objects (acceptable — recoverable); needs
  per-scene tuning of `eps` + normal weight; ragged edges on non-watertight scans
  need light cleanup.

### 2. LCCP done *correctly* (real PCL via pclpy — not the hand-rolled port)
*Report's "best pure geometric method." Right idea, wrong cost assumption.*
- **Pros:** Strongest classical no-merge guarantee (splits on concavity by
  design); fast; deterministic; convexity is exactly right for touching rigid
  objects.
- **Cons:** **Our two pure-Open3D ports both collapsed to 1 region.** A working
  LCCP needs PCL → Conda → **not installable on this box**. The report's "~0.47 s,
  almost never merges" are PCL/C++ numbers we can't reach without changing the
  environment. Strong *if* the env problem were solved; it isn't.

---

## Tier 2 — Real fallbacks, only if Tier 1 measurably fails

### 3. SAM-on-rendered-views hybrid (SGS-3D style) — the report's TOP pick
- **Pros:** Most robust on **novel/unseen** objects (SAM generalizes past a fixed
  class list — matters for lab/industrial gear); uses texture/color cues geometry
  can't see; "split-then-grow" actively avoids merges; class-agnostic (matches our
  no-labels need). If a learned step is ever needed, **this is the right one**
  because it's domain-robust.
- **Cons:** **Weeks** of install + glue before the first correct cluster (PyTorch
  + CUDA + SAM + multi-view rendering + camera-pose + mask-lifting) on a
  Windows/pip-only/one-dev repo; 10–30 s/scene; viewpoint/version nondeterminism;
  optimizes for semantic quality we discard. Over-engineered unless geometry
  provably can't separate the objects.

### 4. Mask3D / ISBNet / SoftGroup (pretrained 3D instance nets)
- **Pros:** Highest **ScanNet** mAP; clean *whole* objects (few splits) on
  furniture-like categories; sub-second inference *on a CUDA GPU*; output converts
  to clusters.
- **Cons:** Trained on residential/office furniture → **weakest exactly on our
  lab/industrial objects**; fixed class vocabulary → unknown objects missed or
  **merged** (catastrophic, worse than recoverable over-split); needs PyTorch +
  CUDA + sparse-conv libs (MinkowskiEngine/spconv), brutal on Windows;
  nondeterministic; no retraining data available. High *average* accuracy on the
  wrong distribution doesn't protect our specific touching objects.

---

## Tier 3 — Not worth trying for this case

### 5. Plain DBSCAN / Euclidean clustering — *the current method*
- **Pros:** Dead simple; already shipping; deterministic; fast; rarely splits.
- **Cons:** **Merges touching objects** (cup-on-table, chair-on-floor) — the exact
  catastrophic failure we're eliminating. No `eps` fixes it; distance is the wrong
  signal. The baseline we're moving away from.

### 6. Region growing (normal smoothness) / RANSAC primitives alone
- **Pros:** Fast; excellent at planes (the floor/wall pre-strip we already use);
  simple, in Open3D.
- **Cons:** Both over-splits at edges/noise *and* merges across smooth shared
  interfaces — erratic, hard to control for whole instances. Good as a *pre-step*,
  useless as the instance segmenter.

### 7. Graph / spectral segmentation
- **Pros:** Theoretically tunable scale.
- **Cons:** No object-level cue → no merge guarantee; spectral too slow for ~500k
  faces; research-grade code only; hard to tune. No advantage over Tier 1.

### 8. Supervoxel clustering (VCCS) alone
- **Pros:** The substrate LCCP builds on.
- **Cons:** Massively over-segments with **no objectness**; incomplete without the
  LCCP merge stage. Not a standalone option.

### 9. OpenMask3D / Point-SAM / SAMPro3D (open-vocab / promptable)
- **Pros:** Open-vocabulary; class-agnostic; strong zero-shot.
- **Cons:** Built for **semantic queries we don't have**; heaviest dependency
  stack; least turnkey (research prototypes, multi-component); Point-SAM/SAMPro3D
  need prompts or training infra. All cost, no requirement met that SGS-3D doesn't
  meet more cheaply.

---

## The shape of the ranking
This ordering is roughly the **inverse** of the report's benchmark ranking. That's
expected: the report ranks by mAP on someone else's furniture dataset; we need *no
merges on touching lab objects, with a footprint one person can maintain on
Windows*. By that metric the cheap geometric method using the convexity signal
wins, and the heavy learned pipelines are fallbacks reached for **only after
measuring** that geometry failed — not before.

**Recommended path:** build Tier 1.1, run on the real scene, **count merges**.
Escalate to Tier 2.3 (SAM hybrid) only if geometry provably can't separate
specific objects.

---

## Implemented: Normals-augmented DBSCAN (Tier 1.1)

> Script: `Assets/StreamingAssets/clusters/export_clusters_normals.py`. This is
> Tier 1.1 above, now built and run. The two LCCP routes (CloudCompare and the
> pure-Open3D ports) are exhausted: CloudCompare 2.13.2's PCL wrapper exposes no
> segmentation, and both Open3D ports collapsed to one region. pclpy-via-conda is
> rejected (likely won't install, and only re-tests the failed convexity approach
> at the cost of a permanent dual-toolchain). So this is the live path.

### What changed vs. the shipping `export_clusters.py`

Exactly one thing: the **signal DBSCAN clusters on**. Everything else — the RANSAC
plane pre-strip, and the mesh-crop → QEM-simplify → `clusterN.obj` →
`centres.txt` / `clusters.json` backend — is byte-for-byte identical, so the Unity
runtime sees the same output contract and nothing in Unity changes.

| | Current (plain DBSCAN) | Normals-augmented |
|---|---|---|
| Point feature | position `(x, y, z)` | position **+ surface normal** `(x, y, z, w·nx, w·ny, w·nz)` |
| "Same cluster?" | within `eps` in 3-D space | within `eps` in space **and** normal direction |
| Touching objects | continuous geometry → **merge** | normal flips at the contact crease → **split** |
| New dependencies | — | **none** (DBSCAN runs on an Open3D KD-tree) |

### How it works

1. **Sample + normals.** The mesh is sampled to a point cloud; per-point surface
   normals are estimated and oriented consistently.
2. **Strip planes (RANSAC).** Six dominant planes (floor, walls, ceiling) are
   removed — unchanged from the original. These are structure, not movable objects.
3. **Build a 6-D feature per point:** `[x, y, z, w·nx, w·ny, w·nz]`. The position
   half says *how close* two points are in space; the normal half says *how
   aligned their surfaces are*. `w` (`NORMAL_WEIGHT`) sets how much the normal
   counts relative to a metre of distance.
4. **DBSCAN on that 6-D feature.** DBSCAN grows a cluster by repeatedly absorbing
   neighbours within `eps` — but now "neighbour" means close in space **and**
   similar in normal. Where two objects touch, the surface has a **normal
   discontinuity**: the normal direction jumps at the seam. That jump pushes the
   two sides far apart in the normal half of the feature, even though they're
   touching in space — so the cluster boundary lands exactly on the seam and the
   objects separate. (Implementation note: the KD-tree indexes only the position
   half as a cheap prefilter — any point within `eps` in 6-D is necessarily within
   `eps` in position — then each candidate is refined against the full 6-D
   distance. Same result as `sklearn.DBSCAN`, no sklearn dependency.)
5. **Backend (unchanged).** Each cluster's points are cropped from the mesh,
   simplified, and written as `clusterN.obj` with the manifest.

### Why this works where LCCP failed

LCCP makes a **hard per-edge convex/concave decision** and is unforgiving: one
wrong verdict on a seam fuses two whole objects (union-find / flood-fill is
transitive), and on a noisy non-watertight scan with imperfectly oriented normals,
some verdicts are always wrong → collapse to one region. Normals-DBSCAN makes **no
binary per-edge decision** — it's a soft density threshold over many points, so a
handful of noisy normals are outvoted rather than catastrophic. And its failure
mode is the **safe** one: when it's wrong it **over-splits** (recoverable via
multi-pick), never merges (unrecoverable in-app).

### Tuning knobs (top of the script)

- **`NORMAL_WEIGHT`** — the important one. Higher → normals matter more → splits
  harder at creases (more over-split, the safe direction). Lower toward 0 →
  behaves like the original position-only DBSCAN (more merges). Currently `0.25`.
- **`EPS`** — spatial reach (≈ `0.15`).
- **`MIN_POINTS`** — speck rejection.
- **`BBOX_PAD`** — lower to `1.0` if crop boxes fill gaps between e.g. chair legs.

### Status / how to validate

First run produced **33 clusters** (vs. ~14 from plain DBSCAN) — the expected,
safe over-split direction. Validate by eye:
`py -3.11 visualize_clusters.py`, then **count merges** (any single cluster
spanning two physical objects is the only fatal error; raise `NORMAL_WEIGHT` and
re-run if found). **Known limit:** objects that touch with *no* normal change
(flat book flush on a flat shelf, coplanar surfaces) won't separate — that is the
documented trigger to escalate to the Tier 2.3 SAM hybrid.
