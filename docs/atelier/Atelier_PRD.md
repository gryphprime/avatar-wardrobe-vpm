# Atelier — Product Requirements Document

**Status:** Draft product definition<br>
**Product:** Atelier<br>
**Positioning:** A standalone successor/rethink of Avatar Wardrobe's architecture and UX, shipped as a separate product offering.<br>
**Relationship to Avatar Wardrobe:** Atelier may reuse substantial Avatar Wardrobe code, patterns, and proven features, but Avatar Wardrobe remains a distinct product. Atelier must not depend on Avatar Wardrobe being installed or licensed.

---

## 1. Executive Summary

Atelier is a desktop avatar creation and management environment that uses Unity as a backend execution, rendering, compatibility, and build engine rather than as the primary user interface.

The product goal is to let users work with complex Unity/VRChat avatar projects through a responsive, visual, drag-and-drop-oriented desktop experience while hiding most Unity latency and implementation complexity behind asynchronous operations, caching, background workers, and managed project lifecycle.

Atelier should feel closer to a modern character creator and creative asset workspace than to a Unity Editor extension.

The core user experience is:

> **Open Atelier → choose an avatar → browse or import assets → try changes visually → customize → save a desired state → let Atelier synchronize Unity in the background → validate/build when ready.**

Unity remains authoritative for operations that actually require Unity semantics, such as imports, serialized Unity objects, shaders, editor-only packages, NDMF/Modular Avatar processing, supported adapters, validation, and final build steps. Atelier is authoritative for its own library metadata, desired avatar state, workspace manifests, cached artifacts, operation history, and product UX.

The first implementation should prioritize **perceived responsiveness and decoupling** rather than aggressive project pruning. Expensive Unity work should happen asynchronously and should rarely block the user interface. More aggressive workspace materialization and project-size optimization can follow after instrumentation proves where the real bottlenecks remain.

---

## 2. Product Vision

### 2.1 Vision statement

**Atelier is the avatar creation workspace that lets users build, customize, organize, preview, and prepare Unity-backed avatars without having to work directly in Unity for routine tasks.**

### 2.2 Product principles

1. **Unity is a backend, not the product UI.**
   - Users should not need to manually open Unity for ordinary Atelier workflows.
   - Native Unity remains available as an advanced escape hatch.

2. **The UI must stay responsive even when Unity is slow.**
   - User intent is recorded immediately.
   - Slow Unity operations are queued, coalesced where safe, and reconciled later.

3. **Desired state and confirmed Unity state are distinct.**
   - Atelier should never pretend an asynchronous Unity mutation has completed when it has merely been accepted.
   - The user can continue designing while synchronization is pending.

4. **Visual interaction first.**
   - Prefer dragging, selecting, previewing, contextual controls, and direct manipulation over hierarchy/inspector concepts.

5. **Do not recreate Unity wholesale.**
   - Atelier should expose high-value avatar workflows, not duplicate the entire Unity Editor.

6. **Safe by default.**
   - Exact target identity, reversible operations, immutable source assets, derived outputs, and explicit recovery states are core requirements.

7. **Open integration layer.**
   - First-party adapters should be free and open source.
   - First-party optional integrations should favor permissively licensed dependencies.
   - Community authors should be able to create adapters using the same public integration surface used by first-party adapters.

8. **Measure before aggressively optimizing.**
   - Hide latency first.
   - Instrument Unity costs.
   - Optimize project materialization, import sets, or worker topology only when real data justifies the complexity.

---

## 3. Relationship to Avatar Wardrobe

Atelier should be treated as an **AW 2-style successor in architecture and user experience**, but as a separate product offering.

### 3.1 What “AW 2” means

Atelier may reuse:

- external local library code and data structures;
- immutable archive/original asset handling;
- provenance and update-impact tracking;
- drag-and-drop command paths;
- durable asynchronous operation infrastructure;
- exact avatar and exact-copy targeting;
- retry/recovery logic;
- snapshot and photo-history infrastructure;
- private persistent Unity worker concepts;
- appearance-editing foundations;
- menu diagnostics/organization concepts;
- supported adapter patterns;
- advanced scene/object inspection where appropriate;
- test fixtures, regression tests, and validation patterns.

### 3.2 What must change

Atelier should not be constrained by Avatar Wardrobe's original assumption that the product is a Unity-hosted wardrobe tool.

Atelier should instead own:

- the standalone desktop lifecycle;
- workspace lifecycle;
- project/package management;
- Unity worker lifecycle;
- desired-state model;
- asset library and staging;
- cache/artifact layer;
- generic adapter SDK;
- character-creator-style visual UI;
- background synchronization;
- product-level recovery and status.

### 3.3 Product separation

Avatar Wardrobe remains a separate product.

Atelier:

- must not require Avatar Wardrobe;
- must not present itself as an Avatar Wardrobe add-on;
- may share code internally where licensing and repository structure allow;
- may support migration/import from Avatar Wardrobe data later;
- may interoperate with Avatar Wardrobe through public protocols later, but this is not an MVP requirement.

---

## 4. Existing Reusable Baseline from Avatar Wardrobe

The current Avatar Wardrobe development milestone provides a substantial technical baseline that can inform Atelier. This is implementation evidence, not a claim that Atelier already exists or that all production acceptance gates are complete.

The milestone reports working implementations for:

- exact avatar/copy targeting;
- revision checks;
- reversible replacement/removal;
- configuration-aware Undo;
- external local library with immutable archive originals;
- reviewed imports, provenance, offline browsing, and update-impact records;
- durable asynchronous commands;
- pending UI rows, failure notices, Retry/Dismiss, and reload/lost-response recovery;
- core drag gestures;
- processed snapshots for supported inputs;
- a private persistent Unity worker;
- Front / Three-quarter / Back and Before / After photo flows;
- retained photo history and explicit PNG export;
- appearance recipes and supported material/static-shape editing;
- selected menu diagnostics/organization;
- AAO preview/apply and Gesture Manager targeting;
- opt-in advanced Scene hierarchy/object editing;
- automated test coverage across Python, JavaScript, C#, and Unity integration fixtures.

The same milestone also records unfinished areas that Atelier should not treat as solved, including:

- representative production-avatar validation;
- broader plugin compatibility;
- complete appearance-region workflows;
- complete automatic photo-refresh coverage;
- worker capacity/coalescing and bounded terminal retention;
- richer operation phase/warning metadata;
- production performance measurements;
- live 3D/video streaming;
- complete build/upload acceptance evidence;
- universal asset dependency pruning;
- universal plugin support.

**Source baseline:** Avatar Wardrobe milestone and remaining-work handoff, commit `26df488` on `dev/ux-plan`.

---

## 5. Target Users

### 5.1 Primary user: avatar customizer

A user who buys avatars, outfits, hairstyles, accessories, textures, and creator tools and wants to combine them without understanding Unity deeply.

Needs:

- easy imports;
- visual try-on;
- fast switching between options;
- simple appearance controls;
- safe installation/removal;
- reliable build preparation;
- minimal Unity interaction.

### 5.2 Secondary user: experienced avatar creator

A user already comfortable with Unity but frustrated by project scale, repetitive setup, tool fragmentation, and slow project workflows.

Needs:

- powerful organization;
- exact target control;
- fast navigation;
- background processing;
- versioned workspaces;
- plugin/package management;
- an advanced escape hatch to Unity.

### 5.3 Secondary user: commission creator

A creator managing multiple avatars or client projects.

Needs:

- reusable assets and source provenance;
- multiple isolated workspaces;
- reliable history;
- repeatable setup;
- predictable package environments;
- snapshots for review;
- clean handoff/build state.

### 5.4 Future user: adapter/tool author

A developer who wants to integrate a Unity/avatar tool with Atelier without modifying Atelier itself.

Needs:

- documented public SDK;
- stable action protocol;
- test utilities;
- dependency declarations;
- standard UI controls;
- operation/result/artifact APIs;
- clear trust and distribution model.

---

## 6. Problems to Solve

### 6.1 Unity is the wrong routine UX for many avatar tasks

Routine customization frequently requires knowledge of:

- scenes;
- hierarchy objects;
- prefabs;
- inspectors;
- package managers;
- Unity project structure;
- editor plugins;
- serialized objects;
- build pipelines.

The user's actual intent is much simpler:

> “Put this jacket on this avatar, make it black, see how it looks, and keep it if I like it.”

Atelier should express the intent directly.

### 6.2 Unity operations can be slow and unpredictable

Large projects may experience expensive:

- startup;
- package resolution;
- script compilation/domain reload;
- imports;
- asset scans;
- shader warmup;
- plugin processing;
- validation/build work.

Atelier should not assume all of these can be made fast immediately. It should instead keep them off the interactive critical path.

### 6.3 User assets and Unity projects are conflated

Users often accumulate many unrelated purchases inside a Unity project, which increases complexity and may increase import/indexing overhead.

Atelier should distinguish:

- **Library:** everything the user owns and has indexed;
- **Workspace:** what is relevant to one avatar/project context;
- **Unity project:** execution/build environment;
- **Generated outputs:** Atelier-owned derived state.

### 6.4 Third-party tooling is fragmented

Users currently jump among Unity windows and tools.

Atelier should offer a generic integration layer where supported operations can be surfaced through coherent Atelier workflows.

### 6.5 Users need visual confidence

The user should not need to return to Unity just to answer:

> “How does this outfit actually look on my avatar?”

Atelier should provide cached and refreshed avatar views, eventually including a responsive live preview.

---

## 7. Goals

### 7.1 Product goals

1. Launch Atelier independently of Unity.
2. Let users perform normal avatar customization without manually opening the Unity Editor.
3. Keep core UI actions responsive regardless of Unity latency.
4. Provide a visual character-creator-style avatar experience.
5. Manage project/package/workspace lifecycle from Atelier.
6. Reuse the user's asset library across workspaces without requiring every asset to live in every Unity project.
7. Provide safe, observable background Unity execution.
8. Provide an open adapter system.
9. Make first-party adapters free/open and focus first-party optional integrations on permissive dependencies.
10. Preserve a native Unity escape hatch for unsupported/advanced work.

### 7.2 MVP success criteria

A representative user should be able to:

1. install Atelier;
2. register or create an Atelier workspace;
3. add an avatar/source project;
4. browse their local asset library without Unity running;
5. launch a visual avatar editing session;
6. drag an outfit or supported asset onto the avatar;
7. see immediate pending/desired state;
8. continue browsing while Unity processes in the background;
9. receive a refreshed authoritative snapshot when ready;
10. safely replace/remove/undo a change;
11. install at least one supported VPM integration from Atelier;
12. open the managed project in normal Unity when needed;
13. reconcile back into Atelier afterward.

---

## 8. Non-Goals for Initial Release

Atelier v1 should **not** attempt to:

- replace Unity as a general-purpose editor;
- implement every Unity inspector or Scene View feature;
- guarantee support for arbitrary proprietary or closed plugins;
- implement universal automatic clothing fitting;
- perfectly infer file-level dependency closures for every Unity asset package;
- dynamically mount/unmount individual files for maximum project minimalism;
- share a mutable Unity `Library` across unrelated projects;
- support every VRChat upload/account flow without native/manual fallback;
- provide arbitrary third-party JavaScript/UI execution in the desktop app;
- provide a marketplace in v1;
- provide arbitrary remote/cloud execution in v1;
- require live 3D streaming before shipping a useful product;
- optimize every Unity operation before real performance evidence exists.

---

## 9. Product Model

Atelier should expose the following user-facing concepts.

### 9.1 Library

The long-lived collection of user-owned source assets.

Contains:

- purchased archives;
- extracted/reviewed products;
- avatars;
- outfits;
- hair;
- accessories;
- textures;
- derived metadata;
- provenance;
- tags/favorites;
- cached thumbnails;
- compatibility hints.

The Library is not the Unity project.

### 9.2 Avatar

A high-level Atelier entity representing the avatar the user edits.

It references:

- source avatar assets;
- current workspace;
- desired state;
- confirmed Unity state;
- cached preview artifacts;
- saved variants/presets as applicable;
- relevant packages/integrations.

### 9.3 Workspace

A managed execution context for an avatar or related project.

Contains/references:

- Unity project path;
- Unity version;
- package environment;
- active/materialized user assets;
- generated outputs;
- persistent Unity `Library` cache;
- worker state;
- synchronization state;
- operation history.

For v1, Atelier may adapt existing projects rather than fully minimize them. The workspace abstraction must still exist so that more aggressive management can be added later.

### 9.4 Desired State

What the user currently wants Atelier to produce.

Examples:

- current outfit selection;
- active hair;
- appearance parameters;
- requested material settings;
- requested integration configuration.

Desired State is updated immediately from the UI.

### 9.5 Confirmed State

What Unity has actually applied and Atelier has reconciled.

The UI must be able to show divergence from Desired State.

### 9.6 Rendered State

What the currently displayed photograph/live preview depicts.

Rendered State may temporarily lag behind Confirmed State.

### 9.7 Artifact

An immutable generated output, including:

- preview image;
- thumbnail;
- validation report;
- performance report;
- exported comparison;
- generated metadata snapshot.

Artifacts should include enough revision/input metadata to reject stale results.

### 9.8 Integration / Adapter

An optional extension that contributes supported actions against Atelier resources or Unity targets.

Examples:

- Avatar Optimizer integration;
- Gesture Manager integration;
- VRCQuestTools integration;
- future external tools.

---

## 10. Core UX

### 10.1 Home

The home screen should prioritize avatars/workspaces, not Unity projects.

Example:

```text
Atelier

My Avatars
┌─────────────┐ ┌─────────────┐
│ Mamehinata  │ │ Manuka      │
│ [snapshot]  │ │ [snapshot]  │
│ Ready       │ │ Syncing…    │
└─────────────┘ └─────────────┘

+ Add Avatar
```

Secondary navigation:

- Avatars
- Library
- Integrations
- Activity
- Settings

Unity project details should be available but not dominate the primary navigation.

### 10.2 Avatar Creator view

The central editing view should be character-first.

```text
┌──────────────┬──────────────────────────────┬───────────────┐
│ Library      │                              │ Properties    │
│              │                              │               │
│ Hair         │          AVATAR              │ Appearance    │
│ Tops         │         viewport             │ Color         │
│ Bottoms      │                              │ Shapes        │
│ Shoes        │                              │ Options       │
│ Accessories  │                              │               │
│              │                              │               │
├──────────────┴──────────────────────────────┴───────────────┤
│ Undo   Compare   Snapshot        Unity syncing…            │
└────────────────────────────────────────────────────────────┘
```

Core interactions:

- drag asset onto avatar to try/apply;
- click item to inspect;
- replace a currently selected item explicitly;
- use contextual properties instead of raw inspector fields;
- orbit/zoom avatar when live viewport is available;
- use camera presets otherwise;
- keep last confirmed image visible during refresh;
- show pending state on the affected object, not as modal blocking UI.

### 10.3 Contextual camera behavior

When supported by the preview system:

| Context | Camera behavior |
|---|---|
| Hair | head/shoulders |
| Eyes / makeup | face close-up |
| Tops | torso |
| Shoes | feet/lower body |
| Full outfit | full body |
| Expressions | face |
| Performance | full avatar |

Camera transitions should be product polish, not a release blocker if the first version uses static snapshots.

### 10.4 Drag-and-drop

Core gestures:

| Gesture | Result |
|---|---|
| Files/archive → Library | inspect/import to Library |
| Library item → avatar | request Try On / desired-state change |
| Item → selected worn slot/item | replace exact target |
| Item → active/worn list | request install/application |
| Worn item → Remove | request removal |

Every drag operation must have a click/keyboard-accessible equivalent.

### 10.5 Snapshot system

Atelier must provide avatar images so users can inspect results without returning to Unity.

Minimum views:

- Front
- Three-quarter
- Back

Required behavior:

- keep previous image visible while refreshing;
- label stale image subtly as updating;
- bind photo to avatar/state revision;
- reject late stale render results;
- preserve useful history;
- support Before/After comparison;
- support explicit export;
- refresh automatically after confirmed supported changes where safe.

### 10.6 Live preview evolution

Live 3D/video preview is a product goal, but should be incremental.

Recommended progression:

1. snapshot-based character creator;
2. persistent preview worker;
3. low-rate interactive RenderTexture stream;
4. higher-performance transport only if profiling justifies it.

Do not delay the product waiting for perfect live streaming.

---

## 11. Asynchronous Interaction Model

This is a foundational requirement.

### 11.1 Rule

**No routine Unity operation should block the Atelier UI thread.**

### 11.2 Three-state model

Atelier must explicitly track:

```text
Desired State
    what the user wants

Confirmed State
    what Unity has applied

Rendered State
    what the current visual depicts
```

Example:

```text
Desired:   Black Hoodie
Confirmed: White Shirt
Rendered:  White Shirt
```

UI:

> Black Hoodie — Applying…

Later:

```text
Desired:   Black Hoodie
Confirmed: Black Hoodie
Rendered:  White Shirt
```

UI:

> Black Hoodie ✓  · Updating preview…

Finally all three converge.

### 11.3 Operation classes

Atelier should distinguish at least two internal operation types.

#### A. Desired-state updates

Characteristics:

- cheap UI acceptance;
- coalescible;
- latest-value-wins where safe;
- not every intermediate value must be persisted to Unity.

Examples:

- slider movement;
- camera movement;
- hover preview;
- pose selection;
- candidate selection;
- transient material value preview.

#### B. Durable mutations

Characteristics:

- explicit operation ID;
- persisted acceptance;
- ordered execution;
- target revision/preconditions;
- result/receipt;
- retry/recovery semantics;
- cannot be silently discarded.

Examples:

- install outfit;
- replace exact item;
- remove exact item;
- save/apply persistent appearance;
- package mutation;
- optimization apply;
- build preparation.

### 11.4 Priorities

Atelier should schedule work in at least three priority classes.

**Interactive**

- current try-on;
- current preview;
- user-visible selected operation.

**Background**

- diagnostics;
- back-view photo;
- performance scan;
- metadata refresh;
- package checks.

**Idle**

- stale thumbnail refresh;
- cache warming;
- low-value indexing;
- cleanup.

Interactive work must preempt or outrank nonessential background work where safe.

### 11.5 Coalescing

Atelier must coalesce safe superseded requests.

Example:

```text
Hair A → Hair B → Hair C
```

If Unity has not started applying the transient preview, only Hair C may need to be processed.

Do not apply this rule to explicit durable user actions unless the operation semantics explicitly allow supersession.

### 11.6 Debouncing expensive derived work

Full validation, performance analysis, composed-menu diagnostics, and complete snapshot sets should not rerun after every intermediate edit.

Trigger after:

- user stops interacting;
- a durable operation completes;
- an explicit validation boundary;
- a short safe debounce window.

### 11.7 Blocking boundaries

Some workflows legitimately require synchronization:

- final build/upload;
- handing the real project to interactive Unity;
- destructive operations without safe optimistic semantics;
- migrations/major package changes requiring consistency.

These should be explicit and rare.

---

## 12. Unity Runtime / Worker Model

### 12.1 Normal product behavior

Atelier should start independently.

Unity should start only when required.

Modes:

| Atelier mode | Unity process | Purpose |
|---|---|---|
| Offline | none | Library, organization, cached artifacts, desired-state planning |
| Background | hidden/automated Editor worker | import, mutations, rendering, validation |
| Interactive Unity | normal Editor | advanced/manual fallback |

### 12.2 Worker manager responsibilities

The desktop app owns:

- worker startup;
- worker shutdown;
- project ownership coordination;
- readiness/health;
- operation dispatch;
- reconnect/recovery;
- idle shutdown policy;
- logs and diagnostics.

### 12.3 Worker lifecycle

Default behavior:

1. Atelier launches with no Unity process.
2. User browses without Unity.
3. Entering a Unity-dependent flow triggers background worker startup.
4. Cached state remains visible during startup.
5. Worker remains warm during the active editing session.
6. Worker can shut down after an idle threshold.

### 12.4 Batch/headless strategy

Use automation-friendly Unity startup where appropriate, but do not assume `-batchmode` eliminates the dominant project-loading costs.

Rendering workers require graphics capability and therefore cannot use a graphics-disabled mode when generating faithful Unity previews.

### 12.5 Interactive Unity handoff

When the user chooses **Open in Unity**:

1. finish or safely pause relevant pending project mutations;
2. flush confirmed state;
3. stop any worker that owns the same real project;
4. launch the normal Unity Editor;
5. reconnect the Atelier bridge when available;
6. observe/reconcile manual changes;
7. regain background ownership after Unity exits.

Atelier must not allow two writers to fight over the same project.

---

## 13. Workspace and Project Lifecycle

### 13.1 Long-term direction

Atelier should increasingly own project lifecycle rather than merely attaching to arbitrary existing projects.

However, v1 should prioritize reliability and async UX over aggressive materialization.

### 13.2 Workspace model

Recommended future structure:

```text
Atelier Library
    immutable originals
          ↓
Workspace Manifest
    avatar + active assets + packages
          ↓
Managed Unity Project
    controlled package set
    relevant/materialized assets
    generated outputs
    persistent Library cache
```

### 13.3 Existing projects

Atelier must support adapting an existing project during migration/adoption.

Recommended flow:

> Register existing Unity project → inspect → create Atelier workspace metadata → preserve original project → optionally migrate later.

Do not destructively convert existing user projects by default.

### 13.4 Managed project direction

Future managed-project features may include:

- one primary avatar/workspace;
- persistent per-workspace Unity `Library` cache;
- known Unity version;
- known VPM package set;
- shared immutable source library;
- generated writable overlay;
- inactive-workspace cache cleanup;
- workspace recreation from manifest.

### 13.5 Do not overpromise symlink optimization

The optimization is **controlling what Unity sees**, not merely using symlinks.

A symlink exposing a large tree to `Assets/` can still result in Unity discovering/importing it.

Therefore the architecture should use a generic **materializer** abstraction instead of hard-coding symlinks.

Potential materialization methods:

- symlink;
- junction;
- hardlink;
- reflink/clone;
- copy;
- package/local path reference.

### 13.6 Conservative asset pruning

Do not attempt universal file-level dependency pruning in v1.

Support levels may be:

1. complete product/package;
2. known variant/folder;
3. validated dependency closure.

Unknown integrations may require the entire product to remain available.

---

## 14. Asset Library

### 14.1 Requirements

The Library should:

- exist independently of Unity;
- preserve original files/archives;
- track provenance and version;
- provide search/filter/favorites/tags;
- support offline browsing;
- show cached thumbnails and avatar snapshots;
- stage reviewed imports;
- distinguish original and derived assets;
- support references from multiple workspaces.

### 14.2 Immutable source policy

Original purchased/source assets should be immutable by policy.

Atelier-generated edits should live in a writable overlay/output area.

Example:

```text
Library/Originals/...      read-only policy
Workspace/Generated/...   writable
```

This avoids cross-project mutation when the same source asset is reused.

### 14.3 Generated outputs

Examples:

- modified materials;
- generated textures;
- generated menu/controller assets;
- adapted prefabs;
- appearance recipes;
- optimization configuration;
- Atelier metadata.

---

## 15. Project and Package Management

### 15.1 vrc-get / ALCOM direction

Atelier should reuse a permissively licensed VPM/project-management engine where practical rather than rebuilding Creator Companion-style package logic.

The preferred boundary is a small internal abstraction, e.g.:

```text
ProjectRuntime
    discoverUnity()
    inspectProject()
    createWorkspace()
    resolvePackages()
    installPackage()
    removePackage()
    updatePackages()
    launchUnity()
```

The first implementation may invoke `vrc-get` as a subprocess. A deeper library integration can be considered later if required.

### 15.2 Do not absorb another application's UX

Atelier should reuse package/project plumbing, not embed ALCOM's complete UI or mental model.

The user sees:

> Install Integration

not:

> Manage VPM Repository Dependencies in Project Settings

### 15.3 Package mutations are asynchronous

Installing/updating/removing a package can cause:

- download;
- dependency resolution;
- manifest changes;
- Unity compilation;
- domain reload;
- adapter restart.

The UI should show:

> Avatar Optimizer — Installing…

while Atelier remains responsive.

### 15.4 Package mutation exclusivity

Package mutations must coordinate with Unity mutation operations because package changes can trigger compilation/domain reload.

At minimum:

- package mutation: exclusive against scene/project mutations;
- project mutation: serialized per workspace;
- preview jobs: retry/reconcile across reload;
- Atelier-only library work: independent.

---

## 16. Adapter / Integration Platform

### 16.1 Policy

First-party adapters:

- free;
- open source;
- use the public SDK;
- tested against declared versions;
- prefer permissively licensed optional dependencies.

Community adapters:

- independently maintained;
- explicitly installed by the user;
- use the same public contract;
- are clearly marked as community-maintained;
- are not automatically trusted just because they use the SDK.

### 16.2 First-party dependency policy

Prefer optional integrations whose code and redistributable content fit Atelier's permissive-license policy.

Examples already discussed as candidates include permissively licensed avatar tooling. Each actual package release still requires a release-level dependency/content audit.

### 16.3 SDK concepts

The public SDK should remain small.

Shared concepts:

- Project
- Resource
- Action
- Operation
- Artifact
- Target
- Dependency

Avoid baking avatar/outfit concepts into the generic SDK.

### 16.4 Adapter declaration

An adapter should be able to declare:

- identity;
- version;
- compatible Atelier SDK/protocol range;
- required package dependencies;
- target types;
- actions;
- input schema;
- output schema;
- read/preview/mutation classification;
- expected changed resources;
- generated artifacts;
- recovery/undo support where applicable;
- simple UI metadata.

### 16.5 Shared infrastructure

Adapters must reuse Atelier's:

- target resolution;
- worker execution;
- operation queue;
- cache/artifact store;
- result/error model;
- retry/recovery infrastructure;
- standard UI controls.

Adapters must not each create their own local servers, queue systems, or snapshot caches.

### 16.6 UI extensibility

v1 community adapters should contribute:

- actions;
- simple forms;
- results/reports;
- optional preview artifacts.

Do not permit arbitrary custom frontend code in v1.

### 16.7 Trust

Unity adapter code is trusted local Editor code, not a sandbox.

Atelier must:

- show adapter origin;
- require explicit installation;
- distinguish official/community;
- avoid auto-executing code merely because it exists in an imported asset archive.

---

## 17. Preview and Rendering Architecture

### 17.1 Authoritative renderer

Unity should remain the authoritative renderer for Unity avatar fidelity.

Do not make a web renderer responsible for reproducing arbitrary Unity shaders or editor processing.

### 17.2 Snapshot-first

Initial product experience can rely on processed snapshots.

Required qualities:

- fast cache reuse;
- revision-aware stale handling;
- multiple camera views;
- Before/After;
- background refresh;
- no blank viewport during refresh.

### 17.3 Persistent preview worker

The preview worker should remain warm during an active editing session.

Potential preview scene:

```text
Preview Scene
├── Avatar Root
├── Camera
├── Lighting Rig
├── Neutral Background
└── candidate/prepared objects
```

The preview environment should be deterministic and separate from the user's arbitrary Scene View.

### 17.4 Live viewport target

Later, Atelier may stream frames from a Unity RenderTexture into the desktop UI.

A simple first prototype may use:

```text
Camera
→ RenderTexture
→ asynchronous GPU readback
→ background image encoding
→ local transport
→ Atelier viewport
```

Higher-performance video/shared-texture transport should only be introduced after measurement shows it is necessary.

### 17.5 Atomic preview swap

Do not expose partially constructed preview state.

Prepare candidate state off-screen/hidden, validate it, then atomically switch the visible preview.

If candidate preparation fails, preserve the last good preview.

---

## 18. Character Creator Features

Atelier should evolve toward a visual creator experience, but v1 should scope carefully.

### 18.1 Required v1 categories

- avatar/base selection;
- outfits;
- hair/accessories where discoverable;
- supported appearance parameters;
- currently active/worn items;
- snapshots/comparison;
- undo/history;
- integrations/status.

### 18.2 Appearance

The UI should prefer human-oriented controls over raw Unity fields.

Possible groupings:

- Face
- Eyes
- Hair
- Body
- Materials
- Shapes

Only expose mappings that Atelier can identify with sufficient confidence. Unknown raw fields can remain available through an advanced/native fallback.

### 18.3 Poses

Later preview presets may include:

- Neutral
- Arms Up
- Walking
- Sitting
- Hands on Hips

These help inspect clipping and fit.

### 18.4 Focus mode

Future direct manipulation may allow focusing a semantic region and exposing relevant controls.

This is not required for MVP.

---

## 19. Safety, Undo, and Recovery

### 19.1 Exact targets

Every persistent operation must identify the exact target, not rely on current Unity selection.

### 19.2 Revision checks

Mutations must validate that the target state has not changed unexpectedly since the operation was prepared.

### 19.3 Undo

Where Atelier promises Undo, the operation must restore the complete Atelier-supported state affected by that operation, not only visible scene objects.

### 19.4 Operation receipts

Durable operations should include:

- stable operation ID;
- target ID;
- requested revision;
- phase;
- status;
- structured warning/error;
- result revision;
- execution receipt where appropriate.

### 19.5 Crash/reload recovery

After disconnect/reload:

1. reconnect;
2. inspect receipts and actual state;
3. determine whether the operation applied;
4. retry only when safe/idempotent;
5. otherwise show **Needs Review**.

A timeout does not prove failure.

### 19.6 Desired-state preservation

If Unity fails to apply one requested component of a user's draft, Atelier should not discard the rest of the user's desired configuration.

Example:

> Jacket could not be applied. Draft preserved. Retry / Fix / Remove from Draft.

---

## 20. Caching

### 20.1 Cache categories

Atelier should distinguish:

- source Library;
- Atelier metadata/index cache;
- generated artifact cache;
- Unity's own `Library` import cache.

Do not attempt to replace Unity's internal import cache.

### 20.2 Cache key requirements

A preview artifact may depend on:

- source asset revisions;
- relevant dependencies;
- desired-state recipe;
- avatar/body settings;
- renderer/material state;
- adapter versions;
- camera/lighting/pose;
- processing environment;
- handler version.

Do not key complex previews only by outfit ID.

### 20.3 Invalidation

Prefer incremental invalidation.

Do not invalidate all avatar artifacts because one unrelated file changed.

Unknown adapter dependencies may require conservative invalidation.

---

## 21. Performance Strategy

### 21.1 Near-term philosophy

**Hide latency before aggressively optimizing Unity execution time.**

The near-term stack is:

- standalone responsive UI;
- cached state immediately;
- persistent worker during active sessions;
- asynchronous operation queue;
- desired-state model;
- coalescing;
- priority scheduling;
- debounced expensive work;
- speculative worker startup/prefetch;
- instrumentation.

### 21.2 Instrumentation

Record at least:

- operation type;
- queue wait;
- worker startup time;
- package/compiler reload time where observable;
- Unity execution time;
- import time where observable;
- first preview latency;
- snapshot render time;
- cache hit/miss;
- project size indicators;
- user-visible pending duration;
- retries/failures;
- worker resource usage.

### 21.3 Performance targets

Initial product targets should be treated as acceptance goals, not current measured claims.

Recommended:

- local UI interaction response: <100 ms p95;
- desired-state acceptance: <100 ms p95;
- durable operation acceptance into local queue: investigate if sustained >250 ms p95;
- cached artifact display: effectively immediate after page/data availability;
- no long-running Unity work on the desktop UI thread;
- interactive jobs prioritized above background/idle jobs.

### 21.4 Future optimization triggers

Only invest in aggressive project materialization when telemetry shows project scale/import visibility remains a major source of user-visible latency despite async hiding.

Possible later work:

- smaller managed project working set;
- prewarmed base environments;
- variant-level materialization;
- validated dependency closures;
- workspace cache GC;
- separate render/execution workers;
- shared GPU transport.

---

## 22. Build / Validation / Publish

### 22.1 Synchronization gate

Before a final build/publish operation:

- required Desired State must equal Confirmed State;
- required pending mutations must be complete;
- required validation must be current;
- unresolved errors must be surfaced.

### 22.2 Build UX

Build/publish is an acceptable blocking boundary because correctness matters.

The UI should explain phases rather than appear frozen.

Example:

```text
Preparing avatar
✓ Sync complete
✓ Packages ready
→ Running validation
○ Building
```

### 22.3 Native fallback

If a final authentication/upload flow requires native Unity/SDK interaction that Atelier does not safely automate, provide a clear handoff:

> Continue in Unity

Do not block the rest of Atelier on solving every upload path in v1.

---

## 23. Reporting and Diagnostics

Atelier should provide a user-visible Activity/Diagnostics surface.

Minimum information:

- worker state;
- pending operations;
- failed operations;
- package changes;
- current project/workspace;
- logs relevant to failures;
- retry/review controls.

Routine users should not see low-level operation IDs unless they expand diagnostics.

Object-level status is preferred:

> Black Hoodie — Applying…

rather than:

> Operation #1402 queued.

---

## 24. Onboarding

### 24.1 First launch

1. Detect supported Unity installations.
2. Explain that Unity is used in the background.
3. Let user register an existing project or create an Atelier-managed workspace.
4. Add/import an avatar.
5. Index Library source locations or imports.
6. Install Atelier Unity bridge/package as required.
7. Open the avatar workspace in Atelier.

### 24.2 Existing-project path

> Add Existing Project

Atelier scans without destructive conversion.

It should identify:

- Unity version;
- packages;
- candidate avatars/scenes;
- supported integrations;
- obvious compatibility issues.

### 24.3 Managed path

Future default:

> Create Avatar Workspace

Atelier provisions the controlled Unity environment automatically.

---

## 25. Distribution

Atelier is distributed as a standalone desktop application.

Supporting Unity components should be distributed separately or installed into workspaces through supported package-management paths.

Recommended conceptual components:

```text
Atelier Desktop
Atelier Core
Atelier Unity Bridge
Atelier Adapter SDK
First-party adapters
```

These may initially live in one repository and release pipeline. Separate repositories are not required merely to enforce module boundaries.

---

## 26. Technical Architecture

### 26.1 High-level architecture

```text
                    Atelier Desktop
                         │
       ┌─────────────────┼──────────────────┐
       │                 │                  │
     Library         Desired State       Activity
       │                 │                  │
       └─────────────────┼──────────────────┘
                         │
                    Atelier Core
        cache / jobs / workspaces / artifacts
                         │
              ┌──────────┴──────────┐
              │                     │
        Project Runtime         Adapter SDK
              │                     │
        vrc-get / lifecycle     first/community
              │                     │
              └──────────┬──────────┘
                         │
                   Unity Bridge
                         │
                 Background Unity
                         │
                Managed/Adapted Project
```

### 26.2 Suggested module boundaries

```text
apps/desktop/
core/
project-runtime/
adapters-sdk/
adapters/
unity/bridge/
unity/preview/
features/creator/
features/library/
protocol/
```

These are code boundaries, not separate services.

### 26.3 Core must remain Unity-agnostic where practical

The core should not contain VRChat/Unity types in shared models unless required at a clear adapter boundary.

Unity-specific external indexing/parsing belongs in a Unity-source adapter, not the generic core.

### 26.4 Transport

Use the simplest reliable local transport initially.

Requirements:

- local authentication/token;
- explicit protocol version;
- operation IDs;
- health/readiness;
- bounded payloads;
- reconnect support.

Do not introduce a distributed message broker for a local desktop product without demonstrated need.

### 26.5 Persistence

A local embedded database plus artifact files is sufficient initially.

The desktop host should own the database. Unity should communicate through the protocol rather than concurrently writing the same database.

---

## 27. Migration / Code Reuse Plan from Avatar Wardrobe

### Phase 1 — Extract reusable foundations

Reuse/refactor:

- Library/indexing;
- async command model;
- operation receipts/recovery;
- target identity/revisions;
- snapshot/artifact storage;
- photo history;
- worker bridge;
- appearance primitives;
- tests.

Acceptance:

- Atelier desktop can run independently of the Unity-hosted AW UI.

### Phase 2 — Establish Atelier domain model

Add:

- Workspace;
- Desired State;
- Confirmed State;
- Rendered State;
- ProjectRuntime;
- generic Operation/Artifact APIs.

Acceptance:

- no core API assumes the product is specifically “Wardrobe.”

### Phase 3 — Project/package management

Add:

- Unity discovery;
- project registration;
- vrc-get-backed package operations;
- bridge provisioning;
- integration dependency installation.

Acceptance:

- user can add a project and install a supported integration without leaving Atelier.

### Phase 4 — Character Creator shell

Add:

- avatar-first home;
- creator viewport;
- Library → avatar drag flow;
- active/worn state;
- appearance controls;
- snapshots and comparison;
- object-level pending/sync state.

Acceptance:

- a representative dressing/customization task requires no routine Unity UI interaction.

### Phase 5 — Background lifecycle

Add:

- worker manager;
- on-demand startup;
- keep-warm/idle shutdown;
- Unity handoff/reconciliation;
- package-reload recovery.

Acceptance:

- Atelier can be opened and used for Library/desired-state work with Unity closed.

### Phase 6 — Open adapter SDK

Extract first-party integrations to public API.

Acceptance:

- a sample external adapter compiles/installs without modifying Atelier source;
- first-party adapters use the same public API.

### Phase 7 — Performance-directed optimization

Only after telemetry.

Potential work:

- managed minimal workspaces;
- asset materializer;
- prewarmed base environment;
- live viewport optimization.

---

## 28. Release Phases

### Alpha

Focus:

- code extraction;
- desktop lifecycle;
- existing project registration;
- offline Library;
- worker bridge;
- snapshots;
- async desired/confirmed state;
- basic dressing/customization;
- one supported integration installation.

No promise of broad plugin compatibility.

### Beta

Add:

- stronger managed workspace lifecycle;
- integration SDK preview;
- package management polish;
- worker recovery across reloads;
- representative production-avatar validation;
- performance instrumentation;
- improved creator UX;
- broader snapshot refresh coverage.

### 1.0

Requires:

- stable migration/onboarding;
- representative avatar acceptance testing;
- fresh-install validation;
- reliable async recovery;
- documented support matrix;
- public adapter SDK;
- at least a small set of reviewed first-party adapters;
- build/validation handoff that users can complete reliably;
- clear unsupported-input behavior.

---

## 29. Validation and Test Requirements

### 29.1 Unit/component tests

Cover:

- desired-state transitions;
- coalescing;
- operation ordering;
- cache key construction;
- artifact stale rejection;
- manifest persistence;
- integration dependency resolution;
- exact target identity;
- retry rules;
- package mutation exclusivity.

### 29.2 Unity integration tests

Cover:

- bridge startup;
- worker reconnect;
- supported avatar loading;
- outfit install/replace/remove;
- Undo;
- snapshot capture;
- package reload;
- adapter activation;
- target revision conflict;
- editor handoff where testable.

### 29.3 Process-boundary tests

Must exercise real process interruptions:

- desktop host restart;
- Unity worker restart;
- assembly reload;
- package install reload;
- lost response after Unity applied mutation;
- duplicate retry;
- stale preview arriving late;
- worker crash during render;
- project manually opened while worker is active.

### 29.4 Performance tests

Measure on:

- clean small project;
- representative medium project;
- asset-heavy project;
- cold worker;
- warm worker;
- cached artifact;
- uncached render;
- package reload;
- minimized/unfocused Unity where relevant.

### 29.5 Usability tests

Representative tasks:

1. add avatar/project;
2. import/browse an outfit;
3. try it on;
4. replace color/variant;
5. remove one exact copy;
6. undo;
7. resolve a missing dependency;
8. install a supported integration;
9. open project in Unity;
10. return to Atelier;
11. reach a validated build handoff.

Record:

- Unity round trips;
- wrong-target actions;
- confusion about pending vs complete;
- time to first useful preview;
- recovery success;
- task completion.

---

## 30. Product Metrics

Primary product metrics:

- percentage of routine sessions completed without opening normal Unity;
- time to first useful avatar view;
- time to accepted desired-state change;
- percentage of Unity mutations performed asynchronously;
- median/p95 visible pending duration;
- snapshot cache hit rate;
- worker cold-start frequency per session;
- number of manual recovery events;
- operation failure rate;
- successful build/validation completion rate;
- average number of Unity round trips per common task.

Qualitative success:

> Users describe Atelier as a character/avatar tool, not as “another Unity plugin.”

---

## 31. Risks

### 31.1 Plugin compatibility

Some Unity plugins may rely on interactive Editor behavior or unsupported processing assumptions.

Mitigation:

- explicit support matrix;
- permissive-only first-party integration policy;
- public community adapters;
- preserve unknown creator components;
- clear fallback to Unity.

### 31.2 Desired-state divergence

Optimistic UI could mislead users if Atelier does not clearly show sync failures.

Mitigation:

- explicit pending/failed object state;
- Desired/Confirmed separation;
- never mark completed before receipt;
- preserve draft when apply fails.

### 31.3 Project corruption / duplicate execution

Retries after lost responses can apply a mutation twice.

Mitigation:

- stable IDs;
- revision checks;
- execution receipts;
- actual-state reconciliation;
- Needs Review state when ambiguous.

### 31.4 Overengineering managed workspaces

Aggressive asset materialization can become complex before proven necessary.

Mitigation:

- async-first strategy;
- telemetry;
- adapt existing projects initially;
- whole-product materialization fallback.

### 31.5 Cold Unity startup remains slow

Mitigation:

- cached UI and snapshots;
- speculative startup;
- keep-warm worker;
- background synchronization;
- eventually controlled managed projects if measured necessary.

### 31.6 Desktop ↔ Unity protocol churn

Mitigation:

- versioned public contract;
- small operation vocabulary;
- avoid leaking internal C# types;
- first-party adapters dogfood public API.

---

## 32. Open Product Questions

These decisions can remain open during early implementation:

1. Should the first Atelier release adapt existing projects by default or immediately create a new managed copy?
2. Should one workspace map to one avatar or permit multiple closely related avatars initially?
3. How much of the existing Avatar Wardrobe appearance/menu functionality should ship in Atelier v1 versus remain deferred?
4. What exact first-party adapters should ship at 1.0?
5. What package versions define the first supported managed environment?
6. Is live 3D viewport required for 1.0, or are fast snapshot-based previews sufficient?
7. What idle timeout should the Unity worker use?
8. Should generated state auto-sync continuously or offer an explicit Apply/Commit mode for some high-risk operations?
9. How should Atelier migrate existing Avatar Wardrobe Library metadata without coupling the products?
10. What licensing model should Atelier itself use? This is independent of the open adapter SDK policy.

---

## 33. Recommended MVP Scope

To avoid turning Atelier into a multi-year rewrite, the recommended MVP is deliberately narrower than the complete vision.

### Ship in MVP

- standalone desktop app;
- reuse existing AW Library/indexing where practical;
- avatar/workspace registration;
- existing Unity project support;
- Unity bridge provisioning;
- worker lifecycle manager;
- offline Library browsing;
- desired/confirmed/rendered state model;
- durable async mutations;
- coalescible preview state;
- drag-and-drop outfit/asset flow;
- exact replace/remove/undo;
- snapshot-based preview with history and Before/After;
- basic supported appearance controls;
- vrc-get-backed VPM installation/removal for Atelier integrations;
- at least one first-party permissive integration;
- Open in Unity handoff;
- diagnostics/activity;
- instrumentation.

### Explicitly defer

- aggressive symlink/materialized minimal projects;
- perfect dependency closure;
- universal avatar plugin support;
- universal clothing fitting;
- full Scene View replacement;
- custom third-party frontend panels;
- extension marketplace;
- cloud sync;
- multi-machine execution;
- sophisticated live WebRTC/shared-texture viewport;
- fully automated VRChat upload if native fallback is required.

---

## 34. MVP User Journey

```text
Install Atelier
    ↓
Register existing avatar project
    ↓
Atelier detects Unity + packages
    ↓
Avatar appears using cached/generated snapshot
    ↓
Browse Library
    ↓
Drag outfit onto avatar
    ↓
Desired state updates instantly
    ↓
Unity worker starts/wakes in background
    ↓
Outfit operation accepted
    ↓
User continues browsing/customizing
    ↓
Unity confirms outfit
    ↓
Snapshot refreshes
    ↓
User compares / keeps / replaces / undoes
    ↓
Optional integration required
    ↓
Install from Atelier via VPM runtime
    ↓
Package reload handled asynchronously
    ↓
Continue editing
    ↓
Validate / Build
    ↓
If required: Continue in Unity for unsupported final step
```

The success condition is not that Unity becomes instant. The success condition is that **Unity's slowness rarely interrupts the creative flow.**

---

## 35. Strategic Direction After MVP

If Atelier proves the interaction model, the long-term direction is:

> **Atelier becomes the user's primary avatar workspace, while Unity becomes a compatibility/compiler/render/build backend managed by Atelier.**

Potential later capabilities:

- Atelier-managed minimal workspaces;
- shared immutable asset store;
- active-working-set materialization;
- automatic workspace cache management;
- fast live Unity-rendered viewport;
- additional permissive integrations;
- external non-Unity adapters;
- richer appearance workflows;
- posing and clipping inspection;
- Android/platform preparation;
- visual optimization review;
- creator/commission review workflows;
- migration/import from other avatar-management tools.

The architecture should leave room for those capabilities without requiring them in the first release.

---

## 36. Product Definition in One Sentence

> **Atelier is a responsive desktop avatar creation workspace that manages Unity and its ecosystem in the background so users can customize avatars visually without making Unity their primary interface.**
