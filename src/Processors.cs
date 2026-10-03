using MelonLoader;
using SK.Libretro;
using SK.Libretro.Header;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BR_Libretro;

internal sealed class BrLogProcessor : ILogProcessor
{
    public bool SupportsColorTags => false;
    public void LogDebug(string message) => MelonLogger.Msg(message);
    public void LogInfo(string message) => MelonLogger.Msg(message);
    public void LogWarning(string message) => MelonLogger.Warning(message);
    public void LogError(string message) => MelonLogger.Error(message);
    public void LogException(Exception exception) => MelonLogger.Error(exception.ToString());
}

internal sealed class BrGraphicsProcessor : IGraphicsProcessor
{
    private readonly object gate = new();
    private byte[] pending;
    private int width, height;
    private bool firstFrameReceived;
    internal Texture2D Texture { get; private set; }

    public System.Threading.Tasks.ValueTask<IntPtr> GetCurrentSoftwareFramebuffer(int width, int height) => new(IntPtr.Zero);
    public void ProcessFrameSoftwareFramebuffer(IntPtr data, int pitch, int height) { }
    public void ProcessFrame0RGB1555VFlip(IntPtr data, int w, int h, int pitch) => Copy16(data, w, h, pitch, true, false);
    public void ProcessFrameRGB565VFlip(IntPtr data, int w, int h, int pitch) => Copy16(data, w, h, pitch, true, true);
    public void ProcessFrameXRGB8888(IntPtr data, int w, int h, int pitch) => Copy32(data, w, h, pitch, false);
    public void ProcessFrameXRGB8888VFlip(IntPtr data, int w, int h, int pitch) => Copy32(data, w, h, pitch, true);

    private void Copy32(IntPtr source, int w, int h, int pitch, bool flip)
    {
        if (source == IntPtr.Zero || w < 1 || h < 1) return;
        byte[] src = new byte[pitch * h], dst = new byte[w * h * 4];
        Marshal.Copy(source, src, 0, src.Length);
        for (int y = 0; y < h; y++)
        {
            int sy = flip ? h - 1 - y : y;
            Buffer.BlockCopy(src, sy * pitch, dst, y * w * 4, w * 4);
            for (int x = 3; x < w * 4; x += 4) dst[y * w * 4 + x] = 255;
        }
        Publish(dst, w, h);
    }

    private void Copy16(IntPtr source, int w, int h, int pitch, bool flip, bool rgb565)
    {
        if (source == IntPtr.Zero || w < 1 || h < 1) return;
        byte[] src = new byte[pitch * h], dst = new byte[w * h * 4];
        Marshal.Copy(source, src, 0, src.Length);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int sy = flip ? h - 1 - y : y, si = sy * pitch + x * 2, di = (y * w + x) * 4;
            ushort p = (ushort)(src[si] | src[si + 1] << 8);
            int r, g, b;
            if (rgb565) { r = (p >> 11) & 31; g = (p >> 5) & 63; b = p & 31; r = r * 255 / 31; g = g * 255 / 63; b = b * 255 / 31; }
            else { r = (p >> 10) & 31; g = (p >> 5) & 31; b = p & 31; r = r * 255 / 31; g = g * 255 / 31; b = b * 255 / 31; }
            dst[di] = (byte)b; dst[di + 1] = (byte)g; dst[di + 2] = (byte)r; dst[di + 3] = 255;
        }
        Publish(dst, w, h);
    }

    private void Publish(byte[] bytes, int w, int h)
    {
        lock (gate) { pending = bytes; width = w; height = h; }
        if (!firstFrameReceived)
        {
            firstFrameReceived = true;
            byte min = 255, max = 0;
            for (int i = 0; i < bytes.Length; i += 4)
                for (int c = 0; c < 3; c++) { byte value = bytes[i + c]; if (value < min) min = value; if (value > max) max = value; }
            MelonLogger.Msg($"First libretro video frame received ({w}x{h}, RGB range {min}-{max}).");
        }
    }
    internal bool Pump(out Texture2D texture)
    {
        byte[] frame; int w, h;
        lock (gate) { frame = pending; pending = null; w = width; h = height; }
        if (frame == null) { texture = Texture; return false; }
        if (Texture == null || Texture.width != w || Texture.height != h)
        {
            if (Texture != null) UnityEngine.Object.Destroy(Texture);
            Texture = new Texture2D(w, h, TextureFormat.BGRA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        }
        Texture.LoadRawTextureData(frame); Texture.Apply(false, false); texture = Texture; return true;
    }
    public void Dispose() { pending = null; if (Texture != null) UnityEngine.Object.Destroy(Texture); Texture = null; }
}

internal sealed class BrInputProcessor : IInputProcessor
{
    internal volatile bool Enabled;
    private volatile int buttons, leftX, leftY, rightX, rightY;
    public LeftStickBehaviour LeftStickBehaviour { get; set; } = LeftStickBehaviour.AnalogAndDigital;
    public short JoypadButton(int port, RETRO_DEVICE_ID_JOYPAD button) => Enabled && port == 0 && IsPressed(button) ? (short)1 : (short)0;
    public short JoypadButtons(int port) { short mask = 0; if (!Enabled || port != 0) return mask; for (int i = 0; i < 16; i++) if (IsPressed((RETRO_DEVICE_ID_JOYPAD)i)) mask |= (short)(1 << i); return mask; }
    internal void UpdateSnapshot()
    {
        Keyboard k = Keyboard.current; Gamepad g = Gamepad.current;
        int mask = 0;
        void Set(RETRO_DEVICE_ID_JOYPAD b, bool down) { if (down) mask |= 1 << (int)b; }
        for (int index = 0; index < 16; index++)
        {
            RETRO_DEVICE_ID_JOYPAD button = (RETRO_DEVICE_ID_JOYPAD)index;
            Set(button, InputBindings.IsPressed(button, k, g));
        }
        buttons = mask;
        leftX = Axis(g?.leftStick.x.ReadValue() ?? 0); leftY = Axis(-(g?.leftStick.y.ReadValue() ?? 0));
        rightX = Axis(g?.rightStick.x.ReadValue() ?? 0); rightY = Axis(-(g?.rightStick.y.ReadValue() ?? 0));
    }
    private bool IsPressed(RETRO_DEVICE_ID_JOYPAD b) => (buttons & 1 << (int)b) != 0;
    public short AnalogLeftX(int p) => Enabled && p == 0 ? (short)leftX : (short)0;
    public short AnalogLeftY(int p) => Enabled && p == 0 ? (short)leftY : (short)0;
    public short AnalogRightX(int p) => Enabled && p == 0 ? (short)rightX : (short)0;
    public short AnalogRightY(int p) => Enabled && p == 0 ? (short)rightY : (short)0;
    private static short Axis(float v) => (short)Mathf.Clamp(v * 32767f, short.MinValue, short.MaxValue);
    public short MouseX(int p) => 0; public short MouseY(int p) => 0; public short MouseWheel(int p) => 0; public short MouseButton(int p, RETRO_DEVICE_ID_MOUSE b) => 0;
    public short KeyboardKey(int p, retro_key k) => 0; public short LightgunX(int p) => 0; public short LightgunY(int p) => 0; public bool LightgunIsOffscreen(int p) => true;
    public short LightgunButton(int p, RETRO_DEVICE_ID_LIGHTGUN b) => 0; public short PointerX(int p) => 0; public short PointerY(int p) => 0; public short PointerPressed(int p) => 0; public short PointerCount(int p) => 0;
    public bool SetRumbleState(int p, retro_rumble_effect e, ushort s) => false;
}

internal sealed class NullLedProcessor : ILedProcessor { public void SetState(int led, int state) { } }
internal sealed class NullMessageProcessor : IMessageProcessor { public void Dispose() { } public void ShowNotification(string m,uint s,LogLevel l,uint p)=>MelonLogger.Msg(m); public void ShowNotificationAlt(string m,uint s,LogLevel l,uint p)=>MelonLogger.Msg(m); public void ShowStatus(string m)=>MelonLogger.Msg(m); public void ShowProgress(string m,sbyte p)=>MelonLogger.Msg($"{m} ({p}%)"); }
