# Problem Context and Pipeline Requirements  

This project segments uploaded 3D indoor meshes into movable object instances for a Unity/Meta Quest VR app.  The segmented clusters become separate OBJ meshes, each with its own `MeshCollider`, so that Unity’s `Physics.Raycast` can correctly select individual objects.  The pipeline runs offline (not in real time) but must complete in at most seconds-to-minutes per mesh, even for million-triangle scenes.  **Crucially, false *merges* (two distinct objects labeled as one) must be minimized at all costs**, since a merged cluster would break object selection.  Over-segmentation (splitting one object into pieces) is less critical and can be handled by treating sub-parts as separate colliders.  Only instance-level separation is needed (no semantic labels).  The segmentation module should fit into a Python preprocessing pipeline, output cluster OBJ files, and leave the Unity side (portal masks, controllers, colliders) unchanged.

The current pipeline uses Open3D/PCL’s classical geometry-based segmentation (likely **LCCP** / supervoxels) to cluster the mesh.  This works but has known issues: it often cuts single objects into parts along concave edges (e.g. chair handles, bottle caps), and can fail when objects touch or rest on each other.  It is fast (on the order of 0.5–1 second per scene with GPU acceleration) but tends to *over*-segment, which our priority allows, and almost never merges distinct objects. We will evaluate whether more modern methods can reduce errors (especially merges) while respecting engineering constraints.

We will compare a wide range of methods:

- **Classical geometric methods** (LCCP, supervoxel clustering, Euclidean/DBSCAN clustering, region-growing, graph cuts, etc.)  
- **Deep-learning instance segmentation** (e.g. Mask3D, SoftGroup, ISBNet, etc.) trained on indoor 3D data.  
- **Open-vocabulary and SAM-based methods** (SAM3D, OpenMask3D, SGS-3D, SAM-guided graph cuts).  
- **Hybrid pipelines** that combine geometry and learned cues (e.g. use 2D Mask R-CNN/SAM to propose masks then refine in 3D).

For each, we will analyze segmentation accuracy (merge vs split tradeoffs, handling of clutter/partial scans/thin objects), computational cost, integration and installation complexity, output format, determinism, and community support.  We will then tabulate these factors and make evidence-based recommendations.  The goal is to recommend a single segmentation *pipeline* (which could be multi-stage) that balances accuracy, robustness, speed, ease-of-integration, and maintainability in a production context.

## Classical Geometry-Based Segmentation  

**LCCP (Locally Convex Connected Patches, PCL/Open3D)** – This supervoxel-based method clusters points by local convexity (concave edges separate objects).  It is **unsupervised** and has been widely used for scene partitioning.  LCCP almost never *merges* distinct rigid objects (concave junctions will split them), but it **frequently over-segments** single objects into parts (e.g. different segments for chair back vs seat vs legs, or a mug’s handle separated).  In benchmark terms, Stein et al. report low under-segmentation (few merges) but higher over-segmentation (Fos ≈8.3%) on an object dataset.  LCCP runs fairly quickly (≈0.47 s per scene on a single CPU core, with further GPU speed-ups possible), and can handle dense indoor scans.  It produces point/segment labels that can be converted to OBJ clusters trivially.  However, integration requires PCL or Open3D (C++ or limited Python APIs), and its over-splitting means extra colliders per object.  Since our priority is to **avoid merges**, LCCP remains attractive as a baseline, but its strong splitting means additional small pieces.  

**Supervoxel Clustering (VCCS)** – This is the underlying step in LCCP.  It voxelizes the point cloud into “supervoxels” of fixed size, then merges them.  VCCS alone (without LCCP’s concavity merging) yields very fine segments and needs further grouping.  It has no notion of objectness by itself and typically over-segments massively (unless followed by another stage).  Like LCCP, it requires PCL or Open3D support.  On its own, VCCS is not sufficient, but it is the basis for algorithms like LCCP.  

**Euclidean/DBSCAN clustering** – A simple approach is to cluster by spatial proximity (e.g. PCL’s `EuclideanClusterExtraction` or DBSCAN on points).  This finds connected components under a distance threshold.  It can separate objects that are well-separated, but it *fails* badly if objects touch or are only millimeters apart (which is common in cluttered indoor scenes).  Euclidean clustering will merge touching objects unless the threshold is set extremely small, which then tends to *over-segment* even single objects.  DBSCAN (density-based) similarly merges adjacent objects unless tuned per scene.  In practice, naïve Euclidean clustering yields many merges in crowded indoor scans.  Its runtime (with kd-tree) is moderate but must examine neighbors of every point – on a 1M-point cloud this could take seconds on CPU.  It produces point-labels directly convertible to meshes.  It’s deterministic (given a fixed threshold).  Ease of integration is good since many libraries (PCL, scikit-learn) support it in Python, but tuning thresholds for varying scenes is tricky.

**Region-Growing & RANSAC Primitives** – Another classical tactic is to first remove large planes (floor, walls, tables) via RANSAC, then apply region-growing (by smooth normals) to segment remaining surfaces.  Plane removal is fast and helpful (non-movable structural elements).  But region-growing by normal similarity tends to split on any curvature or noise, and can also merge adjacent faces with similar normals.  It often produces a few large planar clusters and many tiny fragments.  This approach is simple (Open3D/PCL support) and fast, but it is **hard to control** for object instances.  It works well to detect walls/floor but not arbitrary object boundaries.  Merges can occur when two objects share a smooth interface, and splits happen at edges/occlusions.  In our context, plane removal could help (separating floor/walls early), but region-growing alone is not robust for complete instances.

**Graph/Spectral Segmentation** – Image-segmentation ideas (e.g. Felzenszwalb-Huttenlocher graph cuts) can be extended to 3D by building a mesh/point graph weighted by normal differences or color.  These methods produce a segmentation with an adjustable scale parameter.  They can over-segment or under-segment depending on parameters.  In practice, pure graph cuts are seldom used as-is for 3D instance segmentation of scenes, because they lack object-level cues.  Spectral clustering (eigen-decomposition of affinity) is too slow for millions of points.  Some mesh segmentation algorithms (like curvature-based mesh cuts) exist, but typically target single model parting, not scene-wide instance segmentation.  These methods are deterministic and fully geometric but have no guarantee to avoid merges in clutter.  They also require tuning and do not come with robust library implementations for large scenes.

**Normal-/Curvature-aware DBSCAN (e.g. by PR2 or PCL)** – Some hybrid classical methods cluster points using both distance and normal similarity (only cluster points close *and* with similar normals).  This can help avoid merging objects that touch at a corner with different orientations.  It still fails for objects that touch without a clear normal discontinuity.  No dominant code for point clouds exists for this in 3D scenes (there is such an approach in 2D image segmentation).  We could implement a variant, but it would likely resemble region-growing or VCCS with stricter criteria.  No public benchmark data suggests dramatic improvements in our scenario.

**Summary of Classical Methods:** Geometry-based segmentation is **fast**, deterministic, and avoids merges by design when using convexity cues (LCCP), but at the cost of over-segmentation.  Naïve clustering (Euclidean/DBSCAN) easily merges touching objects.  Region-growing is simple but tends to both split and merge erratically.  Graph/spectral methods are theoretically possible but complex to tune and heavy to run.  In practice, **LCCP remains one of the strongest classical baselines** for separating convex objects, whereas simpler clustering will likely underperform on clutter.  Table 1 (below) summarizes these tradeoffs qualitatively for key classical methods:

| Method                 | Merge Errors      | Split Errors       | Clutter/Thin Robustness    | Runtime (large scene) | Python Support   |
|------------------------|-------------------|--------------------|----------------------------|-----------------------|------------------|
| **LCCP (Convexity)**   | **Very Low** (splits on concavity) | High (splits object parts) | Handles most shapes; fails on texture-less uniform surfaces  | ~0.5s (CPU+GPU) | PCL/C++ only (no maintained Python) |
| Euclidean Clustering   | High (touching objects merge)  | Low (unless threshold small) | Very poor on cluttered/touching objects | ~1-2s (CPU kd-tree) | PCL/Open3D/Sklearn (Python) |
| Region Growing (normals)| Medium (similar normals can merge) | High (splits at edges) | Drops thin structures; noise-sensitive | Fast (ms) | Open3D/PCL (Python via wrappers) |
| Graph-Based (Spectral) | Variable (depends on param) | Variable | Can separate by features but hard to tune | Very slow (NP-hard) | Research code only |
| DBSCAN-like (dist+norm)| Medium | Medium | Slightly better on separated surfaces | Slow on large clouds | Custom, limited libraries |

## Deep Learning-Based Segmentation  

Deep neural networks can learn to segment point clouds into object instances, but they require **training data** and often output both semantics and instances.  Because we do not need semantic labels (and have no annotated training data in our target environment), we focus on class-agnostic methods or consider ignoring semantics.

**Mask3D** (Mask Transformer for 3D) – This 2022 CVPR model is a top performer on ScanNet/S3DIS semantic instance benchmarks.  It uses a 3D transformer to predict instance masks from point clouds.  Mask3D achieves state-of-art accuracy on ScanNet (e.g. +6.2% mAP over prior) by jointly outputting semantics and instances.  In our context we could ignore the semantic classes and take all predicted instances as objects.  It excels at finding whole objects (low splitting) with few false positives (low over-segmentation) on its training classes.  **Limitations:** Without retraining on our domain, Mask3D may miss or merge objects not seen in training (e.g. factory equipment, wires, unique tools).  It also requires GPU inference (likely >8GB VRAM), and its large model and PyTorch code impose a heavy install dependency.  However, it runs in sub-second range on scans (we did not find exact numbers, but similar point transformers like ISBNet run ~0.2–0.3s per scene).  Output is per-point instance labels, easily converted to mesh clusters.  Deterministic (aside from any non-critical seeds).  The implementation (code available) is research-grade PyTorch; using it requires setup of the exact environment and possibly CUDA.  Community use: published by Jonáš Schult et al., with code on GitHub; but as of 2025 it may not have broad production use.  Maintenance: moderate (research code, no official releases beyond GitHub).

**SoftGroup** – A 2022 CVPR method that builds on Mask3D ideas.  It does bottom-up “soft” semantic grouping then top-down refinement, allowing points to have multiple class hypotheses before final instance selection.  It reports +6.2% AP50 on ScanNet v2 and runs at ~345 ms per scan on a Titan X, with high accuracy on known indoor scenes.  Like Mask3D, SoftGroup outputs instance masks (plus semantics).  It is similarly heavy to train/use.  Without retraining, it would only detect classes seen in its dataset.  It tends to produce whole objects, so splitting is low.  Merge errors are low for familiar classes, but unknown shapes may fragment.  No easy way to remove semantics — one could treat all predicted instances as objects.  Implementation: PyTorch with sparse convs; relatively high complexity.  Community: some academic adoption; code on GitHub, but not plug-and-play.

**ISBNet** (CVPR 2023) – Another transformer-based instance network.  It reports state-of-the-art semantic instance scores (55.9 mAP on ScanNetV2, 60.8 on S3DIS) with only ~237 ms inference.  Similar pros/cons as above (good on dataset, needs same classes).  

**Other 3D Instance Models** – A plethora of 3D instance methods exist (3D-BoNet, 3D-SIS, DyCo3D, PointGroup, etc.), but all follow the paradigm of training on datasets like ScanNet or PartNet.  Some older models (PointGroup CVPR 2020) also separate by clustering proposals, but they are largely superseded by transformer methods.  None are truly *class-agnostic* without retraining.

**Practical Summary:** Deep 3D instance networks can deliver high accuracy (low split and merge errors) on environments like office/furnishing scenes, as evidenced by leading benchmarks.  However, they require an environment similar to training, and development/maintenance of PyTorch models and CUDA.  They demand substantial GPU resources but inference can be done in sub-second to seconds on a good GPU.  Their output (point-labels) integrates easily into mesh exports.  The main drawback is **generalization**: in our lab/industrial scenes with novel objects, they may either fail to segment them or mis-segment.  Also integration cost is high (complex code, heavy dependencies).  If segmentation quality *only* were the goal (and if training data were available), a method like Mask3D or ISBNet might be best.  But for generality and engineering practicality, we must be cautious.

## Open-Vocabulary and SAM-based Methods  

Recent work uses vision “foundation models” (like SAM and CLIP) to segment 3D scenes without explicit training on 3D data. These tend to be class-agnostic and generalize well.

**SAM3D / SAMPro3D / Point-SAM / OpenMask3D** – These methods leverage 2D image segmentation models (especially Meta’s Segment Anything (SAM) and CLIP) to propose or refine 3D masks.  For example, *SGS-3D* (2025) uses SAM on multi-view images of a scene and then refines the lifted 3D masks using geometric splitting/merging. It is **training-free** and shows strong zero-shot generalization. SGS-3D reports 34.3% mAP on ScanNet200 without retraining, which is lower than fully-trained Mask3D (53.3%), but importantly it *outperforms other zero-shot methods* and avoids catastrophic failures on novel scenes.  Qualitatively, SAM-based pipelines often produce very coherent object masks (since SAM’s image masks are high-quality) and tend to **avoid merges** if objects have distinct colors or textures.  In SGS-3D, over-segmentation is substantially reduced compared to earlier approaches (62 vs 102 predicted instances on ScanNet).  It still might split objects if 2D masks are ambiguous, but its “split-then-grow” scheme explicitly merges nearby pieces to complete objects.  Another SAM-based work (SAM-guided Graph Cut, ECCV 2024) similarly combines 2D masks with a 3D graph cut, yielding robust segmentation across diverse scenes.

OpenMask3D (NeurIPS 2023) uses CLIP-based features on multi-view images to do zero-shot instance segmentation.  It is more focused on semantic queries, but it produces class-agnostic masks too.  The Google research page notes that OpenMask3D “outperforms other open-vocabulary methods, especially on long-tail classes”.  Its practicality is limited by heavy dependencies (PyTorch, CLIP, multi-view fusion) and typically lacking a turnkey implementation.

**Segment Anything (SAM) 3D Extensions** – Several independent efforts extend SAM to 3D.  For example, Point-SAM (2024) is a learned 3D segmentation model trained on millions of pseudo-labels from SAM.  It is “promptable” (requires user seeds) and shows strong zero-shot transfer, but as a research prototype it requires significant infrastructure (transformer, GPU training).  SAMPro3D (arXiv 2023) projects SAM prompts into 3D with no training, aligning view prompts to produce consistent 3D segments.  These methods can handle arbitrarily new objects by relying on powerful 2D models.  In principle, they can segment metallic, irregular, or novel items by texture/appearance cues.  In practice, they often require input images (or rendered views) and multiple passes of SAM, making them computationally heavy (potentially many seconds per scene) and complex to integrate.  However, they have the advantage of **no training cost** and class-agnostic output.

**Hybrid Strategy Example:** A realistic hybrid pipeline might render a few key views of the mesh, run a 2D segmentation model (SAM or Grounding-DINO+SAM) on each, lift and merge masks into 3D points, then refine with a small geometric clustering (like LCCP) to enforce convexity.  This splits tasks: semantic grouping by 2D and precise boundaries by geometry.  Work like SGS-3D shows that such fusion dramatically improves segmentation completeness and reduces spurious masks.  

**Engineering Remarks:** SAM-based methods require handling image generation (from the mesh) or working with original RGB-D scans, and setting up Meta’s or Google’s open models.  Installation (PyTorch 2.x, CUDA, SAM repo, possibly CLIP, etc.) is moderate-to-hard.  However, these models are actively developed (SAM and CLIP have strong backing).  They often provide Python APIs.  All such pipelines output per-point or per-primitive labels that can convert to OBJ segments.  They are fully deterministic given fixed inputs (though e.g. SAM’s internal randomness is negligible or seedable).  

## Comparative Summary (Tables)  

**Table 1:** *Segmentation Quality & Behavior.* We compare merge/split tendencies and robustness qualitatively.  (High/Medium/Low are relative ratings.)  Merging is worst-case for our app, so “Low” merge error is desirable. 

| Method                  | Merge Error   | Split Error   | Touching Objects            | Thin/Delicate Objects        | Clutter Robustness          |
|-------------------------|---------------|---------------|-----------------------------|------------------------------|-----------------------------|
| **LCCP (Convexity)**    | **Very Low** | High  | Good (splits concavities)   | Fair (may split long rods)    | Decent (geometry-driven)    |
| Euclidean/DBSCAN        | High          | Low/Med       | *High* (merges on contact) | Poor (often loses small pieces) | Poor (merges many clusters) |
| Region Growing (normals)| Med           | High          | Med (depends on normal gaps)| Poor (misses thin edges)     | Med (noise causes splits)   |
| **Mask3D/SoftGroup**    | Low (for known classes) | Low (integrates full objects) | Good on trained categories | Good if seen in training   | Good on ScanNet-like clutter |
| **ISBNet**              | Low           | Low           | Good (SOTA for indoor)      | Good (trained data)          | Good                        |
| **SGS-3D (SAM-based)**  | Very Low      | Low/Med      | Excellent (leverages 2D cues) | Med (depends on image coverage)| High (generalizes to novel) |
| **OpenMask3D (CLIP)**   | Low           | Low/Med       | Good (multiview fusion)     | Med                          | High (open-vocab masks) |
| **Point-SAM**           | Low           | Low           | Very Good (prompted)        | Good (trained on parts)      | High (foundation model)     |

- *LCCP:* virtually no merges (two touching convex objects are split), but it “over-segments” (splits objects into parts).  
- *Euclidean Clustering:* easily *merges* objects in contact (bad) unless thresholds are tiny; rarely splits (unless threshold too small).  
- *Deep networks:* e.g. Mask3D/ISBNet yield very complete objects on their training classes (few splits/merges), but they only succeed on familiar geometry.  
- *SAM-based (SGS-3D, OpenMask3D):* train-free, class-agnostic, they excel at separating objects seen in images, greatly reducing splits (SGS-3D cuts instances from 102→62 vs older method) and avoiding merges using visual cues.  Thin structures (wires, etc.) may or may not be fully captured depending on view; typical pipelines make several views to improve coverage.  

**Table 2:** *Performance & Resources.* Approximate runtimes and resource needs on a modern GPU+CPU for a ~1M-point indoor scene.  

| Method            | GPU?   | GPU Mem (approx) | Time (processing one scene) | Memory Use | Scalability | Python API |
|-------------------|--------|------------------|-----------------------------|------------|-------------|------------|
| LCCP (PCL)        | Optional (GPU speedup) | <2 GB (supervoxel table) | ~0.5–1 s (with GPU) | Low (streaming) | Scales to 1–5M points | C++ only (no stable Py) |
| Euclid DBSCAN     | No     | ~1–2 GB (kd-tree) | ~1–5 s (CPU kd-tree)       | Low (O(n))  | Scales moderately (via octree) | PCL/Python wrappers exist |
| Plane/RgnGrow     | No     | <1 GB            | <0.1 s (fast)             | Low         | Good (planes scale well) | Open3D (Python)           |
| Mask3D/SoftGroup  | **Yes**| ~8–12 GB         | ~0.3–1 s                    | High (dense CNN) | Scales by batch (could trim) | PyTorch (Python)         |
| ISBNet            | **Yes**| ~8 GB            | ~0.24 s (237ms) | High       | Efficient (sparse convs) | PyTorch (Python)         |
| SGS-3D (SAM)      | **Yes**| ~6–12 GB         | ~10–20 s (full pipeline)   | High (multiview overhead) | Moderate (image count matters) | PyTorch / Open3D (Python) |
| OpenMask3D        | **Yes**| ~8–12 GB         | ~5–10 s (multi-view)       | High       | Similar to SGS-3D | PyTorch                 |
| Point-SAM         | **Yes**| ~4–8 GB          | seconds (prompt-based)    | High       | Uncertain    | PyTorch (Python)         |

- Geometry methods (LCCP, Euclid) run almost entirely on CPU (GPU optional for acceleration).  LCCP’s GPU speedup is reported 10×, making it sub-second.  Memory usage is small.  
- Deep networks (Mask3D, ISBNet) need a CUDA GPU; inference is fast (~0.3s) but models occupy ~8–12 GB VRAM (transformer weights + feature grids).  
- SAM-based pipelines require both a GPU (for SAM and image models) and CPU (for projecting masks).  The total pipeline can take on the order of tens of seconds (SGS-3D reports ~9.5s using only 10% of images for 34.3% AP).  They also need memory for multiple views.  
- All methods are parallelizable per scene, but classical methods scale more linearly with point count.  

**Table 3:** *Integration and Practicality.*  

| Method            | Integration (Python) | OBJ Export | Unity Changes | Install Complexity        | Determinism | Maintenance/Community       |
|-------------------|----------------------|------------|---------------|---------------------------|-------------|----------------------------|
| LCCP (PCL)        | Difficult (C++ lib)  | Native (clusters → meshes) | None          | **Moderate** (install PCL/VTK) | Yes (deterministic) | PCL mature (though Python bindings weak) |
| Open3D RegionGrow | Easy (Open3D-Python) | N/A (point labels) -> convert mesh | None | **Easy** (pip) | Yes (no randomness) | Active (Open3D project)    |
| Euclidean/DBSCAN  | Easy (scikit/PCL)    | N/A (convert) | None           | **Easy** (pip/conda) | Yes  | Common (DBSCAN known)   |
| Mask3D/SoftGroup  | Moderate (PyTorch)   | N/A (labels -> convert) | None | **Difficult** (conda, CUDA, dependencies) | Yes (fixed model) | Academic, code on GitHub |
| ISBNet            | Moderate (PyTorch)   | N/A       | None           | **Difficult** | Yes | Academic, code available |
| SGS-3D (SAM)      | Complex (PyTorch & SAM+Open3D) | N/A | None | **Difficult** (SAM, CLIP, CUDA, Open3D) | Yes | New (code on GitHub)  |
| OpenMask3D        | Complex (PyTorch, CLIP) | N/A | None           | **Difficult** | Yes | New (Google Repo)      |
| Point-SAM         | Complex (PyTorch)    | N/A       | None           | **Difficult** | Yes | New (GitHub repo)     |

- Unity integration for all methods remains trivial: each method outputs per-object meshes (either directly, or by grouping faces by point labels) and thus no Unity changes are needed.  The main differences are in the preprocessing pipeline.  
- Python support varies: Open3D and scikit-learn (for simple clustering) integrate easily in Python.  PCL methods may require writing a C++ wrapper or calling a command-line tool.  All deep/SAM methods are Python-friendly (PyTorch).  
- Installation: classical methods are straightforward (pip/conda for Open3D or PCL).  Deep/SAM require CUDA, heavy libraries, often only readily usable on Linux or via Docker.  We would rate LCCP/PCL as “moderate” because setting up PCL on Windows can be tricky; Open3D methods as “easy” (pip); deep models as “difficult” (managing multiple repos, CUDA versions).  
- Determinism: All methods above are essentially deterministic given fixed inputs (no randomness), except if some step uses non-deterministic GPU ops (minor).  
- Maintenance: PCL/Open3D are industrial-grade libraries.  Research models (Mask3D, SoftGroup, SGS-3D) have published code but minimal long-term support; however they have active research interest and could be adopted.  SAM-based pipelines are based on widely-used models (Meta’s SAM, CLIP), which are well-supported in the community.  In general, classical code is stable; deep/SAM code may require occasional updates.

## Evaluation of the Current (Open3D/LCCP) Pipeline  

The existing pipeline (Open3D/PCL segmentation, likely LCCP-based) has these characteristics:

- **Strengths:** Very low merge errors – different objects are seldom combined, thanks to convexity heuristics.  Fast execution (sub-second per scene on modern hardware).  Simple: outputs cluster meshes directly.  Deterministic and easy to understand.  Minimal dependencies (Open3D/PCL).  No training needed.

- **Weaknesses:** High over-segmentation – many single objects become multiple colliders.  This can clutter the Unity scene (users clicking may need to trigger several colliders to select one object).  Particularly, objects with concavities or thin appendages get fragmented.  LCCP also relies purely on geometry; if the scan is noisy or incomplete, segments may be spurious or incomplete.  It struggles with objects on shelves or touching (e.g. books on a shelf often look flat and merge into one segment unless convex cues separate them).  Industrial shapes (machinery, tools, cables) may not be well-handled unless convexity edges are clear.  In short, it optimizes “no merges” at the cost of many splits and some fragmentation under real-world scanning noise.

Given these trade-offs, **LCCP is still competitive as a baseline** since it aligns with the priority of avoiding merges.  However, it likely over-splits too much and may fail in highly cluttered or novel scenes.  The question is whether modern methods (deep or hybrid) can markedly reduce splits/false fragments while still ensuring no-object merges.  For example, SGS-3D shows that a SAM-based refinement can drastically cut the number of segments (from 102→62), suggesting a large gain in coherence.  We must weigh such improvements against integration cost.

## Recommendations  

After surveying the methods above (with supporting citations), we make the following evidence-based recommendations:

- **Best Overall Algorithm:** A **hybrid SAM-based pipeline (e.g. SGS-3D style)** that uses 2D segmentation (SAM or similar) plus geometric refinement.  This offers the best balance: it avoids merges, greatly reduces spurious splits, and generalizes to novel objects.  It scored highest among zero-shot methods in benchmarks and is class-agnostic.

- **Best Pure Geometric Method:** **LCCP (supervoxel convexity)** remains the top classical choice.  It has essentially zero merge errors and is very fast.  No newer pure-geometry method significantly beats it on merges.  (One could optionally post-process LCCP clusters with a simple bounding-box merging to fix minor splits, but base LCCP is strong.)

- **Best Deep Learning Method:** Among trained networks, **Mask3D/ISBNet** achieve the highest accuracy on indoor benchmarks.  They generate holistic object segments with few splits or false merges on familiar classes.  (If ample compute and training data were available, one could fine-tune Mask3D on lab/industrial objects.)  However, without retraining, their out-of-domain reliability is uncertain.  SoftGroup is similarly strong.  In practice, these are more for reference; the pipeline’s focus is on off-the-shelf readiness.

- **Best Hybrid (Geometry+Learning):** The **SGS-3D** pipeline (or variants like SAM-guided Graph Cut) is a leading example of hybrid zero-shot segmentation.  It first leverages pre-trained image segmentation (SAM+2D models) and then refines with geometry.  This yields very clean instance outputs with few errors.  We expect it to outperform plain LCCP if implemented.

- **Ease-of-Integration:** For minimal friction, **Open3D’s built-in methods** (plane segment + region grow or simple clustering) or **point-cloud DBSCAN** are easiest, since they require only `pip install open3d` and basic Python code.  They produce point labels that we can convert to meshes.  They do not require maintaining C++ or heavy models.  Among deep methods, Mask3D has a PyPI package (Mask3D’s author provides one), making it somewhat easier to install than a multi-component SAM pipeline.  But overall, classical/simple geometry is easiest to integrate and maintain.  

- **Maintainability:** Long-term, **simple geometric code (PCL/Open3D)** is easiest.  Industry uses PCL for many perception tasks.  SAM and deep models are active research fields; while trendy now, their APIs may change and they have heavier dependencies.  For this reason, a robust production system might prefer a geometry-first approach, possibly augmented by one trained step rather than full heavy ML.

- **For Industrial/Lab Scenes:** Scenes with irregular shapes (pipes, machines, tools) will likely be best segmented by *shape cues* rather than semantic priors.  Classical convexity-based or clustering methods might handle these (if surfaces are well-scanned).  SAM-based vision cues could also help if textures/color differences exist, but clutter in labs may confuse 2D models.  Without clear training data, we suspect a hybrid unsupervised approach (SGS-3D) might still give better generalization.  

- **Best for Unity Portal Pipeline:** Since the Unity side is fixed (raycasting on mesh colliders), the best method is the one that yields **clean object meshes** so that each click hits exactly one segment.  This again points to the hybrid SAM+geometry solution: it minimizes false merges (two objects with one collider) and produces complete object shapes (so a click on any part of an object hits a collider).  LCCP is safe in terms of not merging, but you may need to select several colliders for one object.  SoftGroup/Mask3D (if retrained) would also give ideal one-collider-per-object outputs, but is harder to obtain.  In practice, the SAM-hybrid (SGS-3D) pipeline is likely the top pick: it is designed for high-fidelity instance extraction and is explicitly tested on diverse scenes.

## Final Segmentation Pipeline Design  

**Recommended Pipeline (2026)** – We propose a hybrid pipeline combining multi-view image segmentation with geometry refinement:

1. **Scene Preprocessing:** (optional) Remove large planar surfaces (floor, walls, ceiling) via RANSAC. Export remaining mesh as point cloud or surface.
2. **Multi-View Image Capture:** Generate a sparse set of RGB-D renders of the mesh from different angles (e.g. 5–10 views).  (Or if original sensor RGB-D data is available, use that.)  
3. **2D Mask Proposals:** Run a 2D foundation model (e.g. SAM or Grounding-DINO+SAM) on each image to get segmentation masks of *any* objects.  This yields many 2D masks per view.  
4. **Mask Lifting:** Project 2D masks into 3D using known camera parameters (estimating depths if needed). Associate each 3D point/vertex with one or more image masks.  
5. **Mask Filtering:** Following SGS-3D, remove ambiguous masks (e.g. co-occurrence filtering) to keep only reliable instance candidates.
6. **Over-Segmentation (Seeds):** Optionally, over-segment the 3D mesh into small “superpoints” (e.g. by LCCP or voxel clustering).  Use the filtered 3D masks to group these superpoints (each 3D mask votes on multiple seed regions).  
7. **Spatial Refinement (“Split-then-Grow”):** Perform SGS-3D’s two-step refinement: first use spatial continuity to split any mask that accidentally includes multiple objects; then grow each split seed by merging adjacent points that share similar mask features.  This produces final 3D instance labels for points.  
8. **Cluster Extraction:** Convert instance-labeled points or faces into separate OBJ meshes (each cluster as a mesh with associated texture).  Discard tiny clusters below a volume threshold (likely just noise).
9. **Export:** Save `cluster0.obj`, `cluster1.obj`, etc. (and possibly a JSON mapping of cluster IDs to metadata if needed).  

**Justification:** This pipeline follows the design of SGS-3D (split-then-grow), which has demonstrated state-of-art fidelity on diverse indoor scenes.  It uses readily available foundation models (SAM, CLIP) and does not require any training.  Geometry steps (steps 1,6) ensure no-false merges.  We expect runtime on the order of 10–30 s per scene on a modern GPU (like an RTX A6000) when using ~5–10 images, as SGS-3D reports ~9.5 s for 10% of images.  Accuracy should be high: objects with distinct appearance will be well separated, and even similar objects will be split by the split-&-grow logic.  Worst-case, if some objects have no texture, their masks come only from geometry (the split step will then ensure they are not merged incorrectly).  

**Expected Accuracy:** Based on SGS-3D’s results, we can expect ~30–35% mAP on generic indoor instance segmentation with no training.  More importantly, the qualitative outcome will be that almost every physical object is isolated.  The number of clusters will be close to the true count of objects (SGS-3D used only 10% images to get 62 clusters vs 102 for a prior method, indicating very few spurious splits).  Merge errors should be negligible by design. 

**Software Stack:** Use Python 3.9+ with PyTorch (CUDA) for the model, Open3D for mesh/point operations, and possibly OpenCV/numpy for image I/O.  We would install Meta’s SAM and optionally Grounding-DINO via pip, and CLIP if used.  SGS-3D code is available on GitHub, which can be adapted.  For supervoxel or splitting steps, Open3D’s `VoxelDownSample` and KD-tree neighbors suffice.  The output writing can use Open3D or Trimesh.  We may package this as a Conda environment or Docker for reproducibility.

**Implementation Roadmap:**  
- **Prototyping:** Implement mesh→images (rendering), image→masks (SAM inference), mask→3D points.  Verify on small scenes.  
- **Refinement Logic:** Code the splitting/merging (or adapt SGS-3D code).  Tune parameters on a few example scans (ensuring no merges).  
- **Performance Tuning:** Accelerate heavy loops with C++ or vectorized operations.  Possibly cache SAM outputs if similar scenes.  
- **Testing:** Validate on a variety of lab/industrial meshes (where ground truth cluster IDs can be roughly counted by hand). Check error modes (missed object, merged objects).  
- **Deployment:** Integrate into the Python pipeline, ensuring OBJ export matches Unity’s expectations (normals, texture mapping, etc.).  Set up automated tests for determinism (run twice on same mesh, compare results).

**Potential Risks:** Heavy dependency on 2D model quality – if textures are poor (monochrome lab walls), SAM might produce large ambiguous regions.  Mitigation: include more camera views, incorporate simple color segmentation fallback, or rely on the “grow” step to refine.  Another risk is runtime: if too many images or high-res images are used, processing time could blow up.  We will control this by limiting images (SGS-3D found 10% of views was enough).  Depth ambiguities in mask lifting can also cause errors; SGS-3D’s occlusion-aware mapping strategy should address that.

**Future Upgrades:** As foundation models improve, this pipeline can directly benefit (e.g. newer SAM or diffusion-based 2D segmenters).  One could add a small learned 3D refinement network to further polish boundaries without needing full training (like a lightweight point-critic).  If training data becomes available, a supervised instance model (Mask3D/ISBNet) could be incorporated as an additional stage.  However, the proposed pipeline remains fully unsupervised and thus general.

**Summary:** The above hybrid pipeline offers a production-ready segmentation stage optimized for our use-case.  It maximizes instance accuracy (no merges, few splits), is robust to novel objects (via SAM), and integrates into the Unity workflow with minimal changes. It strikes a practical balance: better quality than raw LCCP, yet avoids the brittleness of fully supervised deep models. 

