using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using UnityEngine.UI;
using MeshVR;
using SimpleJSON;

namespace FivelSystems.LiveStreamConnectorToOBS
{
    public class LiveStreamConnectorToOBS : MVRScript
    {
        private const int DEFAULT_WIDTH = 1280;
        private const int DEFAULT_HEIGHT = 720;
        private const int RT_DEPTH = 24;
        private const int WORST_HOST_RANK = 4;
        private const float TEXT_INPUT_HEIGHT = 50f;
        private const float RESOLVE_RETRY_SECONDS = 1f;
        private const float RESOLVE_WINDOW_SECONDS = 20f;
        private const string CREATE_NEW_KEY = "__create_new__";
        private const int DEFAULT_PORT = 8088;
        private const int DEFAULT_JPEG_QUALITY = 75;
        private const int DEFAULT_FPS = 30;

        private const int MAX_READBACKS_IN_FLIGHT = 3;

        private UIDynamicToggle _enableToggle;
        private UIDynamicToggle _flipToggle;
        private UIDynamicToggle _networkToggle;
        private UIDynamicTextField _accessKeyField;
        private UIDynamicTextField _portField;
        private UIDynamicSlider _widthSlider;
        private UIDynamicSlider _heightSlider;
        private UIDynamicSlider _qualitySlider;
        private UIDynamicSlider _fpsSlider;
        private UIDynamicPopup _cameraPopup;
        private UIDynamicButton _refreshButton;
        private UIDynamicTextField _statusText;
        private UIDynamicTextField _urlText;
        private UIDynamicTextField _addressesText;

        private readonly List<CameraInfo> _sceneCameras = new List<CameraInfo>();
        private readonly List<string> _popupKeys = new List<string>();
        private readonly List<string> _popupLabels = new List<string>();

        // Set when a restored selection names a camera the scene has not loaded yet.
        private string _pendingCameraKey;
        private float _resolveTimer;
        private float _resolveElapsed;
        private readonly JSONStorableStringChooser _cameraChooser = new JSONStorableStringChooser("SourceCamera", new List<string>(), "", "Source Camera");
        private readonly JSONStorableString _statusStorable = new JSONStorableString("Status", "");
        private readonly JSONStorableString _portStorable = new JSONStorableString("Port", "" + DEFAULT_PORT);
        private readonly JSONStorableString _urlStorable = new JSONStorableString("OBS URL", "");
        private readonly JSONStorableString _addressesStorable =
            new JSONStorableString("All Addresses", "");
        private readonly JSONStorableBool _enableStorable = new JSONStorableBool("Enable Streaming", true);
        private readonly JSONStorableBool _flipStorable = new JSONStorableBool("Flip Output Vertically", true);
        private readonly JSONStorableBool _networkStorable = new JSONStorableBool("Allow Network Access", false);
        private readonly JSONStorableString _accessKeyStorable = new JSONStorableString("Access Key", "");
        private readonly JSONStorableFloat _widthStorable = new JSONStorableFloat("Width", DEFAULT_WIDTH, 320f, 3840f, false);
        private readonly JSONStorableFloat _heightStorable = new JSONStorableFloat("Height", DEFAULT_HEIGHT, 240f, 2160f, false);
        private readonly JSONStorableFloat _qualityStorable = new JSONStorableFloat("JPEG Quality", DEFAULT_JPEG_QUALITY, 10f, 100f, false);
        private readonly JSONStorableFloat _fpsStorable = new JSONStorableFloat("Target FPS", DEFAULT_FPS, 5f, 60f, false);

        private Camera _sourceCamera;
        private GameObject _camContainer;
        private RenderTexture _outputRT;
        private readonly List<RenderTexture> _retiredTextures = new List<RenderTexture>();
        private HttpStreamServer _server;
        private bool _sourceIsCreated;
        private bool _needsRebuild;
        private float _frameBudget;
        private float _frameTimer;
        private string _lastStatus = "";
        private readonly Queue<PendingReadback> _readbacks = new Queue<PendingReadback>();
        private int _gameFpsCap;
        private JpegEncodeWorker _worker;
        private static byte[] s_srgbLUT;

        // Diagnostics counters.
        private int _statFrames;
        private int _statCaptures;
        private float _statWindowStart;
        private float _statConsumeMs;
        private float _statRenderMs;

        private struct PendingReadback
        {
            public UnityEngine.Experimental.Rendering.AsyncGPUReadbackRequest Request;
            public int Width;
            public int Height;
            public bool IsSRGB;
        }

        public override void Init()
        {
            try
            {
                EnsureSRGBLUT();
                BuildUI();
                RescanCameras();
                RebuildPipeline();
            }
            catch (Exception e)
            {
                SuperController.LogError("LiveStreamConnectorToOBS.Init: " + e);
            }
        }

        private void BuildUI()
        {
            _enableToggle = CreateToggle(_enableStorable);
            _enableStorable.setCallbackFunction = v =>
            {
                if (v) RebuildPipeline();
                else StopStreaming();
            };

            // Readback data starts at the bottom row; JPEG scanlines run top-down.
            _flipToggle = CreateToggle(_flipStorable);
            RegisterBool(_flipStorable);

            // Off = loopback only. On = bind all interfaces.
            _networkToggle = CreateToggle(_networkStorable);
            RegisterBool(_networkStorable);
            _networkStorable.setCallbackFunction = v => { _needsRebuild = true; };

            // Empty = no key required; otherwise callers must pass ?key=...
            _accessKeyField = CreateEditableTextField(_accessKeyStorable);
            RegisterString(_accessKeyStorable);
            _accessKeyStorable.setCallbackFunction = v => { _needsRebuild = true; };

            _portField = CreateEditableTextField(_portStorable);
            RegisterString(_portStorable);
            _portStorable.setCallbackFunction = v => { _needsRebuild = true; };

            _cameraPopup = CreatePopup(_cameraChooser, false);
            RegisterStringChooser(_cameraChooser);
            _cameraChooser.setCallbackFunction = OnCameraSelected;

            _refreshButton = CreateButton("Refresh Camera List", true);
            _refreshButton.button.onClick.AddListener(RescanCameras);

            _widthSlider = CreateSlider(_widthStorable);
            RegisterFloat(_widthStorable);
            _widthStorable.setCallbackFunction = v => { _needsRebuild = true; };

            _heightSlider = CreateSlider(_heightStorable);
            RegisterFloat(_heightStorable);
            _heightStorable.setCallbackFunction = v => { _needsRebuild = true; };

            _qualitySlider = CreateSlider(_qualityStorable);
            RegisterFloat(_qualityStorable);
            _qualityStorable.setCallbackFunction = v =>
            {
                if (_server != null) _server.JpegQuality = Mathf.RoundToInt(v);
            };

            _fpsSlider = CreateSlider(_fpsStorable);
            RegisterFloat(_fpsStorable);
            _fpsStorable.setCallbackFunction = v => { ApplyFrameBudget(); };

            _urlText = CreateTextField(_urlStorable);
            _addressesText = CreateTextField(_addressesStorable);

            _statusText = CreateTextField(_statusStorable);
        }

        /// <summary>
        /// CreateTextField only displays a value; it has no way to accept typing, which
        /// left Access Key and Port permanently read-only. An InputField grafted onto the
        /// same object and handed to the storable is what routes typed characters into
        /// val and fires setCallbackFunction.
        /// </summary>
        private UIDynamicTextField CreateEditableTextField(JSONStorableString storable)
        {
            UIDynamicTextField field = CreateTextField(storable);
            field.height = TEXT_INPUT_HEIGHT;

            InputField input = field.gameObject.AddComponent<InputField>();
            input.textComponent = field.UItext;
            storable.inputField = input;
            return field;
        }

        private void RescanCameras()
        {
            _sceneCameras.Clear();
            _popupKeys.Clear();
            _popupLabels.Clear();
            var found = CameraScanner.Scan(c => c.name != "__SpoutSourceCam");
            _sceneCameras.AddRange(found);

            _popupKeys.Add(CREATE_NEW_KEY);
            _popupLabels.Add("-- Create New Camera --");
            for (int i = 0; i < _sceneCameras.Count; i++)
            {
                // Two cameras can share a name under one atom; keep keys unique so a
                // saved selection resolves to exactly one of them.
                string key = _sceneCameras[i].Key;
                if (_popupKeys.Contains(key)) key = key + "#" + i;

                _popupKeys.Add(key);
                _popupLabels.Add(_sceneCameras[i].DisplayName);
            }

            // The popup shows labels; the scene stores keys.
            _cameraChooser.choices = new List<string>(_popupKeys);
            _cameraChooser.displayChoices = new List<string>(_popupLabels);
            if (string.IsNullOrEmpty(_cameraChooser.val) && _popupKeys.Count > 0)
                _cameraChooser.val = _popupKeys[0];
        }

        private void OnCameraSelected(string val)
        {
            if (string.IsNullOrEmpty(val)) return;

            int index = _popupKeys.IndexOf(val);
            if (index < 0)
            {
                // Almost always a scene still loading rather than a bad value, so hold
                // the key and keep looking instead of tearing down what is running.
                _pendingCameraKey = val;
                _resolveTimer = 0f;
                _resolveElapsed = 0f;
                SetStatus("Waiting for camera: " + val);
                return;
            }

            TeardownCamera();
            if (index == 0) CreateNewSourceCamera();
            else AdoptSourceCamera(_sceneCameras[index - 1].Camera);
        }

        private void CreateNewSourceCamera()
        {
            _camContainer = new GameObject("__SpoutSourceCam");
            _camContainer.transform.SetParent(transform, false);
            _sourceCamera = _camContainer.AddComponent<Camera>();
            _sourceCamera.clearFlags = CameraClearFlags.Skybox;
            _sourceCamera.fieldOfView = 60f;
            _sourceCamera.nearClipPlane = 0.05f;
            _sourceCamera.farClipPlane = 5000f;
            _sourceCamera.enabled = false;
            _sourceIsCreated = true;
            SetStatus("Created new camera on plugin object");
        }

        /// <summary>
        /// Fills the target without distorting the source, by sampling the largest
        /// centred region of it that already has the target's aspect ratio. A plain Blit
        /// stretches to fit; this scales the source UVs so the excess is cropped instead.
        /// </summary>
        private static void BlitPreservingAspect(RenderTexture source, RenderTexture target)
        {
            float sourceAspect = (float)source.width / source.height;
            float targetAspect = (float)target.width / target.height;

            Vector2 scale;
            Vector2 offset;
            if (targetAspect > sourceAspect)
            {
                float fraction = sourceAspect / targetAspect;
                scale = new Vector2(1f, fraction);
                offset = new Vector2(0f, (1f - fraction) * 0.5f);
            }
            else
            {
                float fraction = targetAspect / sourceAspect;
                scale = new Vector2(fraction, 1f);
                offset = new Vector2((1f - fraction) * 0.5f, 0f);
            }

            Graphics.Blit(source, target, scale, offset);
        }

        private void AdoptSourceCamera(Camera cam)
        {
            _sourceCamera = cam;
            _sourceIsCreated = false;
            if (cam.targetTexture != null)
            {
                // Only clamp: overwriting would discard the user's downscale on every
                // load, since selecting a camera runs whenever a scene restores.
                if (_widthStorable.val > cam.targetTexture.width)
                    _widthStorable.valNoCallback = cam.targetTexture.width;
                _needsRebuild = true;
                SetStatus("Using: " + cam.name + " (" + cam.targetTexture.width + "x" + cam.targetTexture.height
                          + ") -- lower Width to stream smaller than the source");
            }
            else
            {
                SetStatus("Using: " + cam.name + " (no targetTexture, will manual-render)");
            }
        }

        private void TeardownCamera()
        {
            if (_sourceIsCreated && _camContainer != null)
            {
                Destroy(_camContainer);
            }
            _sourceCamera = null;
            _camContainer = null;
            _sourceIsCreated = false;
        }

        private void RebuildPipeline()
        {
            int w = Mathf.RoundToInt(_widthStorable.val);
            int h = Mathf.RoundToInt(_heightStorable.val);
            int port;
            int.TryParse(_portStorable.val, out port);
            if (port <= 0 || port > 65535) port = DEFAULT_PORT;
            int quality = Mathf.RoundToInt(_qualityStorable.val);
            if (quality < 10) quality = 10;
            if (quality > 100) quality = 100;
            ApplyFrameBudget();
            _frameTimer = 0f;

            RetireOutputTexture();

            _outputRT = new RenderTexture(w, h, RT_DEPTH, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
            _outputRT.Create();

            StopStreaming();
            try
            {
                bool bindAll = _networkStorable.val;
                string key = _accessKeyStorable.val == null ? "" : _accessKeyStorable.val.Trim();

                _server = new HttpStreamServer(port, w, h, quality, bindAll, key);
                _server.Start();

                _worker = new JpegEncodeWorker(_server, w * h * 4);
                _worker.Start();

                // The socket is bound to every interface, so every address below
                // reaches it. OBS URL carries the single best guess for one-click paste;
                // All Addresses exists because only the operator knows which network the
                // viewing device is actually on.
                string suffix = key.Length > 0 ? "?key=" + Uri.EscapeDataString(key) : "";
                List<string> hosts = bindAll ? GetHostAddresses() : new List<string>();
                string host = hosts.Count > 0 ? hosts[0] : "localhost";

                _urlStorable.val = BuildUrl(host, port, suffix);
                _addressesStorable.val = bindAll
                    ? BuildAddressList(hosts, port, suffix)
                    : BuildUrl("localhost", port, suffix);

                SetStatus(bindAll
                    ? "Streaming on all interfaces, port " + port
                    : "Streaming on localhost, port " + port);
            }
            catch (Exception e)
            {
                SetStatus("Server failed: " + e.Message);
                _urlStorable.val = "(server not running)";
            }

            _needsRebuild = false;
        }

        /// <summary>
        /// Freeing a RenderTexture with a readback still pointing at it is an access
        /// violation, which kills the process outright rather than throwing. Hold it
        /// until the request returns.
        /// </summary>
        private void RetireOutputTexture()
        {
            if (_outputRT == null) return;
            if (_readbacks.Count > 0) _retiredTextures.Add(_outputRT);
            else DestroyTexture(_outputRT);
            _outputRT = null;
        }

        private void ReleaseRetiredTextures()
        {
            if (_readbacks.Count > 0 || _retiredTextures.Count == 0) return;
            for (int i = 0; i < _retiredTextures.Count; i++) DestroyTexture(_retiredTextures[i]);
            _retiredTextures.Clear();
        }

        private static void DestroyTexture(RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            Destroy(rt);
        }

        /// <summary>
        /// The framerate ceiling the game is running under, or 0 when it is uncapped.
        /// VSync overrides <c>targetFrameRate</c> in Unity, so it is checked first.
        /// </summary>
        private static int GetGameFpsCap()
        {
            int vsync = QualitySettings.vSyncCount;
            if (vsync > 0)
            {
                int refresh = Screen.currentResolution.refreshRate;
                if (refresh > 0) return refresh / vsync;
            }
            int target = Application.targetFrameRate;
            return target > 0 ? target : 0;
        }

        /// <summary>
        /// Capture rate is clamped to the game's own cap. Every capture costs a readback
        /// and, on the manual-render path, a second scene render -- so streaming above
        /// the rate the user allowed the game would spend GPU they asked not to spend.
        /// </summary>
        private void ApplyFrameBudget()
        {
            float requested = Mathf.Max(1f, _fpsStorable.val);
            _gameFpsCap = GetGameFpsCap();
            float effective = _gameFpsCap > 0 ? Mathf.Min(requested, _gameFpsCap) : requested;
            _frameBudget = 1f / effective;
        }

        /// <summary>
        /// Every IPv4 address this host answers on, most likely to be reachable first.
        /// The listener binds all of them at once, so this is a display concern only.
        /// </summary>
        private static List<string> GetHostAddresses()
        {
            List<string> ranked = new List<string>();
            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(Dns.GetHostName());

                // Collect a rank at a time. List.Sort is not stable in this runtime, and
                // two addresses of equal rank should keep the order the OS gave them.
                for (int want = 0; want <= WORST_HOST_RANK; want++)
                {
                    for (int i = 0; i < addrs.Length; i++)
                    {
                        if (addrs[i].AddressFamily != AddressFamily.InterNetwork) continue;
                        byte[] b = addrs[i].GetAddressBytes();
                        if (b[0] == 127) continue;
                        if (RankHost(b) != want) continue;
                        ranked.Add(addrs[i].ToString());
                    }
                }
            }
            catch
            {
                // Name resolution is best-effort; an empty list falls back to localhost.
            }
            return ranked;
        }

        private static string BuildUrl(string host, int port, string suffix)
        {
            return "http://" + host + ":" + port + "/stream" + suffix;
        }

        private static string BuildAddressList(List<string> hosts, int port, string suffix)
        {
            if (hosts.Count == 0) return "(no network address found)";

            string list = "";
            for (int i = 0; i < hosts.Count; i++)
            {
                if (i > 0) list += "\n";
                list += BuildUrl(hosts[i], port, suffix);
            }
            return list;
        }

        /// <summary>
        /// How likely a phone or a second PC on the same Wi-Fi is to reach this address,
        /// lower being better. One host answers on several at once - the router's LAN
        /// range, a hypervisor's virtual switch, an overlay VPN - and only the first is
        /// dialable by a device that has done nothing but join the same Wi-Fi.
        /// </summary>
        private static int RankHost(byte[] b)
        {
            if (b[0] == 192 && b[1] == 168) return 0;               // the usual home router range
            if (b[0] == 10) return 1;                               // larger private LANs
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return 2;  // private, but Hyper-V, WSL and Docker sit here
            if (b[0] == 169 && b[1] == 254) return 4;               // link-local: DHCP never answered, useless
            return 3;                                               // overlay VPN, carrier NAT, or public
        }

        /// <summary>
        /// A scene load can build this plugin before the atom owning the saved camera
        /// exists, so the restored key matches nothing on the first scan. Rescan on a
        /// slow tick for a bounded window rather than giving up on the first miss.
        /// </summary>
        private void ResolvePendingCamera()
        {
            _resolveElapsed += Time.unscaledDeltaTime;
            _resolveTimer += Time.unscaledDeltaTime;
            if (_resolveTimer < RESOLVE_RETRY_SECONDS) return;
            _resolveTimer = 0f;

            RescanCameras();
            string key = _pendingCameraKey;
            if (_popupKeys.Contains(key))
            {
                _pendingCameraKey = null;

                // Show the resolved entry in the popup without re-entering the callback
                // that is about to run.
                _cameraChooser.valNoCallback = key;
                OnCameraSelected(key);
                return;
            }

            if (_resolveElapsed >= RESOLVE_WINDOW_SECONDS)
            {
                _pendingCameraKey = null;
                SetStatus("Saved camera not found: " + key);
            }
        }

        private void StopStreaming()
        {
            // Worker first: it holds the server and may still be mid-submit.
            if (_worker != null)
            {
                _worker.Stop();
                _worker = null;
            }
            if (_server != null)
            {
                _server.Stop();
                _server = null;
            }
        }

        private void LateUpdate()
        {
            if (_needsRebuild) RebuildPipeline();
            if (_pendingCameraKey != null) ResolvePendingCamera();
            if (!_enableStorable.val || _sourceCamera == null || _server == null) return;

            // Accumulate before any early return, so readback latency overlaps
            // the frame budget instead of stacking on top of it.
            _frameTimer += Time.unscaledDeltaTime;
            _statFrames++;

            bool hasClients = _server.ClientCount > 0;

            while (_readbacks.Count > 0 && _readbacks.Peek().Request.done)
            {
                PendingReadback done = _readbacks.Dequeue();
                if (done.Request.hasError || !hasClients) continue;

                float t0 = Time.realtimeSinceStartup;
                ConsumeReadback(done);
                _statConsumeMs += (Time.realtimeSinceStartup - t0) * 1000f;
                _statCaptures++;
            }

            ReleaseRetiredTextures();

            // With nobody connected, every term below is wasted frame time.
            if (!hasClients)
            {
                _frameTimer = 0f;
                UpdateDiagnostics();
                return;
            }

            RenderTexture readFrom = null;
            bool needManualRender = false;
            if (_sourceIsCreated)
            {
                readFrom = _outputRT;
            }
            else if (_sourceCamera.targetTexture != null)
            {
                readFrom = _sourceCamera.targetTexture;
            }
            else
            {
                needManualRender = true;
                readFrom = _outputRT;
            }

            if (readFrom == null) return;

            if (_frameTimer < _frameBudget) return;
            _frameTimer -= _frameBudget;
            if (_frameTimer > _frameBudget) _frameTimer = 0f;

            if (needManualRender)
            {
                float tr = Time.realtimeSinceStartup;
                RenderCameraToMyRT();
                _statRenderMs += (Time.realtimeSinceStartup - tr) * 1000f;
            }

            // Downscaling here is linear in every cost downstream.
            if (_outputRT != null && readFrom != _outputRT &&
                (readFrom.width != _outputRT.width || readFrom.height != _outputRT.height))
            {
                BlitPreservingAspect(readFrom, _outputRT);
                readFrom = _outputRT;
            }

            // Mismatched buffers drop the copy onto a per-byte loop.
            _worker.EnsureBufferSize(readFrom.width * readFrom.height * 4);

            if (_readbacks.Count < MAX_READBACKS_IN_FLIGHT)
            {
                try
                {
                    PendingReadback queued;
                    queued.Request = UnityEngine.Experimental.Rendering.AsyncGPUReadback.Request(readFrom, 0);
                    queued.Width = readFrom.width;
                    queued.Height = readFrom.height;
                    queued.IsSRGB = readFrom.sRGB;
                    _readbacks.Enqueue(queued);
                }
                catch (Exception e)
                {
                    SetStatus("Submit failed: " + e.Message);
                }
            }

            UpdateDiagnostics();
        }

        /// <summary>Writes measured rates and costs to the Status line once a second.</summary>
        private void UpdateDiagnostics()
        {
            float now = Time.realtimeSinceStartup;
            if (_statWindowStart <= 0f) { _statWindowStart = now; return; }
            float elapsed = now - _statWindowStart;
            if (elapsed < 1f) return;

            // The cap can change mid-session; the slider callback alone would miss it.
            ApplyFrameBudget();

            int clients = _server != null ? _server.ClientCount : 0;
            float gameFps = _statFrames / elapsed;
            float outFps = _statCaptures / elapsed;
            float mainMs = _statCaptures > 0 ? _statConsumeMs / _statCaptures : 0f;
            float renderMs = _statCaptures > 0 ? _statRenderMs / _statCaptures : 0f;
            float jpegMs = _worker != null ? _worker.LastEncodeMs : 0f;
            int dropped = _worker != null ? _worker.TakeDroppedFrames() : 0;

            // Not via SetStatus: that logs, and once a second is spam.
            _statusStorable.val =
                "game " + gameFps.ToString("F0") + " fps" +
                (_gameFpsCap > 0 ? " (cap " + _gameFpsCap + ")" : " (uncapped)") +
                "  |  stream " + outFps.ToString("F1") + " fps" +
                "  |  main " + mainMs.ToString("F1") + " ms" +
                "  |  jpeg " + jpegMs.ToString("F1") + " ms" +
                "  |  render " + renderMs.ToString("F1") + " ms" +
                "  |  " + clients + (clients == 1 ? " client" : " clients") +
                (dropped > 0 ? "  |  " + dropped + " dropped" : "");

            _statFrames = 0;
            _statCaptures = 0;
            _statConsumeMs = 0f;
            _statRenderMs = 0f;
            _statWindowStart = now;
        }

        private void ConsumeReadback(PendingReadback readback)
        {
            if (_worker == null) return;

            var data = readback.Request.GetData<byte>();
            int needed = readback.Width * readback.Height * 4;
            if (needed <= 0 || data.Length < needed) return;

            // Pooled buffer, so the async path allocates nothing per frame.
            byte[] buffer = _worker.Rent();
            if (buffer == null) return; // worker still busy; the next frame supersedes this one
            if (buffer.Length < needed)
            {
                _worker.Recycle(buffer);
                return;
            }

            // Bulk copy; CopyTo needs exact length, so keep a fallback.
            if (data.Length == buffer.Length)
                data.CopyTo(buffer);
            else
                for (int i = 0; i < needed; i++) buffer[i] = data[i];

            _worker.Submit(buffer, readback.Width, readback.Height, _server.JpegQuality,
                           _flipStorable.val, readback.IsSRGB ? null : s_srgbLUT);
        }

        private void RenderCameraToMyRT()
        {
            // Fallback for cameras without a targetTexture -- one-shot render
            // to our sRGB RT. Costs one extra render per frame.
            if (_sourceCamera == null || _outputRT == null) return;
            RenderTexture prevTarget = _sourceCamera.targetTexture;
            bool prevEnabled = _sourceCamera.enabled;
            try
            {
                _sourceCamera.enabled = false;
                _sourceCamera.targetTexture = _outputRT;
                _sourceCamera.Render();
            }
            finally
            {
                _sourceCamera.enabled = prevEnabled;
                _sourceCamera.targetTexture = prevTarget;
            }
        }

        private static void EnsureSRGBLUT()
        {
            if (s_srgbLUT != null) return;
            s_srgbLUT = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                float lin = i / 255f;
                float srgb;
                if (lin <= 0.0031308f) srgb = lin * 12.92f;
                else srgb = 1.055f * Mathf.Pow(lin, 1f / 2.4f) - 0.055f;
                int b = (int)Mathf.RoundToInt(srgb * 255f);
                if (b < 0) b = 0; else if (b > 255) b = 255;
                s_srgbLUT[i] = (byte)b;
            }
        }

        private void SetStatus(string msg)
        {
            _lastStatus = msg;
            if (_statusStorable != null) _statusStorable.val = msg;
            SuperController.LogMessage("LiveStreamConnectorToOBS: " + msg);
        }

        private void OnDestroy()
        {
            StopStreaming();
            TeardownCamera();

            // A readback outliving the plugin leaks a few MB, which is the cheaper of
            // the two outcomes: the texture is not owned by a GameObject, so the request
            // completes against live memory and the scene unload collects it.
            if (_readbacks.Count > 0)
            {
                _outputRT = null;
                _retiredTextures.Clear();
            }
            else
            {
                RetireOutputTexture();
                ReleaseRetiredTextures();
            }
        }
    }
}
