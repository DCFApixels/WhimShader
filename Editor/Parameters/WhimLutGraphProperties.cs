using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    /// <summary>Call these hooks on Unity's main thread. Import and preview never write the graph file.</summary>
    [InitializeOnLoad]
    static class WhimLutGraphProperties
    {
        sealed class PreviewEntry
        {
            public WeakReference<WhimLutShaderProperty> property;
            public string dataJson;
            public Texture2D texture;
        }

        static readonly List<PreviewEntry> previews = new List<PreviewEntry>();

        static WhimLutGraphProperties()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseAllPreviews;
            EditorApplication.quitting += ReleaseAllPreviews;
        }

        /// <summary>Invoke after graph validation/promotion and before the native Generator collects properties.</summary>
        public static IReadOnlyList<WhimLutAsset> BakeForImport(GraphData graph, AssetImportContext context)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (context == null) throw new ArgumentNullException(nameof(context));
            var assets = new List<WhimLutAsset>();
            foreach (var property in Properties(graph))
            {
                RequireReferenceName(property);
                var asset = CreateAsset(property);
                string identifier = "WhimLut:" + property.objectId;
                context.AddObjectToAsset(identifier + ":Data", asset);
                context.AddObjectToAsset(identifier + ":Texture", asset.Texture);
                property.value = new SerializableTexture { texture = asset.Texture };
                assets.Add(asset);
            }
            return assets;
        }

        public static void PreparePreview(GraphData graph)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            foreach (var property in Properties(graph)) GetPreviewTexture(property);
        }

        public static void ReleasePreview(GraphData graph)
        {
            if (graph == null) return;
            var owned = new HashSet<WhimLutShaderProperty>(Properties(graph));
            for (int i = previews.Count - 1; i >= 0; --i)
                if (!previews[i].property.TryGetTarget(out var property) || owned.Contains(property))
                    ReleaseAt(i);
        }

        internal static Texture2D GetPreviewTexture(WhimLutShaderProperty property)
        {
            var data = property.Data;
            string json = JsonUtility.ToJson(data);
            PreviewEntry found = null;
            for (int i = previews.Count - 1; i >= 0; --i)
            {
                var entry = previews[i];
                if (!entry.property.TryGetTarget(out var candidate)) ReleaseAt(i);
                else if (ReferenceEquals(property, candidate)) found = entry;
            }
            if (found != null && found.texture != null && found.dataJson == json) return found.texture;
            if (found == null)
            {
                found = new PreviewEntry { property = new WeakReference<WhimLutShaderProperty>(property) };
                previews.Add(found);
            }
            if (found.texture != null && found.texture.width == data.resolution)
                WhimLutTexture.Update(found.texture, data);
            else
            {
                var replacement = WhimLutTexture.Create(data, property.displayName + " Preview");
                replacement.hideFlags = HideFlags.HideAndDontSave;
                WhimLutTexture.DestroyOwned(found.texture);
                found.texture = replacement;
            }
            found.dataJson = json;
            return found.texture;
        }

        /// <summary>Creates independent editable LUT assets; does not alter the graph or overwrite existing assets.</summary>
        public static IReadOnlyList<WhimLutAsset> ExportDefaults(GraphData graph, string assetFolder)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (string.IsNullOrEmpty(assetFolder) ||
                !(assetFolder == "Assets" || assetFolder.StartsWith("Assets/", StringComparison.Ordinal)) ||
                !AssetDatabase.IsValidFolder(assetFolder))
                throw new ArgumentException("Choose an existing writable folder under Assets.", nameof(assetFolder));

            var assets = new List<WhimLutAsset>();
            foreach (var property in Properties(graph))
            {
                RequireReferenceName(property);
                var asset = CreateAsset(property);
                string file = WhimLutMaterialStore.SafeFileName(property.referenceName) + ".lut.asset";
                string destination = AssetDatabase.GenerateUniqueAssetPath(assetFolder.TrimEnd('/') + "/" + file);
                WhimLutMaterialStore.SaveNewAsset(asset, destination);
                assets.Add(asset);
            }
            return assets;
        }

        static WhimLutAsset CreateAsset(WhimLutShaderProperty property)
        {
            var data = property.Data;
            data.Validate();
            var texture = WhimLutTexture.Create(data, property.referenceName + " LUT");
            var asset = ScriptableObject.CreateInstance<WhimLutAsset>();
            asset.name = property.displayName + " LUT Data";
            asset.SetData(data);
            asset.SetBinding(texture, null, property.referenceName);
            return asset;
        }

        static IEnumerable<WhimLutShaderProperty> Properties(GraphData graph)
        {
            var seen = new HashSet<string>();
            foreach (var input in graph.properties)
                if (input is WhimLutShaderProperty property && seen.Add(property.objectId)) yield return property;
            // Match native promotion discovery while allowing the preview of an incomplete graph.
            foreach (var node in graph.GetNodes<SubGraphNode>())
            {
                var asset = node.asset;
                if (asset == null) continue;
                foreach (var input in asset.nodeProperties)
                    if (input is WhimLutShaderProperty property && property.promoteToFinalShader && seen.Add(property.objectId))
                        yield return property;
            }
        }

        static void RequireReferenceName(WhimLutShaderProperty property)
        {
            if (string.IsNullOrEmpty(property.referenceName))
                throw new InvalidOperationException("Validate graph property reference names before baking LUT defaults.");
        }

        static void ReleaseAt(int index)
        {
            WhimLutTexture.DestroyOwned(previews[index].texture);
            previews.RemoveAt(index);
        }

        static void ReleaseAllPreviews()
        {
            for (int i = previews.Count - 1; i >= 0; --i) ReleaseAt(i);
        }
    }
}
