# AGENTS.md

Rules and context for AI agents working on this repository.

## Project context

*   **Platform: Virt-A-Mate (VaM).** This is neither a normal Unity project nor a normal
    C# project. VaM compiles the `.cs` files itself at plugin load using its embedded
    Mono compiler. The `.csproj` and `.sln` exist for IDE type resolution only — nothing
    they produce ships, and a green build proves nothing about whether VaM will accept
    the code.
*   **Target Unity 2018.1.9f2.** Verify every API against that version, not current
    documentation. Two that were assumed here and turned out wrong:
    *   `Texture2D.LoadRawTextureData` has only the `byte[]` overload; the
        `NativeArray<T>` one arrived later.
    *   `SystemInfo.supportsAsyncGPUReadback` does not exist — added in 2018.2.
*   **The runtime compiler is conservative.** C# 4/5 syntax. No string interpolation, no
    `?.`, no expression-bodied members. `LangVersion` is pinned to 5 in the `.csproj` so
    the IDE rejects these before VaM does.
*   **Multi-file plugins are supported** via a `.cslist` listing each `.cs` file, one
    relative path per line, in compile order. Do not collapse this into a single file.

## Repository layout

*   Repo root sits at `<VaM>/Custom/Scripts/FivelSystems/LiveStreamConnectorToOBS/`,
    four levels below the VaM install root, which is what makes the `.csproj`
    `../../../../VaM_Data/Managed` hint paths resolve. Override with `/p:VamDir=…` if
    cloned elsewhere.
*   The `.var` payload is the nested `Custom/` tree plus `meta.json`. The packaging
    workflow copies exactly those two and zips them.
*   `TODO.md`, `ROADMAP.md` and `checkpoints/` are gitignored — internal only. Do not
    commit them, and do not move their content into `README.md`.
*   `README.md` is public-facing. It documents behaviour and honest limitations. Fix
    plans, cost models and phase ordering belong in `ROADMAP.md`.

## Architecture

```text
  source camera
       │  (a) camera has a targetTexture  →  read that RT directly (free)
       │  (b) camera renders to screen    →  extra Camera.Render() into _outputRT
       │  (c) plugin-created camera       →  _outputRT (currently black)
       ▼
  AsyncGPUReadback.Request  ── or ──  ReadPixels   (Sync Capture)
       ▼
  sRGB LUT on the CPU          only when the source RT is Linear
       ▼
  LoadRawTextureData → Apply → EncodeToJPG        all on the main thread
       ▼
  HttpStreamServer.SubmitFrame(byte[])
       ▼
  TcpListener, one pooled thread per client
```

| File | Role |
| --- | --- |
| `…/LiveStreamConnectorToOBS.cslist` | Compile order. |
| `…/src/LiveStreamConnectorToOBS.cs` | `MVRScript`: UI, camera handling, capture pipeline. |
| `…/src/HttpStreamServer.cs` | HTTP/MJPEG server. No Unity references. |
| `…/src/CameraScanner.cs` | Scene camera enumeration and display names. |

## Hard rules

1.  **`HttpStreamServer` must never touch a Unity API.** It runs an accept thread plus
    one pooled worker per client. The plugin hands it finished `byte[]` JPEGs and reads
    back plain counters. Keep it that way. This is also why server-side work is the safe
    kind — it cannot reach the render pipeline.
2.  **Capture belongs in `LateUpdate`, not `WaitForEndOfFrame`.** The screen-only camera
    path calls `Camera.Render()`; issuing that from end-of-frame is a reentrant render
    from inside Unity's render loop.
3.  **Never destroy a RenderTexture with a readback pending.** That is an access
    violation — a hard process kill, not a catchable exception. `RebuildPipeline()` and
    `OnDestroy()` currently do exactly this; retiring textures to a list and releasing
    them only once their readback returns is the fix. Leaking a few MB beats killing the
    process.
4.  **Plugin reload runs `OnDestroy`.** Teardown paths are exercised constantly during
    development and must be the safest code in the repo.
5.  **One change per build.** An earlier rewrite landed four pipeline changes together
    (GPU blit, pipelined readbacks, altered camera handling, capture moved to
    `WaitForEndOfFrame`), crashed the host process twice, and was reverted with **no root
    cause established**. Nothing could be ruled out because nothing was isolated. It
    occurred in desktop mode, and threading was never involved. Land pipeline changes one
    at a time, in their own commit, so a crash can be bisected.
6.  **If the process dies, retrieve the log before touching anything:**
    `AppData\LocalLow\MeshedVR\VaM\output_log.txt`, or the `.dmp` beside the executable.
    A native stack in `d3d11.dll`/`UnityPlayer.dll` implicates the render side; a managed
    stack names the method outright.
7.  **Measure, do not reason.** The Status line reports game fps, stream fps, encode ms
    and re-render ms. Confirm which term dominates before changing anything, and confirm
    the change moved it afterwards.
8.  **Work on a branch, one phase per branch.** `feat/…`, `fix/…`, `perf/…`, with
    [Conventional Commits](https://www.conventionalcommits.org/). `main` is the
    known-good state to bisect against and to fall back to when VaM kills the process.
    Do not add zips to `checkpoints/` — git supersedes them. The two zips still there
    predate the repository and hold the only copy of the pre-fork `GGchan` namespace and
    the pre-`TcpListener` server; keep them, do not add more.

## VaM API notes

*   `JSONStorable*` is the user-facing surface. `Register*` is what persists it —
    creating a UI control alone saves nothing.
*   Logging: `SuperController.LogMessage()` / `SuperController.LogError()`. Do not log
    per frame.
*   Atoms are reached via `transform.root.GetComponent<Atom>()`.
*   `MeshVR` and `SimpleJSON` come from `Assembly-CSharp.dll`.
*   `Texture2D.EncodeToJPG` lives in `UnityEngine.ImageConversionModule`, not
    `CoreModule`.

## Coding standards

*   **Naming:** JetBrains Rider C# conventions. Private fields `_camelCase`, statics
    `s_camelCase`, constants `SCREAMING_CASE`. Names must be self-explanatory.
*   **Encapsulation:** fields private; expose behaviour, not state.
*   **Safety:** null-check `Atom`, `Camera`, `RenderTexture` and the server before use.
    Teardown must be idempotent.
*   **Comments explain why, not what.** Match the existing density — the codebase
    annotates non-obvious trade-offs and leaves plain code unannotated.

## Documentation rules

1.  **README.md** must stay public-release clean, in standard Markdown, and must include
    a `<details>` block with the **BBCode** version for the VaM Hub. It must credit
    **MahiroOyama** as the original author.
2.  **Versioning.** Tag format is `v` followed by a single integer — `v1`, `v2`, `v3`.
    **No SemVer.** The packaging workflow strips the `v` and uses the number directly,
    because VaM's package system is integer-versioned.
3.  **CHANGELOG.md** follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).
    Update it with every notable change.
4.  **Licence.** CC BY 4.0, inherited from MahiroOyama's original. Attribution is a
    licence condition, not a courtesy — it must survive in `LICENSE`, `README.md` and
    `meta.json`.
