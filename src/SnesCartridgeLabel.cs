using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BR_Libretro;

internal static class SnesCartridgeLabel
{
    // Bounds of the recessed front-label panel, expressed as fractions of the
    // sanitized model bounds. The source texture used only a small sticker
    // island near the cartridge edge; BOXROOM artwork fills the actual panel.
    private const float CentreX = 0.49f;
    private const float CentreY = 0.72f;
    private const float Width = 0.64f;
    // 0.46 of this model's Y bounds makes the physical decal approximately
    // 92:43 when paired with the 0.64 X width above.
    private const float Height = 0.46f;

    internal static Renderer Create(
        Transform parent,
        Bounds modelBounds,
        Material template,
        out Mesh mesh,
        out Material material)
    {
        var label = new GameObject("BR-Libretro SNES Artwork Label");
        label.transform.SetParent(parent, false);

        float centreX = Mathf.Lerp(modelBounds.min.x, modelBounds.max.x, CentreX);
        float centreY = Mathf.Lerp(modelBounds.min.y, modelBounds.max.y, CentreY);
        float width = modelBounds.size.x * Width;
        float height = modelBounds.size.y * Height;
        float surfaceOffset = Mathf.Max(modelBounds.size.z * 0.006f, modelBounds.size.magnitude * 0.0005f);
        // After the GLB's authored node transform, the cartridge's broad face
        // is XY and its thin axis is Z. The source label geometry faces +Z.
        float z = modelBounds.max.z + surfaceOffset;
        float x0 = centreX - width * 0.5f;
        float x1 = centreX + width * 0.5f;
        float y0 = centreY - height * 0.5f;
        float y1 = centreY + height * 0.5f;

        mesh = new Mesh { name = "BR-Libretro SNES Artwork Decal" };
        mesh.vertices = new[]
        {
            new Vector3(x0, y0, z),
            new Vector3(x0, y1, z),
            new Vector3(x1, y1, z),
            new Vector3(x1, y0, z)
        };
        mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
        mesh.uv = new[]
        {
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f),
            new Vector2(0f, 0f)
        };
        mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
        mesh.RecalculateBounds();

        label.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = label.AddComponent<MeshRenderer>();
        material = new Material(template) { name = "BR_Libretro_SNESCoverArt" };
        material.mainTexture = null;
        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
        renderer.sharedMaterial = material;
        return renderer;
    }

    internal static Renderer CreateEndLabel(
        Transform parent,
        Bounds modelBounds,
        Material template,
        string title,
        out Mesh mesh,
        out Material material,
        out Texture2D texture)
    {
        var label = new GameObject("BR-Libretro SNES End Label");
        label.transform.SetParent(parent, false);

        float centreX = Mathf.Lerp(modelBounds.min.x, modelBounds.max.x, CentreX);
        float width = modelBounds.size.x * Width;
        float x0 = centreX - width * 0.5f;
        float x1 = centreX + width * 0.5f;
        // NTSC SNES labels use a shallow top fold (about 7 mm of the complete
        // 44 mm label). Cover only the narrow front-side lip so this reads as
        // a continuation of the artwork rather than a separate end-face slab.
        float z0 = Mathf.Lerp(modelBounds.min.z, modelBounds.max.z, 0.62f);
        float z1 = modelBounds.max.z;
        float surfaceOffset = Mathf.Max(modelBounds.size.y * 0.006f, modelBounds.size.magnitude * 0.0005f);
        float y = modelBounds.max.y + surfaceOffset;

        mesh = new Mesh { name = "BR-Libretro SNES End Label Decal" };
        mesh.vertices = new[]
        {
            new Vector3(x0, y, z0),
            new Vector3(x0, y, z1),
            new Vector3(x1, y, z1),
            new Vector3(x1, y, z0)
        };
        mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        mesh.uv = new[]
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 1f),
            new Vector2(1f, 0f)
        };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();

        label.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = label.AddComponent<MeshRenderer>();
        texture = SnesEndLabelTexture.Create(title);
        material = new Material(template) { name = "BR_Libretro_SNESEndLabel" };
        material.mainTexture = texture;
        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
        foreach (string property in new[] { "_MainTex", "_BaseMap", "_BaseColorTexture" })
            if (material.HasProperty(property))
            {
                material.SetTexture(property, texture);
                material.SetTextureScale(property, Vector2.one);
                material.SetTextureOffset(property, Vector2.zero);
            }
        renderer.sharedMaterial = material;
        return renderer;
    }

    internal static Renderer CreateWrapBridge(
        Transform parent,
        Bounds modelBounds,
        Material template,
        out Mesh mesh,
        out Material material)
    {
        var bridge = new GameObject("BR-Libretro SNES Label Wrap Bridge");
        bridge.transform.SetParent(parent, false);

        float centreX = Mathf.Lerp(modelBounds.min.x, modelBounds.max.x, CentreX);
        float width = modelBounds.size.x * Width;
        float x0 = centreX - width * 0.5f;
        float x1 = centreX + width * 0.5f;
        float artworkTop = Mathf.Lerp(modelBounds.min.y, modelBounds.max.y, CentreY + Height * 0.5f);
        float frontOffset = Mathf.Max(modelBounds.size.z * 0.006f, modelBounds.size.magnitude * 0.0005f);
        float endOffset = Mathf.Max(modelBounds.size.y * 0.006f, modelBounds.size.magnitude * 0.0005f);
        float frontZ = modelBounds.max.z + frontOffset;
        float endY = modelBounds.max.y + endOffset;

        mesh = new Mesh { name = "BR-Libretro SNES Label Wrap Bridge Decal" };
        mesh.vertices = new[]
        {
            // Continue the black label from the top of the 92:43 artwork to
            // the cartridge corner on the broad front face.
            new Vector3(x0, artworkTop, frontZ),
            new Vector3(x0, modelBounds.max.y, frontZ),
            new Vector3(x1, modelBounds.max.y, frontZ),
            new Vector3(x1, artworkTop, frontZ),
            // Skin the bevel between the broad face and the shallow end face.
            // Without this second quad the cartridge's grey corner remains
            // visible as a disconnected strip between the two label pieces.
            new Vector3(x0, modelBounds.max.y, frontZ),
            new Vector3(x0, endY, modelBounds.max.z),
            new Vector3(x1, endY, modelBounds.max.z),
            new Vector3(x1, modelBounds.max.y, frontZ)
        };
        Vector3 bevelNormal = new Vector3(0f, 1f, 1f).normalized;
        mesh.normals = new[]
        {
            Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward,
            bevelNormal, bevelNormal, bevelNormal, bevelNormal
        };
        mesh.uv = new[]
        {
            Vector2.zero, Vector2.up, Vector2.one, Vector2.right,
            Vector2.zero, Vector2.up, Vector2.one, Vector2.right
        };
        mesh.triangles = new[]
        {
            0, 2, 1, 0, 3, 2,
            4, 6, 5, 4, 7, 6
        };
        mesh.RecalculateBounds();

        bridge.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = bridge.AddComponent<MeshRenderer>();
        material = new Material(template) { name = "BR_Libretro_SNESLabelWrapBridge" };
        // The imported glTF shader does not consistently honor Unity's
        // conventional colour properties. Feed it a genuinely black texture
        // just like the generated end label so the bridge cannot fall back to
        // the cartridge's neutral-grey base material.
        material.mainTexture = Texture2D.blackTexture;
        if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
        foreach (string property in new[] { "_MainTex", "_BaseMap", "_BaseColorTexture" })
            if (material.HasProperty(property))
            {
                material.SetTexture(property, Texture2D.blackTexture);
                material.SetTextureScale(property, Vector2.one);
                material.SetTextureOffset(property, Vector2.zero);
            }
        renderer.sharedMaterial = material;
        return renderer;
    }
}

internal static class SnesLabelArtCache
{
    private sealed class Entry
    {
        internal Texture2D Texture;
        internal int References;
    }

    private static readonly Dictionary<int, Entry> Entries = new();

    internal static Texture2D Acquire(int appId)
    {
        if (Entries.TryGetValue(appId, out Entry existing))
        {
            existing.References++;
            return existing.Texture;
        }

        string path = Path.Combine(
            Application.persistentDataPath,
            "steam_cache_v2",
            appId.ToString(),
            "label.png");
        if (!File.Exists(path)) return null;

        try
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!texture.LoadImage(File.ReadAllBytes(path), true))
            {
                Object.Destroy(texture);
                return null;
            }
            texture.name = $"BR-Libretro SNES label {appId}";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            Entries[appId] = new Entry { Texture = texture, References = 1 };
            return texture;
        }
        catch
        {
            return null;
        }
    }

    internal static void Release(int appId, Texture2D texture)
    {
        if (texture == null || !Entries.TryGetValue(appId, out Entry entry) || entry.Texture != texture)
            return;
        entry.References--;
        if (entry.References > 0) return;
        Entries.Remove(appId);
        Object.Destroy(texture);
    }
}
