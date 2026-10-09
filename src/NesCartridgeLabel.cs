using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BR_Libretro;

internal static class NesCartridgeLabel
{
    private const int TextureSize = 512;
    private const int TitleWidth = 300;
    private const int TitleHeight = 44;

    internal static Texture2D Create(string title, Texture artwork)
    {
        var texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false);
        var pixels = new Color32[TextureSize * TextureSize];
        for (int y = 0; y < TextureSize; y++)
        for (int x = 0; x < TextureSize; x++)
        {
            bool border = x < 9 || x > 308 || y < 9 || y > 500;
            bool spine = y < 52;
            pixels[y * TextureSize + x] = spine && x <= 309
                ? new Color32(0, 0, 0, 255)
                : border ? new Color32(12, 17, 20, 255) : new Color32(27, 41, 48, 255);
        }

        Texture2D readable = MakeReadable(artwork, out bool ownsReadable);
        try
        {
            if (readable != null)
            {
                const int faceX = 10, faceY = 55, faceWidth = 300, faceHeight = 446;
                Color32[] source = readable.GetPixels32();
                for (int y = 0; y < faceHeight; y++)
                for (int x = 0; x < faceWidth; x++)
                {
                    int sourceX = Mathf.Min(readable.width - 1, x * readable.width / faceWidth);
                    int sourceY = readable.height - 1 - Mathf.Min(readable.height - 1, y * readable.height / faceHeight);
                    Color32 color = source[sourceY * readable.width + sourceX];
                    color.a = 255;
                    pixels[(faceY + y) * TextureSize + faceX + x] = color;
                }
            }
        }
        finally
        {
            if (ownsReadable && readable != null) UnityEngine.Object.Destroy(readable);
        }

        string displayTitle = CleanTitle(title).ToUpperInvariant();
        DrawTitle(pixels, displayTitle);
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        texture.name = displayTitle + " NES cartridge label";
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        return texture;
    }

    private static string CleanTitle(string title)
    {
        string value = string.IsNullOrWhiteSpace(title) ? "NES GAME" : title.Trim();
        int region = value.LastIndexOf(" (", StringComparison.Ordinal);
        if (region > 0 && value.EndsWith(")", StringComparison.Ordinal)) value = value.Substring(0, region);
        return value;
    }

    private static Texture2D MakeReadable(Texture source, out bool ownsTexture)
    {
        ownsTexture = false;
        if (source is Texture2D texture && texture.isReadable) return texture;
        if (source == null) return null;

        RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        RenderTexture previous = RenderTexture.active;
        try
        {
            UnityEngine.Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            copy.Apply(false, false);
            ownsTexture = true;
            return copy;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
        }
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
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = TitleWidth, Height = -TitleHeight,
                    Planes = 1, BitCount = 32
                }
            };
            bitmap = CreateDIBSection(dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return;
            oldBitmap = SelectObject(dc, bitmap);
            Marshal.Copy(new byte[TitleWidth * TitleHeight * 4], 0, bits, TitleWidth * TitleHeight * 4);
            SetBkMode(dc, 1);
            SetTextColor(dc, 0x00F5F5F5);

            for (int pointSize = 27; pointSize >= 13; pointSize--)
            {
                IntPtr candidate = CreateFontW(-pointSize, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
                if (candidate == IntPtr.Zero) break;
                IntPtr previous = SelectObject(dc, candidate);
                bool fits = GetTextExtentPoint32W(dc, title, title.Length, out NativeSize measured)
                    && measured.Width <= TitleWidth - 14;
                SelectObject(dc, previous);
                if (font != IntPtr.Zero) DeleteObject(font);
                font = candidate;
                if (fits) break;
            }
            if (font == IntPtr.Zero) return;
            oldFont = SelectObject(dc, font);
            var bounds = new NativeRect { Left = 7, Top = 0, Right = TitleWidth - 7, Bottom = TitleHeight };
            DrawTextW(dc, title, -1, ref bounds, 0x00008825);

            var bytes = new byte[TitleWidth * TitleHeight * 4];
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            for (int y = 0; y < TitleHeight; y++)
            for (int x = 0; x < TitleWidth; x++)
            {
                int source = (y * TitleWidth + x) * 4;
                pixels[(8 + y) * TextureSize + 10 + x] =
                    new Color32(bytes[source + 2], bytes[source + 1], bytes[source], 255);
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
