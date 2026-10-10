using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BR_Libretro;

internal static class SnesEndLabelTexture
{
    private const int Width = 512;
    private const int Height = 44;

    internal static Texture2D Create(string title)
    {
        var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
        var pixels = new Color32[Width * Height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(0, 0, 0, 255);
        DrawTitle(pixels, CleanTitle(title));
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        texture.name = $"{title} SNES end label";
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        return texture;
    }

    private static string CleanTitle(string title)
    {
        string value = string.IsNullOrWhiteSpace(title) ? "SNES GAME" : title.Trim();
        int region = value.LastIndexOf(" (", StringComparison.Ordinal);
        if (region > 0 && value.EndsWith(")", StringComparison.Ordinal)) value = value.Substring(0, region);
        return value;
    }

    private static void DrawTitle(Color32[] pixels, string title)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return;
        IntPtr bitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero, font = IntPtr.Zero, oldFont = IntPtr.Zero;
        try
        {
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = Width, Height = -Height,
                    Planes = 1, BitCount = 32
                }
            };
            bitmap = CreateDIBSection(dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return;
            oldBitmap = SelectObject(dc, bitmap);
            Marshal.Copy(new byte[Width * Height * 4], 0, bits, Width * Height * 4);
            SetBkMode(dc, 1);
            // COLORREF is 0x00BBGGRR: #27AE60 becomes 0x0060AE27.
            SetTextColor(dc, 0x0060AE27);

            for (int pointSize = 32; pointSize >= 12; pointSize -= 2)
            {
                IntPtr candidate = CreateFontW(-pointSize, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
                if (candidate == IntPtr.Zero) break;
                IntPtr previous = SelectObject(dc, candidate);
                bool fits = GetTextExtentPoint32W(dc, title, title.Length, out NativeSize measured)
                    && measured.Width <= Width - 30;
                SelectObject(dc, previous);
                if (font != IntPtr.Zero) DeleteObject(font);
                font = candidate;
                if (fits) break;
            }
            if (font == IntPtr.Zero) return;
            oldFont = SelectObject(dc, font);
            var bounds = new NativeRect { Left = 15, Top = 0, Right = Width - 15, Bottom = Height };
            DrawTextW(dc, title, -1, ref bounds, 0x00000025);

            var bytes = new byte[Width * Height * 4];
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int source = ((Height - 1 - y) * Width + x) * 4;
                pixels[y * Width + x] = new Color32(bytes[source + 2], bytes[source + 1], bytes[source], 255);
            }
        }
        finally
        {
            if (oldFont != IntPtr.Zero) SelectObject(dc, oldFont);
            if (font != IntPtr.Zero) DeleteObject(font);
            if (oldBitmap != IntPtr.Zero) SelectObject(dc, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPixelsPerMeter, YPixelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info,
        uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeout, uint charset, uint outputPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetTextExtentPoint32W")]
    private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int length, out NativeSize measured);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DrawTextW")]
    private static extern int DrawTextW(IntPtr dc, string text, int length, ref NativeRect bounds, uint format);
}
