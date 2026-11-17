using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace BR_Libretro;

internal static class HandheldRetailBoxTexture
{
    internal static Texture2D Create(string platformId, Texture2D cover, out Color bodyColor)
    {
        string resource = platformId switch
        {
            "gbc" => "BR_Libretro.Assets.gameboy_color_box_frame.png",
            "gba" => "BR_Libretro.Assets.gameboy_advance_box_frame.png",
            _ => "BR_Libretro.Assets.gameboy_box_frame.png"
        };

        Texture2D frame = Load(resource);
        bodyColor = AverageColor(cover, platformId);
        if (frame == null) return cover;

        Color32[] framePixels = frame.GetPixels32();
        Color32 background = bodyColor;
        var pixels = new Color32[framePixels.Length];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = background;

        FindArtworkOpening(framePixels, frame.width, frame.height,
            out int left, out int bottom, out int right, out int top);
        if (cover != null && right >= left && top >= bottom)
            DrawCoverToFill(pixels, frame.width, cover, left, bottom, right, top);

        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 overlay = framePixels[i];
            float alpha = overlay.a / 255f;
            pixels[i] = new Color32(
                (byte)Mathf.RoundToInt(Mathf.Lerp(pixels[i].r, overlay.r, alpha)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(pixels[i].g, overlay.g, alpha)),
                (byte)Mathf.RoundToInt(Mathf.Lerp(pixels[i].b, overlay.b, alpha)), 255);
        }

        var result = new Texture2D(frame.width, frame.height, TextureFormat.RGBA32, true);
        result.name = platformId.ToUpperInvariant() + " retail box front";
        result.SetPixels32(pixels);
        result.Apply(true, false);
        result.wrapMode = TextureWrapMode.Clamp;
        result.filterMode = FilterMode.Bilinear;
        UnityEngine.Object.Destroy(frame);
        return result;
    }

    internal static float Aspect(string platformId) => string.Equals(platformId, "gba", StringComparison.OrdinalIgnoreCase)
        ? 326f / 360f
        : 640f / 633f;

    internal static Texture2D LoadCardboardSurface() => Load("BR_Libretro.Assets.handheld_box_cardboard.png");
    internal static Texture2D LoadTopBottomSurface() => Load("BR_Libretro.Assets.handheld_box_top_bottom.png");

    private static Texture2D Load(string resource)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream == null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (texture.LoadImage(memory.ToArray())) return texture;
        UnityEngine.Object.Destroy(texture);
        return null;
    }

    private static void FindArtworkOpening(Color32[] frame, int width, int height,
        out int left, out int bottom, out int right, out int top)
    {
        left = width; bottom = height; right = -1; top = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if (frame[y * width + x].a > 24) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            bottom = Math.Min(bottom, y); top = Math.Max(top, y);
        }
    }

    private static void DrawCoverToFill(Color32[] destination, int width, Texture2D cover,
        int left, int bottom, int right, int top)
    {
        int openingWidth = right - left + 1;
        int openingHeight = top - bottom + 1;
        float openingAspect = openingWidth / (float)openingHeight;
        float coverAspect = cover.width / (float)cover.height;
        float uMin = 0f, uMax = 1f, vMin = 0f, vMax = 1f;
        if (coverAspect > openingAspect)
        {
            float visible = openingAspect / coverAspect;
            uMin = (1f - visible) * 0.5f;
            uMax = 1f - uMin;
        }
        else
        {
            float visible = coverAspect / openingAspect;
            vMin = (1f - visible) * 0.5f;
            vMax = 1f - vMin;
        }
        for (int y = 0; y < openingHeight; y++)
        for (int x = 0; x < openingWidth; x++)
        {
            float u = Mathf.Lerp(uMin, uMax, openingWidth <= 1 ? 0.5f : x / (float)(openingWidth - 1));
            float v = Mathf.Lerp(vMin, vMax, openingHeight <= 1 ? 0.5f : y / (float)(openingHeight - 1));
            destination[(bottom + y) * width + left + x] = cover.GetPixelBilinear(u, v);
        }
    }

    private static Color AverageColor(Texture2D cover, string platformId)
    {
        if (cover == null)
            return string.Equals(platformId, "gbc", StringComparison.OrdinalIgnoreCase)
                ? new Color(0.16f, 0.32f, 0.42f)
                : new Color(0.16f, 0.16f, 0.2f);

        Color sum = Color.black;
        int count = 0;
        const int samples = 12;
        for (int y = 0; y < samples; y++)
        for (int x = 0; x < samples; x++)
        {
            Color pixel = cover.GetPixelBilinear((x + 0.5f) / samples, (y + 0.5f) / samples);
            if (pixel.a < 0.1f) continue;
            sum += pixel; count++;
        }
        Color result = count == 0 ? new Color(0.16f, 0.16f, 0.2f) : sum / count;
        return Color.Lerp(result, Color.black, 0.28f);
    }
}
