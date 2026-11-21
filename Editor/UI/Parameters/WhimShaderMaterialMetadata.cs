using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.Rendering.Universal.ShaderGraph;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    [Serializable]
    internal sealed class WhimShaderMaterialParameter
    {
        public string propertyId;
        public string displayName;
        public string referenceName;
        public List<string> referenceNames = new();
    }

    // Editor-only layout snapshot: an importer subasset, or a sidecar for exported .shader files.
    internal sealed class WhimShaderMaterialMetadata : ScriptableObject
    {
        internal const string ArtifactKey = "WhimShader:MaterialLayout";
        internal const string ExportSuffix = ".whimshader-layout.asset";
        [SerializeField] internal int version = WhimShaderParameterLayout.CurrentVersion;
        [SerializeField] internal WhimShaderParameterLayout layout;
        [SerializeField] internal List<WhimShaderMaterialParameter> parameters = new();
        [SerializeField] internal List<string> otherInputReferences = new();
        [SerializeField] internal bool unlit;
        internal bool Supported => version == WhimShaderParameterLayout.CurrentVersion && layout?.Supported == true;

        // Importer hook: call once per import, after graph validation and LUT baking; before graph.OnDisable().
        // No AssetDatabase writes: the active ScriptedImporter owns this artifact via AddObjectToAsset.
        internal static WhimShaderMaterialMetadata BakeMetadata(GraphData graph, AssetImportContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var metadata = CreateMetadata(graph);
            context.AddObjectToAsset(ArtifactKey, metadata);
            return metadata;
        }

        internal static WhimShaderMaterialMetadata CreateMetadata(GraphData graph)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            var layout = WhimShaderParameterLayoutNode.Find(graph)?.layout?.Copy() ?? WhimShaderParameterLayout.FromGraph(graph);
            if (!layout.Supported) throw new InvalidOperationException("Unsupported WhimShader parameter layout version.");
            layout.Normalize(graph);
            var metadata = CreateInstance<WhimShaderMaterialMetadata>();
            metadata.name = "WhimShader Material Layout";
            metadata.hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable | HideFlags.DontSaveInBuild;
            metadata.layout = layout;
            metadata.unlit = graph.activeTargets.Any(target => target.activeSubTarget is UniversalUnlitSubTarget);
            foreach (var property in graph.properties)
            {
                var parameter = new WhimShaderMaterialParameter
                { propertyId = property.objectId, displayName = property.displayName, referenceName = property.referenceName };
                property.GetPropertyReferenceNames(parameter.referenceNames);
                metadata.parameters.Add(parameter);
            }
            // Keywords, dropdowns and promoted subgraph inputs keep their native material drawers.
            var mapped = new HashSet<string>(metadata.parameters.SelectMany(p => p.referenceNames));
            foreach (var input in graph.keywords.Cast<ShaderInput>().Concat(graph.dropdowns).Concat(graph.GetPromotedInputs()))
            {
                var references = new List<string>();
                if (input is AbstractShaderProperty property) property.GetPropertyReferenceNames(references);
                else references.Add(input.referenceName);
                foreach (string reference in references)
                    if (!string.IsNullOrEmpty(reference) && mapped.Add(reference)) metadata.otherInputReferences.Add(reference);
            }
            return metadata;
        }

        // AssetDatabase path, e.g. Assets/Shaders/Foo.shader.whimshader-layout.asset.
        internal static string ExportMetadataPath(string shaderPath) => shaderPath.Replace('\\', '/') + ExportSuffix;

        internal static WhimShaderMaterialMetadata ExportMetadata(GraphData graph, string shaderPath)
        {
            if (string.IsNullOrWhiteSpace(shaderPath)) throw new ArgumentException("A shader asset path is required.", nameof(shaderPath));
            shaderPath = shaderPath.Replace('\\', '/');
            if (Path.IsPathRooted(shaderPath)) shaderPath = FileUtil.GetProjectRelativePath(shaderPath).Replace('\\', '/');
            if (!shaderPath.StartsWith("Assets/", StringComparison.Ordinal) || shaderPath.Split('/').Any(part => part is "." or "..") ||
                !shaderPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Export metadata requires a .shader path inside Assets.", nameof(shaderPath));
            string path = ExportMetadataPath(shaderPath);
            if (!AssetDatabase.IsValidFolder(Path.GetDirectoryName(path)?.Replace('\\', '/')))
                throw new DirectoryNotFoundException("Shader export folder does not exist: " + path);
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null && existing is not WhimShaderMaterialMetadata || existing == null && File.Exists(path))
                throw new InvalidOperationException("Refusing to replace an unrelated or unreadable metadata asset: " + path);
            var snapshot = CreateMetadata(graph);
            try
            {
                if (existing is WhimShaderMaterialMetadata metadata)
                {
                    // Retain the existing sidecar's GUID and object identity.
                    EditorUtility.CopySerialized(snapshot, metadata);
                    EditorUtility.SetDirty(metadata);
                    AssetDatabase.SaveAssetIfDirty(metadata);
                    return metadata;
                }
                AssetDatabase.CreateAsset(snapshot, path);
                var result = snapshot; snapshot = null; // AssetDatabase now owns this object.
                return result;
            }
            finally { if (snapshot != null) DestroyImmediate(snapshot); }
        }

        internal static WhimShaderMaterialMetadata Find(Shader shader)
        {
            if (shader == null) return null;
            string path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                if (asset is WhimShaderMaterialMetadata metadata) return metadata;
            return path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)
                ? AssetDatabase.LoadAssetAtPath<WhimShaderMaterialMetadata>(ExportMetadataPath(path)) : null;
        }
    }
}
