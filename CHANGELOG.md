# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

Versions are single integers, matching VaM's package versioning. This fork restarts at
`v1` under the `FivelSystems` creator name; it is not a continuation of MahiroOyama's
version numbering.

## [Unreleased]

### Added

*   **Threaded JPEG encoding**, always on for the async capture path. A built-in
    baseline encoder (4:2:0, Annex K tables, AAN DCT) runs on a worker thread, so the
    main thread pays for a buffer copy rather than a full encode. `Texture2D.EncodeToJPG`
    is a Unity object method and must run on the main thread, where it dominated the
    streaming cost at 8–15 ms per frame at 720p. This is not a toggle: it is how
    encoding works.
*   **Flip Output Vertically** toggle, on by default. Readback data starts at the bottom
    row while JPEG scanlines run top-down; `EncodeToJPG` handled this implicitly, the
    threaded encoder needs it stated. Persists with the scene.

### Changed

*   **Capture rate is clamped to the game's own framerate cap**, discovered at runtime
    from `QualitySettings.vSyncCount` and the display refresh rate, falling back to
    `Application.targetFrameRate`. Nothing is hardcoded, and the cap is re-read once a
    second so changing it mid-session takes effect. Every capture costs a GPU readback
    and, on the manual-render path, a second scene render, so capturing faster than the
    game was allowed to run spent GPU the user had asked not to spend. The Status line
    now reports the detected cap beside the game framerate.
*   **Capture is skipped entirely with no clients connected.** The readback, gamma pass
    and encode previously ran regardless, so enabling the plugin cost framerate for
    output nobody was receiving. A readback already in flight is still retired.
*   The Status line reports main-thread and worker time separately — `main` and `jpeg`
    replace the single `encode` figure — and appends a dropped-frame count when the
    worker cannot keep up with the capture rate.
*   Readback dimensions are taken from the source RenderTexture rather than assumed to
    match the configured Width/Height, so a mismatch no longer garbles the stream.

### Fixed

*   **Sync Capture no longer feeds the worker thread.** Doing so called
    `Texture2D.GetRawTextureData()` every frame, which returns a freshly allocated array
    on this Unity build -- there is no `NativeArray` overload before 2018.2. At a
    600x1400 source that is 3.4 MB per frame onto the large object heap, which Mono does
    not compact; sustained, it exhausted the address space and killed the host with
    `VirtualAllocRemap failed`. Sync Capture encodes on the main thread again, as it did
    before. The async path is unaffected: it copies into pooled buffers and allocates
    nothing per frame.
*   `Capture runs even with zero clients connected` is resolved; removed from the known
    limitations.

## [v1] — 2026-08-19

First release of the FivelSystems fork, based on
`MahiroOyama.LiveStreamConnectorToOBS.2`.

### Added

*   **Allow Network Access** toggle. Binds all interfaces instead of loopback only, so
    the stream is reachable from other devices. Persists with the scene.
*   **Access Key** field. When set, every request must carry `?key=…`; anything else is
    answered 403. Applies to both the MJPEG stream and the built-in viewer page, and is
    folded into the generated OBS URL automatically. Persists with the scene.
*   **Sync Capture** toggle. Replaces `AsyncGPUReadback` with `Texture2D.ReadPixels`,
    lifting throughput from `game fps ÷ 2–3` to `min(Target FPS, game fps)` at near-zero
    latency, in exchange for a GPU stall that costs game framerate. Persists with the
    scene.
*   **Live diagnostics** in the Status line, refreshed once a second: game fps, stream
    fps, main-thread encode ms, scene re-render ms, and connected client count. Replaces
    the previous "Waiting for client" / "Streaming" text.
*   **LAN-aware OBS URL.** With network access on, the URL field reports a routable host
    address rather than `localhost`, preferring a CGNAT-range address where one exists.
*   Request timeouts, `NoDelay`, and a bounded send buffer on each client socket, so a
    slow link drops frames instead of queueing them.

### Changed

*   **HTTP server rewritten from `HttpListener` onto a raw `TcpListener`.** On Windows,
    `HttpListener` is a shim over the kernel HTTP stack, which routes by Host header and
    refuses any non-loopback prefix without elevation or an administrator-registered URL
    ACL. Binding the socket directly removes that requirement entirely and exposes the
    socket options the kernel stack hides. Request parsing, routing and response writing
    are now handled in-plugin.
*   `MAX_CLIENTS` (4) is now actually enforced; excess clients receive 503. It was a dead
    constant before.
*   `SO_REUSEADDR` is set on the listener, so a rebuild does not fail on a socket still
    in `TIME_WAIT`.
*   Frame timing now accumulates before the in-flight readback early-return, so readback
    latency overlaps the frame budget instead of stacking on top of it.
*   Readback consumption uses a bulk `CopyTo` when lengths match, instead of a per-byte
    loop.
*   The gamma LUT is applied in place and skips the alpha channel.

### Fixed

*   Status text no longer writes to the VaM log on every change; the once-a-second
    diagnostics update bypasses the logging path.

### Known limitations

Carried over or not yet addressed — see the README for detail.

*   Listener is IPv4 only.
*   `-- Create New Camera --` streams black; the created camera is never rendered.
*   Dragging Width, Height or Port restarts the server on every frame of the drag,
    dropping connected clients.
*   Only Sync Capture, Flip Output Vertically, Allow Network Access and Access Key
    persist with the scene.
*   **Sync Capture with a linear source RenderTexture still allocates per frame** via the
    same `GetRawTextureData()` gamma path, which predates this release. It is skipped
    for sRGB sources, which is the common case, but it is the same hazard.
*   `RebuildPipeline()` and `OnDestroy()` release the RenderTexture without checking for
    a pending readback.
