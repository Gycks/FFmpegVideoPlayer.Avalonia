using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpegVideoPlayer.Core;
using OpenTK.Audio.OpenAL;

namespace FFmpegVideoPlayer.Audio.OpenTK;

/// <summary>
/// OpenTK-based audio player implementation using OpenAL for low-latency audio playback.
/// Handles sample format conversion and buffer management.
/// </summary>
public sealed class OpenTKAudioPlayer : IAudioPlayer
{
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly int _inputChannels;
    private readonly ALDevice _device;
    private readonly ALContext _context;
    private readonly int _source;
    private readonly ConcurrentQueue<int> _availableBuffers;
    private readonly ConcurrentQueue<float[]> _pendingSamples;
    private readonly ConcurrentQueue<short[]> _pendingS16Samples;
    private readonly Thread _audioThread;
    private readonly CancellationTokenSource _cts;
    private float _volume = 1.0f;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _isDisposed;
    private readonly PlayerLogger _logger;
    private long _totalSamplesPlayed = 0; // Track total samples played for sync
    private long _totalSamplesQueued = 0; // Track total samples queued (for accurate sync)
    private readonly object _syncLock = new();

    private const int BufferCount = 16;
    private const int SamplesPerBuffer = 16384; // Increased buffer size for smoother playback

    public OpenTKAudioPlayer(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _inputChannels = channels;
        _channels = Math.Min(channels, 2); // Output is limited to stereo
        _logger = new PlayerLogger();
        
        Debug.WriteLine($"[OpenTKAudioPlayer] Initializing: sampleRate={sampleRate}, inputChannels={channels}, outputChannels={_channels}");
        _logger.Log("OpenTKAudioPlayer", "Initialize", new { SampleRate = sampleRate, InputChannels = channels, OutputChannels = _channels });
        
        // Initialize OpenAL
        try
        {
            _device = ALC.OpenDevice(null);
            if (_device == ALDevice.Null)
            {
                var errorMsg = "Failed to open audio device. OpenAL may not be available. " +
                              "On Windows, ensure OpenAL Soft is installed or OpenTK native libraries are present.";
                Debug.WriteLine($"[OpenTKAudioPlayer] {errorMsg}");
                _logger.Log("OpenTKAudioPlayer", "OpenDeviceFailed", new { Error = errorMsg });
                throw new InvalidOperationException(errorMsg);
            }
            
            Debug.WriteLine($"[OpenTKAudioPlayer] Audio device opened successfully");
            _logger.Log("OpenTKAudioPlayer", "OpenDeviceSuccess", null);
        }
        catch (DllNotFoundException ex)
        {
            var errorMsg = $"OpenAL native library not found: {ex.Message}. " +
                          "On Windows, OpenTK requires OpenAL Soft (openal32.dll) to be available. " +
                          "Ensure OpenTK native libraries are properly deployed or install OpenAL Soft.";
            Debug.WriteLine($"[OpenTKAudioPlayer] {errorMsg}");
            _logger.Log("OpenTKAudioPlayer", "OpenDeviceFailed", new { Error = errorMsg, Exception = ex.Message });
            throw new InvalidOperationException(errorMsg, ex);
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to initialize OpenAL: {ex.Message}";
            Debug.WriteLine($"[OpenTKAudioPlayer] {errorMsg}");
            _logger.Log("OpenTKAudioPlayer", "OpenDeviceFailed", new { Error = errorMsg, Exception = ex.Message });
            throw new InvalidOperationException(errorMsg, ex);
        }

        _context = ALC.CreateContext(_device, (int[]?)null);
        if (_context == ALContext.Null)
        {
            var error = ALC.GetError(_device);
            var errorMsg = $"Failed to create OpenAL context. Error: {error}";
            Debug.WriteLine($"[OpenTKAudioPlayer] {errorMsg}");
            throw new InvalidOperationException(errorMsg);
        }
        
        ALC.MakeContextCurrent(_context);
        var contextError = ALC.GetError(_device);
        if (contextError != AlcError.NoError)
        {
            Debug.WriteLine($"[OpenTKAudioPlayer] Warning: ALC.MakeContextCurrent error: {contextError}");
        }

        // Create source
        _source = AL.GenSource();
        var sourceError = AL.GetError();
        if (sourceError != ALError.NoError)
        {
            Debug.WriteLine($"[OpenTKAudioPlayer] Warning: AL.GenSource error: {sourceError}");
        }
        
        AL.Source(_source, ALSourcef.Gain, _volume);
        var gainError = AL.GetError();
        if (gainError != ALError.NoError)
        {
            Debug.WriteLine($"[OpenTKAudioPlayer] Warning: AL.Source(Gain) error: {gainError}");
        }

        // Create buffers
        _availableBuffers = new ConcurrentQueue<int>();
        for (int i = 0; i < BufferCount; i++)
        {
            _availableBuffers.Enqueue(AL.GenBuffer());
        }

        _pendingSamples = new ConcurrentQueue<float[]>();
        _pendingS16Samples = new ConcurrentQueue<short[]>();
        _cts = new CancellationTokenSource();

        // Start audio processing thread
        _audioThread = new Thread(AudioLoop)
        {
            Name = "OpenTKAudioPlayer",
            IsBackground = true
        };
        _audioThread.Start();
        
        Debug.WriteLine("[OpenTKAudioPlayer] Audio thread started");
    }

    public void SetVolume(float volume)
    {
        var oldVolume = _volume;
        _volume = Math.Clamp(volume, 0f, 1f);
        AL.Source(_source, ALSourcef.Gain, _volume);
        _logger.Log("OpenTKAudioPlayer", "SetVolume", new { OldVolume = oldVolume, NewVolume = _volume });
    }

    /// <summary>
    /// Queue pre-converted S16 stereo samples directly (more efficient when using SwrContext)
    /// </summary>
    public unsafe void QueueSamplesS16(short* samples, int sampleCount)
    {
        if (_isDisposed || _isPaused) return;
        
        // Copy to managed array
        var pcmData = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            pcmData[i] = samples[i];
        }
        
        _pendingS16Samples.Enqueue(pcmData);
    }

    public void QueueSamples(float[] samples)
    {
        if (_isDisposed || _isPaused) return;
        
        // If input has more channels than output, downmix
        if (_inputChannels > _channels)
        {
            samples = DownmixToStereo(samples, _inputChannels);
        }
        
        _pendingSamples.Enqueue(samples);
    }
    
    private float[] DownmixToStereo(float[] input, int inputChannels)
    {
        int samplesPerChannel = input.Length / inputChannels;
        var output = new float[samplesPerChannel * 2]; // Stereo output
        
        for (int i = 0; i < samplesPerChannel; i++)
        {
            int inputOffset = i * inputChannels;
            int outputOffset = i * 2;
            
            // Simple downmix: average channels for left/right
            // For 5.1 (6ch): FL, FR, FC, LFE, BL, BR
            // Left = FL + 0.707*FC + 0.707*BL
            // Right = FR + 0.707*FC + 0.707*BR
            
            if (inputChannels >= 6)
            {
                // 5.1 surround downmix
                float fl = input[inputOffset + 0];     // Front Left
                float fr = input[inputOffset + 1];     // Front Right
                float fc = input[inputOffset + 2];     // Front Center
                float lfe = input[inputOffset + 3];    // LFE (subwoofer)
                float bl = input[inputOffset + 4];     // Back Left
                float br = input[inputOffset + 5];     // Back Right
                
                output[outputOffset + 0] = Math.Clamp(fl + 0.707f * fc + 0.707f * bl + 0.5f * lfe, -1f, 1f);
                output[outputOffset + 1] = Math.Clamp(fr + 0.707f * fc + 0.707f * br + 0.5f * lfe, -1f, 1f);
            }
            else
            {
                // Generic downmix: average odd channels to left, even to right
                float left = 0, right = 0;
                for (int c = 0; c < inputChannels; c++)
                {
                    if (c % 2 == 0)
                        left += input[inputOffset + c];
                    else
                        right += input[inputOffset + c];
                }
                output[outputOffset + 0] = Math.Clamp(left / (inputChannels / 2f), -1f, 1f);
                output[outputOffset + 1] = Math.Clamp(right / (inputChannels / 2f), -1f, 1f);
            }
        }
        
        return output;
    }

    public void Resume()
    {
        _isPaused = false;
        _logger.Log("OpenTKAudioPlayer", "Resume", new { WasPlaying = _isPlaying });
        
        // Always try to start playback if we have buffers queued
        // The AudioLoop will handle the actual start, but we can also try here
        AL.GetSource(_source, ALGetSourcei.BuffersQueued, out int queued);
        AL.GetSource(_source, ALGetSourcei.SourceState, out int state);
        
        if (queued > 0 && state != (int)ALSourceState.Playing)
        {
            AL.SourcePlay(_source);
            var error = AL.GetError();
            if (error != ALError.NoError)
            {
                Debug.WriteLine($"[OpenTKAudioPlayer] Resume: AL.SourcePlay error: {error}");
            }
            else
            {
                _isPlaying = true;
                Debug.WriteLine($"[OpenTKAudioPlayer] Resume: Started playback with {queued} queued buffers");
            }
        }
    }

    public void Pause()
    {
        _isPaused = true;
        _logger.Log("OpenTKAudioPlayer", "Pause", null);
        AL.SourcePause(_source);
    }

    /// <summary>
    /// Gets the current audio playback time in seconds based on samples played.
    /// Accounts for samples currently in playback pipeline for accurate sync.
    /// </summary>
    public double GetPlaybackTime()
    {
        lock (_syncLock)
        {
            // Get OpenAL source offset to account for samples currently being played by hardware
            // SampleOffset returns the number of sample frames (stereo pairs) currently being played
            AL.GetSource(_source, ALGetSourcei.SampleOffset, out int sampleOffsetFrames);
            var error = AL.GetError();
            if (error != ALError.NoError)
            {
                // Fallback if SampleOffset is not supported
                sampleOffsetFrames = 0;
            }
            
            // Convert sample frames to individual samples (for stereo: frames * channels)
            var sampleOffset = sampleOffsetFrames * _channels;
            
            // Calculate current playback position from what OpenAL has actually played:
            // - _totalSamplesPlayed tracks fully processed buffers.
            // - sampleOffset tracks the current position inside the active buffer.
            // Do not include queued-but-unplayed buffers. FFmpeg can decode audio much
            // faster than real time, especially for audio-only files, and counting the
            // queued pipeline makes the media clock run ahead and stop playback early.
            var currentSamples = _totalSamplesPlayed + sampleOffset;
            
            // For stereo audio, _totalSamplesPlayed counts both L and R samples (interleaved)
            // We need to convert to frames (sample pairs) by dividing by channel count
            // Then divide by sample rate to get time in seconds
            // For stereo: time = totalSamples / channels / sampleRate
            return currentSamples / _channels / (double)_sampleRate;
        }
    }

    public void Stop()
    {
        _logger.Log("OpenTKAudioPlayer", "Stop", null);
        AL.SourceStop(_source);
        _isPlaying = false;
        _hasStartedOnce = false;
        lock (_syncLock)
        {
            _totalSamplesPlayed = 0;
            _totalSamplesQueued = 0;
        }
        
        // Clear pending samples
        while (_pendingSamples.TryDequeue(out _)) { }
        
        // Unqueue all buffers
        AL.GetSource(_source, ALGetSourcei.BuffersQueued, out int queued);
        if (queued > 0)
        {
            var buffers = new int[queued];
            AL.SourceUnqueueBuffers(_source, queued, buffers);
            foreach (var buffer in buffers)
            {
                _availableBuffers.Enqueue(buffer);
            }
        }
    }

    private bool _hasStartedOnce = false;
    
    private void AudioLoop()
    {
        var token = _cts.Token;

        try
        {
            ALC.MakeContextCurrent(_context);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OpenTKAudioPlayer] Failed to make context current on audio thread: {ex.Message}");
        }
        
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Check for processed buffers to recycle
                AL.GetSource(_source, ALGetSourcei.BuffersProcessed, out int processed);
                while (processed > 0)
                {
                    var buffer = AL.SourceUnqueueBuffer(_source);
                    _availableBuffers.Enqueue(buffer);
                    
                    // Track samples played for synchronization
                    AL.GetBuffer(buffer, ALGetBufferi.Size, out int bufferSize);
                    // bufferSize is in bytes, each sample is 2 bytes, so total samples = bufferSize / 2
                    // For stereo, this gives total samples (left + right)
                    int samplesInBuffer = bufferSize / 2;
                    lock (_syncLock)
                    {
                        _totalSamplesPlayed += samplesInBuffer;
                    }
                    
                    processed--;
                }

                // Queue S16 samples first (from SwrContext - already in correct format)
                int s16Queued = 0;
                while (_availableBuffers.TryDequeue(out var buffer))
                {
                    if (!_pendingS16Samples.TryDequeue(out var pcmData))
                    {
                        _availableBuffers.Enqueue(buffer);
                        break;
                    }

                    AL.BufferData(buffer, ALFormat.Stereo16, pcmData, _sampleRate);
                    var bufferError = AL.GetError();
                    if (bufferError != ALError.NoError)
                    {
                        Debug.WriteLine($"[OpenTKAudioPlayer] AL.BufferData error: {bufferError}");
                        _availableBuffers.Enqueue(buffer); // Return buffer on error
                        break;
                    }
                    
                    AL.SourceQueueBuffer(_source, buffer);
                    var queueError = AL.GetError();
                    if (queueError != ALError.NoError)
                    {
                        Debug.WriteLine($"[OpenTKAudioPlayer] AL.SourceQueueBuffer error: {queueError}");
                        _availableBuffers.Enqueue(buffer); // Return buffer on error
                        break;
                    }
                    
                    s16Queued++;
                    
                    // Track samples queued (pcmData.Length is total samples for stereo interleaved)
                    lock (_syncLock)
                    {
                        _totalSamplesQueued += pcmData.Length;
                    }
                }

                // Queue float samples (fallback - convert to S16)
                int floatQueued = 0;
                while (_availableBuffers.TryDequeue(out var buffer))
                {
                    if (!_pendingSamples.TryDequeue(out var samples))
                    {
                        _availableBuffers.Enqueue(buffer);
                        break;
                    }

                    // Convert float samples to 16-bit PCM
                    var pcmData = new short[samples.Length];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        pcmData[i] = (short)(samples[i] * 32767);
                    }

                    var format = _channels == 1 ? ALFormat.Mono16 : ALFormat.Stereo16;
                    AL.BufferData(buffer, format, pcmData, _sampleRate);
                    var bufferError = AL.GetError();
                    if (bufferError != ALError.NoError)
                    {
                        Debug.WriteLine($"[OpenTKAudioPlayer] AL.BufferData (float) error: {bufferError}");
                        _availableBuffers.Enqueue(buffer); // Return buffer on error
                        break;
                    }
                    
                    AL.SourceQueueBuffer(_source, buffer);
                    var queueError = AL.GetError();
                    if (queueError != ALError.NoError)
                    {
                        Debug.WriteLine($"[OpenTKAudioPlayer] AL.SourceQueueBuffer (float) error: {queueError}");
                        _availableBuffers.Enqueue(buffer); // Return buffer on error
                        break;
                    }
                    
                    floatQueued++;
                    
                    // Track samples queued (samples.Length is total sample count for stereo interleaved)
                    lock (_syncLock)
                    {
                        _totalSamplesQueued += samples.Length;
                    }
                }
                
                if (s16Queued > 0 || floatQueued > 0)
                {
                    AL.GetSource(_source, ALGetSourcei.BuffersQueued, out int totalQueued);
                    _logger.Log("OpenTKAudioPlayer", "BuffersQueued", new 
                    { 
                        S16Buffers = s16Queued,
                        FloatBuffers = floatQueued,
                        TotalQueued = totalQueued,
                        AvailableBuffers = _availableBuffers.Count,
                        PendingS16 = _pendingS16Samples.Count,
                        PendingFloat = _pendingSamples.Count
                    });
                }

                // Start or resume playback
                AL.GetSource(_source, ALGetSourcei.SourceState, out int state);
                AL.GetSource(_source, ALGetSourcei.BuffersQueued, out int queued);
                
                if (!_isPaused && state != (int)ALSourceState.Playing && queued > 0)
                {
                    // First time start: wait for enough buffers
                    // Subsequent restarts: start immediately if we have any buffers
                    if (!_hasStartedOnce && queued >= 4)
                    {
                        Debug.WriteLine($"[OpenTKAudioPlayer] Initial playback start with {queued} queued buffers");
                        _logger.Log("OpenTKAudioPlayer", "PlaybackStarted", new { QueuedBuffers = queued, Reason = "Initial start" });
                        AL.SourcePlay(_source);
                        var error = AL.GetError();
                        if (error != ALError.NoError)
                        {
                            Debug.WriteLine($"[OpenTKAudioPlayer] AL.SourcePlay (initial) error: {error}");
                        }
                        else
                        {
                            _isPlaying = true;
                            _hasStartedOnce = true;
                            Debug.WriteLine($"[OpenTKAudioPlayer] Playback started successfully");
                        }
                    }
                    else if (_hasStartedOnce && queued >= 1)
                    {
                        // Restart after buffer underrun
                        _logger.Log("OpenTKAudioPlayer", "PlaybackRestarted", new { QueuedBuffers = queued, Reason = "Buffer underrun recovery" });
                        AL.SourcePlay(_source);
                        var error = AL.GetError();
                        if (error != ALError.NoError)
                        {
                            Debug.WriteLine($"[OpenTKAudioPlayer] AL.SourcePlay (restart) error: {error}");
                        }
                        else
                        {
                            _isPlaying = true;
                        }
                    }
                }

                Thread.Sleep(2); // Fast polling for smooth audio with reduced CPU usage
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OpenTKAudioPlayer] Error: {ex.Message}");
                Thread.Sleep(10);
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _logger.Log("OpenTKAudioPlayer", "Dispose", null);
        _cts.Cancel();
        _audioThread.Join(1000);
        _cts.Dispose();

        Stop();

        // Delete buffers
        while (_availableBuffers.TryDequeue(out var buffer))
        {
            AL.DeleteBuffer(buffer);
        }

        AL.DeleteSource(_source);

        if (_context != ALContext.Null)
        {
            ALC.MakeContextCurrent(ALContext.Null);
            ALC.DestroyContext(_context);
        }

        if (_device != ALDevice.Null)
        {
            ALC.CloseDevice(_device);
        }
        
        _logger.Dispose();
    }
}

