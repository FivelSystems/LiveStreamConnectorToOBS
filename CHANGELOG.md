# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

Versions are single integers, matching VaM's package versioning. This fork restarts at
`v1` under the `FivelSystems` creator name; it is not a continuation of MahiroOyama's
version numbering.

## [Unreleased]

Nothing yet.

## [v2] — 2026-08-19

### Added

*   **Threaded JPEG encoding.** A built-in baseline encoder (4:2:0, Annex K tables, AAN
    DCT) runs on a worker thread, so the main thread pays for a buffer copy rather than a
    full encode. Measured in VaM at a 600x1400 source this gives 30 fps against 20 fps
    for `Texture2D.EncodeToJPG` on the main thread. It does not match the throughput of
    the earlier sync-capture-plus-worker configuration, which was removed because it
    exhausted the heap.
*   **Flip Output Vertically** toggle, on by default. Readback data starts at the bottom
    row while JPEG scanlines run top-down; `EncodeToJPG` handled this implicitly, the
    threaded encoder needs it stated. Persists with the scene.
*   **Width, Height, JPEG Quality and Target FPS persist with the scene, and nothing
    overwrites them.** Selecting a camera used to assign Width and Height from the source
    resolution, which discarded any downscale on every scene load since restoring a scene
    re-selects the camera. Width is now only clamped down to the source when it exceeds
    it. Changing Width suggests a Height that preserves the source aspect, so the
    downscale does not stretch by accident, but Height remains yours to override.
*   **GPU downscale.** Width and Height now do something for a camera that already has
    a targetTexture: the source is blitted into the output RenderTexture before readback.
    Every downstream cost is linear in pixel count, so halving each axis quarters the
    readback bandwidth, the copy and the encode together. Height is derived from Width
    and the source aspect, so the blit cannot stretch the image.

### Removed

*   **Sync Capture.** It existed to beat the single-readback throughput ceiling by
    stalling the GPU, paying game framerate for stream framerate. Queued readbacks reach
    the same throughput without the stall, and after the allocation fix the toggle could
    only cost framerate: it forced `EncodeToJPG` back onto the main thread. With it goes
    the last main-thread encode, the intermediate `Texture2D`, and `EncodeToJPG` itself.

### Changed

*   **Up to three readbacks are queued** rather than one at a time. A single request
    takes two to three frames to return, which capped throughput near `game fps / 3`
    regardless of Target FPS. Requests complete in submission order, so the queue drains
    from the head, and each carries the width, height and colour space it was taken at.

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
*   **The server copies frames in and out under its lock**, so a frame buffer is never
    read while it is being replaced, and nothing on the capture path allocates per frame.
*   Readback dimensions are taken from the source RenderTexture rather than assumed to
    match the configured Width/Height, so a mismatch no longer garbles the stream.

### Fixed

*   **RenderTextures are released only once their readback has returned.**
    `RebuildPipeline()` and `OnDestroy()` freed `_outputRT` with a request possibly still
    pointing at it, which is an access violation rather than an exception, on the two
    paths exercised most during development: resolution change and plugin reload. At
    teardown anything still pending is left alone; leaking a few MB beats killing the
    host.

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
*   `RebuildPipeline()` and `OnDestroy()` release the RenderTexture without checking for
    a pending readback.
