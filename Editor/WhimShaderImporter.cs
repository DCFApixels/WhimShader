// Import flow follows Unity Shader Graph 17.7 ShaderGraphImporter.
// Copyright Unity Technologies. Unity Companion License; see ThirdPartyNotices.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEditor.Graphing.Util;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Serialization;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine;
using UnityEngine.Rendering.ShaderGraph;
using Object = UnityEngine.Object;

namespace DCFApixels.WhimShader
{
    [ScriptedImporter(1, "whimshader", -902)]
    internal sealed class WhimShaderImporter : ScriptedImporter
    {
        static string[] GatherDependenciesFromSourceFile(string path)
        {
            var dependencies = new AssetCollection();
            MinimalGraphData.GatherMinimalDependenciesFromFile(path, dependencies);
            return dependencies.assets.Where(a => a.Value.HasFlag(AssetCollection.Flags.ArtifactDependency))
                .Select(a => AssetDatabase.GUIDToAssetPath(a.Key)).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        }

        public override void OnImportAsset(AssetImportContext context)
        {
            WhimShaderURP.RequireVersion();
            var dependencies = new AssetCollection();
            MinimalGraphData.GatherMinimalDependenciesFromFile(context.assetPath, dependencies);
            var graph = new GraphData { assetGuid = AssetDatabase.AssetPathToGUID(context.assetPath), messageManager = new MessageManager() };
            MultiJson.Deserialize(graph, File.ReadAllText(context.assetPath));
            graph.OnEnable(); graph.ValidateGraph();
            try
            {
                WhimLutGraphProperties.BakeForImport(graph, context);
                WhimShaderMaterialMetadata.BakeMetadata(graph, context);
                string name = "WhimShader/" + Path.GetFileNameWithoutExtension(context.assetPath);
                var generator = new Generator(graph, graph.outputNode, GenerationMode.ForReals, name, assetCollection: dependencies);
                Shader main = null;
                foreach (var generated in generator.allGeneratedShaders)
                {
                    string code = generated.codeString;
                    bool Used(string id) => graph.GetNodeFromId(id)?.wasUsedByGenerator ?? false;
                    foreach (string error in graph.messageManager.ErrorStrings(Used)) context.LogImportError(error);
                    if (code == null || graph.messageManager.HasSeverity(Used, UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error))
                        code = ShaderGraphImporter.k_ErrorShader.Replace("Hidden/GraphErrorShader2", generated.shaderName);
                    var shader = ShaderUtil.CreateShaderAsset(context, code, false);
                    if (generated.assignedTextures != null)
                    {
                        var editable = generated.assignedTextures.Where(t => t.modifiable).ToArray();
                        var fixedTextures = generated.assignedTextures.Where(t => !t.modifiable).ToArray();
                        EditorMaterialUtility.SetShaderDefaults(shader, editable.Select(t => t.name).ToArray(),
                            editable.Select(t => EditorUtility.EntityIdToObject(t.textureId) as Texture).ToArray());
                        EditorMaterialUtility.SetShaderNonModifiableDefaults(shader, fixedTextures.Select(t => t.name).ToArray(),
                            fixedTextures.Select(t => EditorUtility.EntityIdToObject(t.textureId) as Texture).ToArray());
                    }
                    foreach (var diagnostic in ShaderUtil.GetShaderMessages(shader))
                        if (diagnostic.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error) context.LogImportError(diagnostic.message, shader);
                        else if (diagnostic.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Warning) context.LogImportWarning(diagnostic.message, shader);
                    if (main == null)
                    {
                        main = shader;
                        var material = new Material(main) { name = Path.GetFileNameWithoutExtension(context.assetPath) };
                        foreach (var target in graph.activeTargets) target.ProcessPreviewMaterial(material);
                        context.AddObjectToAsset("Material", material);
                        foreach (var target in graph.activeTargets)
                        {
                            Artifact(target as IHasImportArtifact); Artifact(target.activeSubTarget as IHasImportArtifact);
                        }
                        void Artifact(IHasImportArtifact provider)
                        {
                            var artifact = provider?.GetImportArtifact(graph, material, material.name);
                            if (artifact != null) context.AddObjectToAsset(artifact.GetType().Name, artifact);
                        }
                    }
                    else context.AddObjectToAsset("Shader-" + generated.shaderName, shader);
                }
                if (main == null) main = ShaderUtil.CreateShaderAsset(context, ShaderGraphImporter.k_ErrorShader, false);
                context.AddObjectToAsset("MainAsset", main, WhimShaderBranding.Icon); context.SetMainObject(main);
                var readOnly = new GraphDataReadOnly(graph);
                foreach (var target in graph.activeTargets)
                    if (target is IHasMetadata provider)
                    {
                        var metadata = provider.GetMetadataObject(readOnly);
                        if (metadata == null) continue;
                        metadata.hideFlags = HideFlags.HideInHierarchy;
                        context.AddObjectToAsset(provider.identifier + ":Metadata", metadata);
                    }
                AddGraphMetadata(context, graph, dependencies);
                foreach (var dependency in dependencies.assets)
                {
                    if (dependency.Value.HasFlag(AssetCollection.Flags.SourceDependency)) context.DependsOnSourceAsset(dependency.Key);
                    if (dependency.Value.HasFlag(AssetCollection.Flags.ArtifactDependency)) context.DependsOnArtifact(dependency.Key);
                }
            }
            finally { graph.OnDisable(); }
        }

        static void AddGraphMetadata(AssetImportContext context, GraphData graph, AssetCollection dependencies)
        {
            var metadata = ScriptableObject.CreateInstance<ShaderGraphMetadata>();
            metadata.hideFlags = HideFlags.HideInHierarchy;
            metadata.assetDependencies = new List<Object>();
            foreach (var dependency in dependencies.assets)
                if (dependency.Value.HasFlag(AssetCollection.Flags.IncludeInExportPackage))
                {
                    string path = AssetDatabase.GUIDToAssetPath(dependency.Key);
                    if (!string.IsNullOrEmpty(path)) metadata.assetDependencies.Add(AssetDatabase.LoadMainAssetAtPath(path));
                }
            var categories = new CategoryDataCollection();
            var seen = new HashSet<ShaderInput>(); int categoryOrder = 1, propertyOrder = 0;
            foreach (var category in graph.categories)
            {
                propertyOrder = 0;
                foreach (var input in category.Children)
                {
                    seen.Add(input);
                    if (MinimalCategoryData.TryProcessInput(input, out var data)) categories.Set(category.name, data, propertyOrder++, categoryOrder);
                }
                categoryOrder++;
            }
            foreach (var input in graph.properties.Cast<ShaderInput>().Concat(graph.keywords).Concat(graph.GetPromotedInputs()))
                if (seen.Add(input) && MinimalCategoryData.TryProcessInput(input, out var data)) categories.Set("", data, propertyOrder++, 0);
            metadata.categoryDatas = categories.GenerateMCD();
            context.AddObjectToAsset("SGInternal:Metadata", metadata);
        }
    }
}
