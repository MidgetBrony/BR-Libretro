using FMOD;
using FMODUnity;
using SK.Libretro;
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BR_Libretro;

internal sealed class AudioBridge : IAudioProcessor
{
    private readonly object gate = new();
    private readonly short[] ring = new short[524288];
    private int read, write, count, sampleRate;
    private Sound sound;
    private Channel channel;
    private ChannelGroup channelGroup;
    private bool disposed, loggedInput, loggedOutput, loggedDrop, loggedPrime, loggedUnderrun, bufferPrimed;
    private volatile bool startRequested;
    private SOUND_PCMREAD_CALLBACK pcmReadCallback;

    public void Init(int rate, double fps)
    {
        if (rate <= 0)
        {
            MelonLoader.MelonLogger.Error($"BR-Libretro core reported an invalid audio rate ({rate} Hz); no FMOD stream will be created.");
            return;
        }

        // Treat every Init as a complete stream boundary. A core switch gets a
        // fresh bridge, while an in-core AV timing change safely rebuilds the
        // FMOD stream at the newly reported native rate.
        DisposeFmod();
        lock (gate)
        {
            sampleRate = rate;
            read = write = count = 0;
            bufferPrimed = false;
            loggedInput = loggedOutput = loggedDrop = loggedPrime = loggedUnderrun = false;
            disposed = false;
            startRequested = true;
        }
        MelonLoader.MelonLogger.Msg($"BR-Libretro core reported {sampleRate} Hz audio; creating a matching FMOD source stream.");
    }

    internal void Update(Vector3 position, float volume, float maximumDistance)
    {
        if (disposed) return;
        if (startRequested && !channel.hasHandle()) StartFmod(position, volume, maximumDistance);
        if (!channel.hasHandle()) return;

        VECTOR pos = new() { x = position.x, y = position.y, z = position.z };
        VECTOR velocity = default;
        channel.set3DAttributes(ref pos, ref velocity);
        float maxDistance = Mathf.Max(0.51f, maximumDistance);
        channel.set3DMinMaxDistance(0.5f, maxDistance);
        Camera listener = Camera.main;
        float falloff = listener == null ? 0f : Mathf.Clamp01(Mathf.InverseLerp(maxDistance, 0.5f,
            Vector3.Distance(listener.transform.position, position)));
        channel.setVolume(Mathf.Clamp01(volume) * falloff);
    }

    private void StartFmod(Vector3 position, float volume, float maximumDistance)
    {
        int rate;
        lock (gate) { rate = sampleRate; startRequested = false; }
        if (rate <= 0) return;

        try
        {
            RESULT result = RuntimeManager.CoreSystem.createChannelGroup("BR-Libretro game", out channelGroup);
            if (result != RESULT.OK || !channelGroup.hasHandle()) { MelonLoader.MelonLogger.Error($"BR-Libretro FMOD channel group unavailable: {result}"); DisposeFmod(); return; }

            pcmReadCallback = FillPcm;
            CREATESOUNDEXINFO info = new();
            info.cbsize = Marshal.SizeOf(typeof(CREATESOUNDEXINFO));
            info.length = checked((uint)(rate * 2 * sizeof(short)));
            info.numchannels = 2;
            info.defaultfrequency = rate;
            info.format = SOUND_FORMAT.PCM16;
            // A 1024-frame callback is still only about 23 ms at 44.1 kHz, but
            // gives the emulation thread enough tolerance to avoid rapid
            // underrun/overrun cycles and audible discontinuities.
            info.decodebuffersize = 1024;
            info.pcmreadcallback = pcmReadCallback;
            MODE mode = MODE.OPENUSER | MODE.CREATESTREAM | MODE.LOOP_NORMAL | MODE._3D | MODE._3D_WORLDRELATIVE | MODE._3D_LINEARROLLOFF;
            result = RuntimeManager.CoreSystem.createSound(IntPtr.Zero, mode, ref info, out sound);
            if (result != RESULT.OK || !sound.hasHandle()) { MelonLoader.MelonLogger.Error($"BR-Libretro FMOD user stream failed: {result}"); DisposeFmod(); return; }
            result = RuntimeManager.CoreSystem.playSound(sound, channelGroup, true, out channel);
            if (result != RESULT.OK || !channel.hasHandle()) { MelonLoader.MelonLogger.Error($"BR-Libretro FMOD playback failed: {result}"); DisposeFmod(); return; }
            Update(position, volume, maximumDistance);
            channel.setPaused(false);
            MelonLoader.MelonLogger.Msg($"BR-Libretro audio attached to BOXROOM FMOD at {rate} Hz.");
        }
        catch (Exception ex) { MelonLoader.MelonLogger.Error("BR-Libretro FMOD setup failed: " + ex); DisposeFmod(); }
    }

    private unsafe RESULT FillPcm(IntPtr ignoredSound, IntPtr data, uint byteCount)
    {
        int samples = checked((int)(byteCount / sizeof(short)));
        if (!loggedOutput) { loggedOutput = true; MelonLoader.MelonLogger.Msg($"BR-Libretro FMOD requested its first PCM buffer ({samples} samples)."); }
        short* output = (short*)data;
        lock (gate)
        {
            // Prime roughly 60 ms before playback. If emulation momentarily
            // starves the stream, output one clean silent block and re-prime
            // rather than playing a partial block followed by zeros (a click).
            int primeSamples = Math.Max(samples * 2, sampleRate * 2 * 60 / 1000);
            if (!bufferPrimed)
            {
                if (count >= primeSamples)
                {
                    bufferPrimed = true;
                    if (!loggedPrime)
                    {
                        loggedPrime = true;
                        MelonLoader.MelonLogger.Msg($"BR-Libretro audio buffer primed with {count / 2} frames.");
                    }
                }
                else
                {
                    for (int i = 0; i < samples; i++) output[i] = 0;
                    return RESULT.OK;
                }
            }

            if (count < samples)
            {
                bufferPrimed = false;
                for (int i = 0; i < samples; i++) output[i] = 0;
                if (!loggedUnderrun)
                {
                    loggedUnderrun = true;
                    MelonLoader.MelonLogger.Warning("BR-Libretro audio underrun; output paused briefly while the PCM buffer re-primes.");
                }
                return RESULT.OK;
            }

            for (int i = 0; i < samples; i++)
            {
                output[i] = ring[read];
                read = (read + 1) % ring.Length;
            }
            count -= samples;
        }
        return RESULT.OK;
    }

    public void ProcessSample(short left, short right) { if (!loggedInput) { loggedInput = true; MelonLoader.MelonLogger.Msg("BR-Libretro received its first PCM samples from the core."); } lock (gate) { Enqueue(left); Enqueue(right); } }
    public unsafe void ProcessSampleBatch(IntPtr data, nuint frames, in PositionalData positionalData)
    {
        if (!loggedInput) { loggedInput = true; MelonLoader.MelonLogger.Msg($"BR-Libretro received its first PCM batch from the core ({frames} frames)."); }
        short* samples = (short*)data;
        lock (gate) for (nuint i = 0; i < frames * 2; i++) Enqueue(samples[i]);
    }
    private void Enqueue(short value)
    {
        // Allow up to 500 ms for startup and scheduling jitter. Always discard
        // complete stereo frames so left/right channels cannot become swapped.
        int maximumQueued = Math.Min(ring.Length - 2, Math.Max(4096, sampleRate));
        if (count >= maximumQueued)
        {
            read = (read + 2) % ring.Length;
            count -= 2;
            if (!loggedDrop)
            {
                loggedDrop = true;
                MelonLoader.MelonLogger.Warning("BR-Libretro audio exceeded its jitter buffer; the oldest stereo frame is being dropped.");
            }
        }
        ring[write] = value;
        write = (write + 1) % ring.Length;
        count++;
    }

    public void Dispose()
    {
        disposed = true;
        DisposeFmod();
        lock (gate) { read = write = count = 0; }
    }

    private void DisposeFmod()
    {
        // A per-session group is the ownership boundary. Stopping it first
        // guarantees that no old game channel survives into the next core,
        // even if FMOD has internally replaced/virtualised the channel handle.
        if (channelGroup.hasHandle()) channelGroup.setVolume(0f);
        if (channelGroup.hasHandle()) channelGroup.stop();
        if (channel.hasHandle()) { channel.stop(); channel.clearHandle(); }
        if (sound.hasHandle()) { sound.release(); sound.clearHandle(); }
        if (channelGroup.hasHandle()) { channelGroup.release(); channelGroup.clearHandle(); }
    }
}
