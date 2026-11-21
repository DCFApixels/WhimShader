// Copied from WhimTex. Copyright (c) 2026 Mikhail, MIT. Only model/path adapters differ.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    internal static class ShaderGraphCatalogThumbnails
    {
        internal const string Folder = "Packages/com.dcfapixels.whimshader/Editor/UI/Thumbnails/";
        internal const int DisplaySize = 32, RowHeight = 35;
        [Serializable] internal sealed class Manifest { public Entry[] entries; }
        [Serializable] internal sealed class Entry { public string kind; public Rect uv; }
        static Texture2D atlas;
        static Dictionary<string, Rect> regions;

        internal static bool TryGet(string kind, out Texture2D texture, out Rect uv)
        {
            texture = null; uv = default;
            if (string.IsNullOrEmpty(kind)) return false;
            if (regions == null)
            {
                regions = new();
                atlas = AssetDatabase.LoadAssetAtPath<Texture2D>(Folder + "Catalog.png");
                var json = AssetDatabase.LoadAssetAtPath<TextAsset>(Folder + "Catalog.json");
                if (atlas != null && json != null)
                {
                    var manifest = JsonUtility.FromJson<Manifest>(json.text);
                    if (manifest?.entries != null) foreach (var entry in manifest.entries)
                        regions[entry.kind] = entry.uv;
                }
            }
            if (atlas == null || !regions.TryGetValue(kind, out uv)) return false;
            texture = atlas; return true;
        }

        internal static float Height(string kind, float compactHeight) =>
            WhimShaderUserSettings.GraphCatalogThumbnails && TryGet(kind, out _, out _) ? RowHeight : compactHeight;

        internal static void Reset() { atlas = null; regions = null; }
        internal static ToolbarMenu CreateSelector(Action changed)
        {
            var menu = new ToolbarMenu { name = "catalog-display" };
            menu.style.flexShrink = 0;
            void Sync() { menu.text = WhimShaderUserSettings.GraphCatalogThumbnails ? "Thumbnails" : "Compact"; changed(); }
            menu.text = WhimShaderUserSettings.GraphCatalogThumbnails ? "Thumbnails" : "Compact";
            menu.menu.AppendAction("Compact", _ => WhimShaderUserSettings.GraphCatalogThumbnails = false,
                _ => !WhimShaderUserSettings.GraphCatalogThumbnails ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            menu.menu.AppendAction("Thumbnails", _ => WhimShaderUserSettings.GraphCatalogThumbnails = true,
                _ => WhimShaderUserSettings.GraphCatalogThumbnails ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            menu.RegisterCallback<AttachToPanelEvent>(_ => { WhimShaderUserSettings.CatalogDisplayChanged += Sync; Sync(); });
            menu.RegisterCallback<DetachFromPanelEvent>(_ => WhimShaderUserSettings.CatalogDisplayChanged -= Sync);
            return menu;
        }
    }

    internal sealed class ShaderGraphCatalogPreview : VisualElement
    {
        static StyleSheet sheet;
        readonly ShaderGraphCatalogIcon icon = new();
        readonly Image preview = new() { pickingMode = PickingMode.Ignore, scaleMode = ScaleMode.ScaleToFit };
        internal ShaderGraphCatalogPreview()
        {
            pickingMode = PickingMode.Ignore;
            AddToClassList("whimshader-graph-catalog-preview");
            if (sheet == null) sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(ShaderGraphCatalogThumbnails.Folder + "ShaderGraphCatalogThumbnails.uss");
            if (sheet != null) styleSheets.Add(sheet);
            Add(icon); Add(preview);
        }
        internal void Bind(ShaderGraphCatalogIcon.Appearance appearance)
        {
            icon.Bind(appearance);
            Texture2D texture = null; Rect uv = default;
            bool available = WhimShaderUserSettings.GraphCatalogThumbnails && ShaderGraphCatalogThumbnails.TryGet(appearance.kind, out texture, out uv);
            EnableInClassList("whimshader-graph-catalog-preview--large", available);
            if (available)
            {
                bool regionChanged = preview.image != texture || preview.uv != uv;
                preview.image = texture; preview.uv = uv;
                // Recycled rows can retain geometry when only the atlas region changes.
                if (regionChanged) preview.MarkDirtyRepaint();
            }
            else preview.image = null;
            preview.style.display = available ? DisplayStyle.Flex : DisplayStyle.None;
            icon.style.display = available ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
