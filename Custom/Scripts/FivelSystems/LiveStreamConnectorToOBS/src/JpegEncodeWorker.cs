using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FivelSystems.LiveStreamConnectorToOBS
{
    /// <summary>
    /// Owns one encoder thread. The main thread copies a readback into a pooled buffer
    /// and hands it over; the gamma pass, colour conversion and JPEG encode all happen
    /// here, leaving the copy as the only main-thread cost. Must not touch a Unity API.
    /// </summary>
    public class JpegEncodeWorker
    {
        // One buffer being filled, one queued, one being encoded.
        private const int BUFFER_POOL_SIZE = 3;

        private const int WAIT_TIMEOUT_MS = 250;
        private const int JOIN_TIMEOUT_MS = 1000;

        private readonly HttpStreamServer _server;
        private int _bufferBytes;
        private readonly object _lock = new object();
        private readonly Stack<byte[]> _freeBuffers = new Stack<byte[]>();
        private readonly JpegEncoder _encoder = new JpegEncoder();

        private Thread _thread;
        private volatile bool _stop;

        // Handoff slot. Latest wins: only the newest frame is worth sending.
        private byte[] _pending;
        private int _pendingWidth;
        private int _pendingHeight;
        private int _pendingQuality;
        private bool _pendingFlip;
        private byte[] _pendingGammaLut;

        private volatile int _lastEncodeMicroseconds;
        private int _droppedFrames;

        public JpegEncodeWorker(HttpStreamServer server, int bufferBytes)
        {
            if (server == null) throw new ArgumentNullException("server");
            _server = server;
            _bufferBytes = bufferBytes < 4 ? 4 : bufferBytes;
            for (int i = 0; i < BUFFER_POOL_SIZE; i++) _freeBuffers.Push(new byte[_bufferBytes]);
        }

        public float LastEncodeMs { get { return _lastEncodeMicroseconds / 1000f; } }

        public int BufferBytes { get { return _bufferBytes; } }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(EncodeLoop);
            _thread.IsBackground = true;
            // Streaming must never outrank the game.
            _thread.Priority = ThreadPriority.BelowNormal;
            _thread.Start();
        }

        public void Stop()
        {
            _stop = true;
            lock (_lock) Monitor.PulseAll(_lock);
            if (_thread != null && _thread.IsAlive)
            {
                try { _thread.Join(JOIN_TIMEOUT_MS); }
                catch { }
            }
            _thread = null;
        }

        /// <summary>
        /// Resizes the pool when the source resolution changes. Buffers must match the
        /// readback exactly or the caller cannot bulk-copy into them.
        /// </summary>
        public void EnsureBufferSize(int bytes)
        {
            if (bytes < 4) bytes = 4;
            lock (_lock)
            {
                if (bytes == _bufferBytes) return;
                _bufferBytes = bytes;
                _freeBuffers.Clear();
                for (int i = 0; i < BUFFER_POOL_SIZE; i++) _freeBuffers.Push(new byte[bytes]);
            }
        }

        /// <summary>Null means the worker is still busy: skip the frame, do not allocate.</summary>
        public byte[] Rent()
        {
            lock (_lock)
            {
                if (_freeBuffers.Count > 0) return _freeBuffers.Pop();
            }
            return null;
        }

        public void Recycle(byte[] buffer)
        {
            // Size-checked: a resize can strand buffers rented before it.
            if (buffer == null || buffer.Length != _bufferBytes) return;
            lock (_lock)
            {
                if (_freeBuffers.Count < BUFFER_POOL_SIZE) _freeBuffers.Push(buffer);
            }
        }

        /// <summary>Ownership of the buffer passes to the worker, which recycles it.</summary>
        public void Submit(byte[] rgba, int width, int height, int quality, bool flipVertical, byte[] gammaLut)
        {
            if (rgba == null) return;
            lock (_lock)
            {
                if (_pending != null)
                {
                    if (_freeBuffers.Count < BUFFER_POOL_SIZE) _freeBuffers.Push(_pending);
                    _droppedFrames++;
                }
                _pending = rgba;
                _pendingWidth = width;
                _pendingHeight = height;
                _pendingQuality = quality;
                _pendingFlip = flipVertical;
                _pendingGammaLut = gammaLut;
                Monitor.Pulse(_lock);
            }
        }

        /// <summary>Frames superseded before the worker reached them; reset by the read.</summary>
        public int TakeDroppedFrames()
        {
            lock (_lock)
            {
                int dropped = _droppedFrames;
                _droppedFrames = 0;
                return dropped;
            }
        }

        private void EncodeLoop()
        {
            while (!_stop)
            {
                byte[] rgba;
                int width, height, quality;
                bool flip;
                byte[] lut;

                lock (_lock)
                {
                    while (!_stop && _pending == null) Monitor.Wait(_lock, WAIT_TIMEOUT_MS);
                    if (_stop) return;

                    rgba = _pending;
                    _pending = null;
                    width = _pendingWidth;
                    height = _pendingHeight;
                    quality = _pendingQuality;
                    flip = _pendingFlip;
                    lut = _pendingGammaLut;
                }

                try
                {
                    long start = Stopwatch.GetTimestamp();
                    int length = _encoder.Encode(rgba, width, height, quality, flip, lut);
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    _lastEncodeMicroseconds = (int)(elapsed * 1000000L / Stopwatch.Frequency);

                    // SubmitFrame copies, so the encoder buffer is free to reuse.
                    if (length > 0 && !_stop) _server.SubmitFrame(_encoder.OutputBuffer, length);
                }
                catch
                {
                    // A fault on one frame must not take the thread down with it.
                }
                finally
                {
                    Recycle(rgba);
                }
            }
        }
    }
}
