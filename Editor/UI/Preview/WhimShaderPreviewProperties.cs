using System.Collections.Generic;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    internal sealed class WhimShaderPreviewProperties
    {
        internal readonly MaterialPropertyBlock Block = new();
        readonly List<PreviewProperty> values = new();
        readonly List<PreviewProperty> virtualTextures = new();

        internal void Rebuild(GraphData graph, IEnumerable<BlockNode> temporaryBlocks)
        {
            Block.Clear(); values.Clear(); virtualTextures.Clear();
            WhimLutGraphProperties.PreparePreview(graph);
            foreach (var node in graph.GetNodes<AbstractMaterialNode>()) node.CollectPreviewMaterialProperties(values);
            if (temporaryBlocks != null)
                foreach (var node in temporaryBlocks) node.CollectPreviewMaterialProperties(values);
            foreach (var property in graph.properties) values.Add(property.GetPreviewMaterialProperty());
            foreach (var value in values)
            {
                if (string.IsNullOrEmpty(value.name)) continue;
                value.SetValueOnMaterialPropertyBlock(Block);
                if (value.propType == PropertyType.VirtualTexture) virtualTextures.Add(value);
                if (value.propType == PropertyType.Texture2D && value.textureValue != null)
                {
                    var texture = value.textureValue;
                    Block.SetVector(value.name + "_TexelSize", new Vector4(1f / texture.width, 1f / texture.height, texture.width, texture.height));
                }
            }
        }

        internal void SetTime(float seconds)
        {
            Block.SetVector("_TimeParameters", new Vector4(seconds, Mathf.Sin(seconds), Mathf.Cos(seconds), 0));
            Block.SetVector("_Time", new Vector4(seconds / 20, seconds, seconds * 2, seconds * 3));
            Block.SetVector("_SinTime", new Vector4(Mathf.Sin(seconds / 8), Mathf.Sin(seconds / 4), Mathf.Sin(seconds / 2), Mathf.Sin(seconds)));
            Block.SetVector("_CosTime", new Vector4(Mathf.Cos(seconds / 8), Mathf.Cos(seconds / 4), Mathf.Cos(seconds / 2), Mathf.Cos(seconds)));
        }

        internal void ApplyMaterialOnlyProperties(Material material)
        {
            // Native virtual-texture layer assignments cannot use MPBs. URP disables streaming VT;
            // these bindings preserve its ordinary texture fallback on our owned node material.
            foreach (var property in virtualTextures)
            {
                var layers = property.vtProperty?.value?.layers;
                if (layers == null) continue;
                foreach (var layer in layers)
                    if (material.HasProperty(layer.layerRefName)) material.SetTexture(layer.layerRefName, layer.layerTexture?.texture);
            }
        }
    }
}
