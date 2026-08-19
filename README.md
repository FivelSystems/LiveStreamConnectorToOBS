# LiveStreamConnectorToOBS for Virt-A-Mate

![License](https://img.shields.io/badge/License-CC_BY_4.0-lightgrey.svg)
![Version](https://img.shields.io/badge/Version-v1-blue.svg)
[![Support](https://img.shields.io/badge/Support-Buy_Me_A_Coffee-orange.svg)](https://buymeacoffee.com/fivelsystems)

Stream any VaM camera as **MJPEG over HTTP**. No Spout, no NDI, no native DLLs — just a
URL that any browser or OBS Browser Source can open.

A maintained fork of [**LiveStreamConnectorToOBS** by MahiroOyama](https://hub.virtamate.com/resources/livestreamconnectortoobs.67984/)
(CC BY), rebuilt around a raw `TcpListener` so the stream can reach other devices on
your network, with an access key, a throughput toggle, and live diagnostics.

## ✨ What this fork adds

*   🌐 **Network access** — bind all interfaces instead of loopback only, so a phone,
    tablet or second PC can view the stream. No elevation, no `netsh http add urlacl`.
*   🔑 **Access key** — optional shared secret required as `?key=…`; anything else gets 403.
*   🔽 **GPU downscale** — stream smaller than the source camera. Every cost scales with
    pixel count, so this is the strongest dial available.
*   🧵 **Threaded JPEG encoding** — the encode runs on a worker thread instead of the
    main thread, so streaming costs a buffer copy rather than a full encode.
*   💤 **No cost with no viewers** — capture is skipped entirely while nothing is
    connected.
*   📊 **Live diagnostics** — game fps, stream fps, encode ms, re-render ms and client
    count, refreshed once a second, so you can see which cost dominates before changing
    anything.
*   🛡️ **Hardened server** — enforced 4-client cap, request timeouts, bounded send
    buffers, so a slow viewer drops frames instead of stalling the stream.

## 🚀 Installation

1.  Download `FivelSystems.LiveStreamConnectorToOBS.<n>.var` from
    [Releases](https://github.com/FivelSystems/LiveStreamConnectorFix/releases).
2.  Drop it into your `AddonPackages` folder and start VaM.
3.  Select any atom → **Plugins** → **Add Plugin** → `LiveStreamConnectorToOBS.cslist`.

## 🎮 Usage

1.  **Pick a source camera.** Entries are labelled with their resolution and parent atom.
2.  **Copy the URL.** The **OBS URL** field always shows the correct URL for the current
    settings, including host and access key. Default `http://localhost:8088/stream`.
3.  **In OBS:** add a Browser Source, paste the URL, set the source Width and Height to
    match the plugin's, and untick *Shutdown source when not visible*.

Opening the bare URL (`http://localhost:8088/`) in a browser serves a plain viewer page,
which is the quickest way to confirm the stream works before touching OBS.

### Choosing a source camera

| Selection | Cost |
| --- | --- |
| A camera that already has a RenderTexture (WindowCamera, CUA phone screen) | **Free.** It was already drawn this frame; the plugin just reads it. |
| A camera without one (the main view) | **Costly.** The scene is rendered a second time per capture. |
| `-- Create New Camera --` | Currently streams black — see [Known limitations](#-known-limitations). |

If the `render` figure in the Status line is above a few ms, you picked the second row.

## 📊 Reading the Status line

```text
game 58 fps  |  stream 19.4 fps  |  main 1.1 ms  |  jpeg 10.4 ms  |  render 0.0 ms  |  1 client
```

| Field | Meaning |
| --- | --- |
| `game` | Frames VaM ran. |
| `stream` | Frames actually encoded and sent. The real output rate. |
| `main` | Main-thread ms per capture. This is the only figure that costs you framerate. |
| `jpeg` | Worker-thread ms for the last encode. Off the frame budget. |
| `render` | Main-thread ms per capture spent re-rendering the scene. |
| `dropped` | Frames superseded before the worker reached them. Only shown when non-zero. |

| Symptom | Cause | Fix |
| --- | --- | --- |
| `render` > 5 ms | Source camera has no RenderTexture. | Pick one that has. |
| `main` > 5 ms | The buffer copy is large. | Lower Width/Height. |
| `jpeg` > 30 ms | Worker cannot keep up with Target FPS. | Lower Width/Height or Target FPS. |
| `dropped` climbing steadily | Capturing faster than the worker encodes. | Lower Target FPS. |
| `stream` hits Target FPS but looks choppy | Not the plugin. | Client-side decode or display rate. |

## ⚡ Framerate

`AsyncGPUReadback` never stalls the GPU, but only one request is in flight at a time and
each takes 2–3 frames to return, so throughput is about `game fps ÷ 2–3`. **Raising
Target FPS above that ceiling does nothing.**

**Threaded Encode** (on by default) runs the JPEG encode on a worker thread using a
built-in encoder, leaving the main thread with only a buffer copy. Turning it off falls
back to Unity's `EncodeToJPG` on the main thread, which is what the `main` figure will
then report. The built-in encoder produces files within a few percent of Unity's at the
same quality setting.

If the worker is still busy when the next frame is captured, that frame is dropped rather
than queued — the stream always shows the newest frame, never a backlog.

**Bandwidth and cost dials, in order of effect:** Width/Height (halving to 960×540 cuts
both the copy and the encode by more than half), then JPEG Quality (75 → 45 saves a great
deal for little visible loss), then Target FPS.

## 🌐 Network access and security

**Allow Network Access** off (the default) binds loopback only. On, it binds every
interface, making the stream reachable from other devices — **a live view of your scene,
served to anything that can route to the host.** An inbound firewall rule for the port
may also be required.

**Access Key**: leave it empty and no key is required. Set it and every request must
carry `?key=…`. The generated OBS URL and the built-in viewer page both include it
automatically. It is a query parameter over plain HTTP: adequate over an
already-encrypted transport, readable otherwise, and it lands in browser history. Put a
reverse proxy in front of the port if that matters.

At most 4 concurrent stream clients are accepted; further requests get 503.

## 🎛️ UI reference

| Control | Notes |
| --- | --- |
| Enable Streaming | Off stops the server. |
| Threaded Encode | On by default. Off reverts to `EncodeToJPG` on the main thread. Persists. |
| Flip Output Vertically | On by default. Turn off only if the stream arrives upside down. Persists. |
| Allow Network Access | Off = loopback only. On = all interfaces. Persists. |
| Access Key | Empty = open. Otherwise required as `?key=…`. Persists. |
| Port | Default 8088. Changing it restarts the server. |
| Source Camera | See above. Refresh with the button below it. |
| Width / Height | Stream resolution. Changing these restarts the server. |
| Target FPS | A ceiling, not a guarantee. |
| JPEG Quality | Applied live, no restart. The most effective bandwidth dial. |
| OBS URL | Read-only. The correct URL for current settings. |
| Status | Diagnostics, refreshed once a second. |

Flip Output Vertically, Allow Network Access and Access Key save with the scene. The
remaining controls currently do not.

## ⚠️ Known limitations

*   **IPv4 only.** The listener binds `IPAddress.Any`; clients reaching the host over
    IPv6 will not connect.
*   **`-- Create New Camera --` streams black.** The created camera is never rendered.
    Pick an existing camera instead.
*   **Dragging Width, Height or Port restarts the server on every frame of the drag**,
    which drops connected clients. Set the value, then reconnect OBS.
*   **Only Flip Output Vertically, Allow Network Access and Access Key persist** with
    the scene.
*   **Streaming still costs framerate.** The encode runs on a worker thread, but at a
    large source resolution it can still fail to keep up and will contend for CPU. Lower
    Width to downscale on the GPU before anything else.

## 🤝 Credits

*   **[MahiroOyama](https://hub.virtamate.com/members/mahirooyama.27972/)** — original
    [LiveStreamConnectorToOBS](https://hub.virtamate.com/resources/livestreamconnectortoobs.67984/)
    (CC BY). The capture pipeline and camera scanner are theirs.
*   **FivelSystems** — TcpListener server rewrite, network access, access key, Sync
    Capture, diagnostics.
*   **MeshedVR** — the MVRScript plugin framework.

## ❤️ Support

If you find this useful, consider buying me a coffee. ☕

<a href="https://buymeacoffee.com/fivelsystems" target="_blank"><img src="https://cdn.buymeacoffee.com/buttons/default-orange.png" alt="Buy Me A Coffee" height="41" width="174"></a>

## 📜 License

**CC BY 4.0**, inherited from the original work. See [LICENSE](LICENSE).

---

<details>
<summary><b>VaM Hub BBCode (Click to Expand)</b></summary>

[size=5][b]LiveStreamConnectorToOBS for Virt-A-Mate[/b][/size]

Stream any VaM camera as [b]MJPEG over HTTP[/b]. No Spout, no NDI, no native DLLs -- just a URL that any browser or OBS Browser Source can open.

A maintained fork of [url=https://hub.virtamate.com/resources/livestreamconnectortoobs.67984/]LiveStreamConnectorToOBS by MahiroOyama[/url] (CC BY).

[size=4][b]What this fork adds[/b][/size]
[list]
[*] [b]Network access[/b]: bind all interfaces instead of loopback only, so a phone or second PC can view the stream. No elevation, no URL ACL.
[*] [b]Access key[/b]: optional shared secret required as ?key=... ; anything else gets 403.
[*] [b]GPU downscale[/b]: stream smaller than the source camera. Every cost scales with pixel count, so this is the strongest dial available.
[*] [b]Threaded JPEG encoding[/b]: the encode runs on a worker thread, so streaming costs a buffer copy rather than a full encode.
[*] [b]No cost with no viewers[/b]: capture is skipped entirely while nothing is connected.
[*] [b]Live diagnostics[/b]: game fps, stream fps, main-thread ms, worker ms, re-render ms and client count, once a second.
[*] [b]Hardened server[/b]: 4-client cap, request timeouts, bounded send buffers.
[/list]

[size=4][b]Setup[/b][/size]
[list=1]
[*] Add the plugin to any atom.
[*] Pick a Source Camera. One that already owns a render texture (WindowCamera, CUA phone screen) is free to capture.
[*] Copy the URL from the [b]OBS URL[/b] field.
[*] In OBS: Browser Source, paste the URL, match Width/Height, untick "Shutdown source when not visible".
[/list]

[size=4][b]Tuning[/b][/size]
Read the Status line before changing anything. High [i]render[/i] means your source camera has no render texture -- pick another. High [i]main[/i] means lower Width/Height. High [i]jpeg[/i] means the worker cannot keep up -- lower Width/Height or Target FPS. Target FPS is a ceiling, not a guarantee.

[size=4][b]Known limitations[/b][/size]
[list]
[*] IPv4 only.
[*] "Create New Camera" streams black -- pick an existing camera.
[*] Dragging Width/Height/Port restarts the server and drops clients.
[/list]

[size=4][b]Credits[/b][/size]
[list]
[*] [b]MahiroOyama[/b]: original plugin, capture pipeline and camera scanner (CC BY).
[*] [b]FivelSystems[/b]: server rewrite, network access, access key, diagnostics.
[/list]

[size=4][b]Support[/b][/size]
If you find this useful, consider buying me a coffee. [url=https://buymeacoffee.com/fivelsystems]buymeacoffee.com/fivelsystems[/url]

[size=4][b]License[/b][/size]
[b]CC BY 4.0[/b], inherited from the original work.

</details>
