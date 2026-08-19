using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using MeshVR;
using SimpleJSON;

namespace FivelSystems.LiveStreamConnectorToOBS
{
    public class LiveStreamConnectorToOBS : MVRScript
    {
        private const int DEFAULT_WIDTH = 1280;
        private const int DEFAULT_HEIGHT = 720;
        private const int RT_DEPTH = 24;
        private const int DEFAULT_PORT = 8088;
        private const int DEFAULT_JPEG_QUALITY = 75;
        private const int DEFAULT_FPS = 30;

        private UIDynamicToggle _enableToggle;
        private UIDynamicToggle _syncToggle;
        private UIDynamicToggle _threadedToggle;
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

        private readonly List<CameraInfo> _sceneCameras = new List<CameraInfo>();
        private readonly List<string> _popupOptions = new List<string>();
        private readonly JSONStorableStringChooser _cameraChooser = new JSONStorableStringChooser("SourceCamera", new List<string>(), "", "Source Camera");
        private readonly JSONStorableString _statusStorable = new JSONStorableString("Status", "");
        private readonly JSONStorableString _portStorable = new JSONStorableString("Port", "" + DEFAULT_PORT);
        private readonly JSONStorableString _urlStorable = new JSONStorableString("OBS URL", "");
        private readonly JSONStorableBool _enableStorable = new JSONStorableBool("Enable Streaming", true);
        private readonly JSONStorableBool _syncCaptureStorable = new JSONStorableBool("Sync Capture", false);
        private readonly JSONStorableBool _threadedEncodeStorable = new JSONStorableBool("Threaded Encode", true);
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
        private Texture2D _readbackTex;
        private byte[] _readbackBytes;
        private HttpStreamServer _server;
        private bool _sourceIsCreated;
        private bool _needsRebuild;
        private float _frameBudget;
        private float _frameTimer;
        private string _lastStatus = "";
        private bool _readbackInFlight;
        private UnityEngine.Experimental.Rendering.AsyncGPUReadbackRequest _pendingRequest;
        private RenderTexture _readbackTarget;
        private bool _readbackTargetIsSRGB;
        private int _readbackWidth;
        private int _readbackHeight;
        private int _gameFpsCap;
        private JpegEncodeWorker _worker;
        private static byte[] s_srgbLUT;

        // Diagnostics counters.
        private int _statFrames;
        private int _statCaptures;
        private float _statWindowStart;
        private float _statConsumeMs;
        private float _statRenderMs;

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

            // Trades game fps for roughly 3x the stream fps.
            _syncToggle = CreateToggle(_syncCaptureStorable);
            RegisterBool(_syncCaptureStorable);

            // On: a pure-C# encoder on a worker thread, so the frame budget pays for
            // the copy alone. Off: Texture2D.EncodeToJPG on the main thread. Kept as a
            // toggle so the two can be compared without rebuilding the plugin.
            _threadedToggle = CreateToggle(_threadedEncodeStorable);
            RegisterBool(_threadedEncodeStorable);

            // Readback data starts at the bottom row and JPEG scanlines run top-down.
            // Only the threaded encoder needs this -- EncodeToJPG flips on its own.
            _flipToggle = CreateToggle(_flipStorable);
            RegisterBool(_flipStorable);

            // Off = loopback only. On = bind all interfaces.
            _networkToggle = CreateToggle(_networkStorable);
            RegisterBool(_networkStorable);
            _networkStorable.setCallbackFunction = v => { _needsRebuild = true; };

            // Empty = no key required; otherwise callers must pass ?key=...
            _accessKeyField = CreateTextField(_accessKeyStorable);
            RegisterString(_accessKeyStorable);
            _accessKeyStorable.setCallbackFunction = v => { _needsRebuild = true; };

            _portField = CreateTextField(_portStorable);
            _portStorable.setCallbackFunction = v => { _needsRebuild = true; };

            _cameraPopup = CreatePopup(_cameraChooser, false);
            _cameraChooser.setCallbackFunction = OnCameraSelected;

            _refreshButton = CreateButton("Refresh Camera List", true);
            _refreshButton.button.onClick.AddListener(RescanCameras);

            _widthSlider = CreateSlider(_widthStorable);
            _widthStorable.setCallbackFunction = v => { _needsRebuild = true; };
            _heightSlider = CreateSlider(_heightStorable);
            _heightStorable.setCallbackFunction = v => { _needsRebuild = true; };

            _qualitySlider = CreateSlider(_qualityStorable);
            _qualityStorable.setCallbackFunction = v =>
            {
                if (_server != null) _server.JpegQuality = Mathf.RoundToInt(v);
            };

            _fpsSlider = CreateSlider(_fpsStorable);
            _fpsStorable.setCallbackFunction = v => { ApplyFrameBudget(); };

            _urlText = CreateTextField(_urlStorable);

            _statusText = CreateTextField(_statusStorable);
        }

        private void RescanCameras()
        {
            _sceneCameras.Clear();
            _popupOptions.Clear();
            var found = CameraScanner.Scan(c => c.name != "__SpoutSourceCam");
            _sceneCameras.AddRange(found);

            _popupOptions.Add("-- Create New Camera --");
            for (int i = 0; i < _sceneCameras.Count; i++)
            {
                _popupOptions.Add(_sceneCameras[i].DisplayName);
            }
            _cameraChooser.choices = new List<string>(_popupOptions);
            if (string.IsNullOrEmpty(_cameraChooser.val) && _popupOptions.Count > 0)
                _cameraChooser.val = _popupOptions[0];
        }

        private void OnCameraSelected(string val)
        {
            if (string.IsNullOrEmpty(val)) return;
            TeardownCamera();
            int index = _popupOptions.IndexOf(val);
            if (index == 0)
            {
                CreateNewSourceCamera();
            }
            else if (index > 0 && index - 1 < _sceneCameras.Count)
            {
                AdoptSourceCamera(_sceneCameras[index - 1].Camera);
            }
            else
            {
                SetStatus("Invalid selection");
                return;
            }
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

        private void AdoptSourceCamera(Camera cam)
        {
            _sourceCamera = cam;
            _sourceIsCreated = false;
            // Auto-apply source RT resolution if present, so we stream at the
            // exact same size the CUA / WindowCamera already renders to.
            if (cam.targetTexture != null)
            {
                _widthStorable.val = cam.targetTexture.width;
                _heightStorable.val = cam.targetTexture.height;
                _needsRebuild = true;
                SetStatus("Using: " + cam.name + " (" + cam.targetTexture.width + "x" + cam.targetTexture.height + ")");
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

            if (_outputRT != null)
            {
                _outputRT.Release();
                Destroy(_outputRT);
            }
            if (_readbackTex != null) Destroy(_readbackTex);

            _outputRT = new RenderTexture(w, h, RT_DEPTH, RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
            _outputRT.Create();
            _readbackTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            _readbackTex.wrapMode = TextureWrapMode.Clamp;
            _readbackBytes = new byte[w * h * 4];

            StopStreaming();
            try
            {
                bool bindAll = _networkStorable.val;
                string key = _accessKeyStorable.val == null ? "" : _accessKeyStorable.val.Trim();

                _server = new HttpStreamServer(port, w, h, quality, bindAll, key);
                _server.Start();

                _worker = new JpegEncodeWorker(_server, w * h * 4);
                _worker.Start();

                // Report a URL usable from the device that will consume it.
                string host = "localhost";
                if (bindAll)
                {
                    string found = GetPreferredHost();
                    if (found != null) host = found;
                }
                string suffix = key.Length > 0 ? "?key=" + Uri.EscapeDataString(key) : "";
                _urlStorable.val = "http://" + host + ":" + port + "/stream" + suffix;

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

        /// <summary>Address a remote client should dial, or null if undetermined.</summary>
        private static string GetPreferredHost()
        {
            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(Dns.GetHostName());
                string lan = null;
                for (int i = 0; i < addrs.Length; i++)
                {
                    if (addrs[i].AddressFamily != AddressFamily.InterNetwork) continue;
                    byte[] b = addrs[i].GetAddressBytes();
                    if (b[0] == 127) continue;
                    if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return addrs[i].ToString();
                    if (lan == null) lan = addrs[i].ToString();
                }
                return lan;
            }
            catch
            {
                return null;
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
            if (!_enableStorable.val || _sourceCamera == null || _server == null) return;

            // Accumulate before any early return, so readback latency overlaps
            // the frame budget instead of stacking on top of it.
            _frameTimer += Time.unscaledDeltaTime;
            _statFrames++;

            bool hasClients = _server.ClientCount > 0;

            // 1. If a previous readback is done, consume it
            if (_readbackInFlight)
            {
                if (_pendingRequest.done)
                {
                    _readbackInFlight = false;
                    if (!_pendingRequest.hasError && hasClients)
                    {
                        float t0 = Time.realtimeSinceStartup;
                        ConsumeReadback();
                        _statConsumeMs += (Time.realtimeSinceStartup - t0) * 1000f;
                        _statCaptures++;
                    }
                }
                else
                {
                    return; // still waiting
                }
            }

            // With nobody connected, every term below is wasted frame time.
            if (!hasClients)
            {
                _frameTimer = 0f;
                UpdateDiagnostics();
                return;
            }

            // 2. Resolve the RT to read from -- no manual render!
            //    - "Create New Camera" mode: my camera renders to my RT natively
            //    - Existing camera with targetTexture: read its existing RT
            //    - Existing camera without targetTexture: must manual-render
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

            // 3. Throttle to target FPS (accumulated at step 0)
            if (_frameTimer < _frameBudget) return;
            _frameTimer -= _frameBudget;
            if (_frameTimer > _frameBudget) _frameTimer = 0f;

            // 4. If we have to manual-render, do it now (only case is a
            //    screen-only camera with no targetTexture -- rare in VAM)
            if (needManualRender)
            {
                float tr = Time.realtimeSinceStartup;
                RenderCameraToMyRT();
                _statRenderMs += (Time.realtimeSinceStartup - tr) * 1000f;
            }

            // 5. Remember which RT and its sRGB flag for the callback
            _readbackTarget = readFrom;
            _readbackTargetIsSRGB = readFrom.sRGB;
            _readbackWidth = readFrom.width;
            _readbackHeight = readFrom.height;

            // 6. Capture. Sync stalls the GPU but lifts throughput from
            //    gameFps/2-3 to min(TargetFPS, gameFps), at a cost in game fps.
            if (_syncCaptureStorable.val)
            {
                float t0 = Time.realtimeSinceStartup;
                CaptureSync(readFrom);
                _statConsumeMs += (Time.realtimeSinceStartup - t0) * 1000f;
                _statCaptures++;
            }
            else
            {
                try
                {
                    _pendingRequest = UnityEngine.Experimental.Rendering.AsyncGPUReadback.Request(readFrom, 0);
                    _readbackInFlight = true;
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

        private void ConsumeReadback()
        {
            var data = _pendingRequest.GetData<byte>();
            int needed = _readbackWidth * _readbackHeight * 4;
            if (needed <= 0 || data.Length < needed) return;

            byte[] gammaLut = _readbackTargetIsSRGB ? null : s_srgbLUT;

            if (_threadedEncodeStorable.val && _worker != null)
            {
                byte[] buffer = _worker.Rent();
                if (buffer == null) return; // worker still busy; the next frame supersedes this one
                if (buffer.Length < needed)
                {
                    _worker.Recycle(buffer);
                    return;
                }

                if (data.Length == buffer.Length)
                    data.CopyTo(buffer);
                else
                    for (int i = 0; i < needed; i++) buffer[i] = data[i];

                _worker.Submit(buffer, _readbackWidth, _readbackHeight,
                               _server.JpegQuality, _flipStorable.val, gammaLut);
                return;
            }

            if (_readbackTex == null || _readbackBytes == null) return;
            if (_readbackTex.width != _readbackWidth || _readbackTex.height != _readbackHeight) return;
            if (_readbackBytes.Length < needed) return;

            // Bulk copy; CopyTo needs exact length, so keep a fallback.
            if (data.Length == _readbackBytes.Length)
                data.CopyTo(_readbackBytes);
            else
                for (int i = 0; i < needed; i++) _readbackBytes[i] = data[i];

            if (!_readbackTargetIsSRGB)
            {
                // Linear source: gamma-encode in place, alpha untouched.
                byte[] lut = s_srgbLUT;
                for (int i = 0; i < needed; i += 4)
                {
                    _readbackBytes[i + 0] = lut[_readbackBytes[i + 0]];
                    _readbackBytes[i + 1] = lut[_readbackBytes[i + 1]];
                    _readbackBytes[i + 2] = lut[_readbackBytes[i + 2]];
                }
            }

            _readbackTex.LoadRawTextureData(_readbackBytes);
            _readbackTex.Apply(false);
            byte[] jpeg = _readbackTex.EncodeToJPG(_server.JpegQuality);
            if (jpeg != null) _server.SubmitFrame(jpeg);
        }

        /// <summary>Immediate readback: stalls the GPU, holds no pending work.</summary>
        private void CaptureSync(RenderTexture src)
        {
            if (_readbackTex == null || src == null || _server == null) return;

            RenderTexture prev = RenderTexture.active;
            try
            {
                RenderTexture.active = src;
                _readbackTex.ReadPixels(new Rect(0f, 0f, _readbackTex.width, _readbackTex.height), 0, 0, false);
                _readbackTex.Apply(false);
            }
            finally
            {
                RenderTexture.active = prev;
            }

            if (_threadedEncodeStorable.val && _worker != null)
            {
                // Allocates, but it buys the encode off this thread.
                byte[] raw = _readbackTex.GetRawTextureData();
                if (raw == null) return;
                _worker.Submit(raw, _readbackTex.width, _readbackTex.height, _server.JpegQuality,
                               _flipStorable.val, _readbackTargetIsSRGB ? null : s_srgbLUT);
                return;
            }

            if (!_readbackTargetIsSRGB && s_srgbLUT != null)
            {
                // Slow path: GetRawTextureData allocates. Skipped for sRGB sources.
                byte[] raw = _readbackTex.GetRawTextureData();
                byte[] lut = s_srgbLUT;
                for (int i = 0; i + 3 < raw.Length; i += 4)
                {
                    raw[i + 0] = lut[raw[i + 0]];
                    raw[i + 1] = lut[raw[i + 1]];
                    raw[i + 2] = lut[raw[i + 2]];
                }
                _readbackTex.LoadRawTextureData(raw);
                _readbackTex.Apply(false);
            }

            byte[] jpeg = _readbackTex.EncodeToJPG(_server.JpegQuality);
            if (jpeg != null) _server.SubmitFrame(jpeg);
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
            if (_outputRT != null)
            {
                _outputRT.Release();
                Destroy(_outputRT);
                _outputRT = null;
            }
            if (_readbackTex != null)
            {
                Destroy(_readbackTex);
                _readbackTex = null;
            }
        }
    }
}
