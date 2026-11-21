using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    internal enum WhimShaderPreviewMode { Flat, Height, ThreeD }
    internal enum WhimShaderPreviewChannel { RGBA, R, G, B, A }

    [Serializable]
    internal struct WhimShaderNodePreviewSettings
    {
        public WhimShaderPreviewMode mode;
        public WhimShaderPreviewChannel channel;
        public int outputSlotId;
        // Encoded defaults keep default(struct) and pre-existing graph entries backward compatible.
        // Zero-initialized serialized storage means legacy channel, .25 height and textured height.
        [SerializeField] int m_ChannelMaskPlusOne;
        [SerializeField] float m_HeightScale;
        [SerializeField] bool m_HasHeightScale;
        [SerializeField] bool m_HeightTextureDisabled;
        public HeightPreviewRenderer.Channel heightChannel;
        public bool highlightOutOfRange;

        public int channelMask
        {
            get => m_ChannelMaskPlusOne - 1;
            set => m_ChannelMaskPlusOne = Mathf.Clamp(value, -1, 15) + 1;
        }
        public int EffectiveMask => channelMask >= 0 ? channelMask : channel switch
        {
            WhimShaderPreviewChannel.R => 1, WhimShaderPreviewChannel.G => 2,
            WhimShaderPreviewChannel.B => 4, WhimShaderPreviewChannel.A => 8, _ => 15
        };
        public float heightScale
        {
            get => m_HasHeightScale ? m_HeightScale : .25f;
            set { m_HeightScale = float.IsFinite(value) ? Mathf.Clamp(value, -4, 4) : .25f; m_HasHeightScale = true; }
        }
        public bool heightTexture { get => !m_HeightTextureDisabled; set => m_HeightTextureDisabled = !value; }
    }

    // Native MultiJson stores this node with the graph. No generated slots/code or external settings asset.
    [Serializable]
    internal sealed class WhimShaderNodePresentationMetadata : AbstractMaterialNode
    {
        internal const string FinalId = "$final";
        [Serializable]
        internal sealed class Entry
        {
            public string nodeId;
            public WhimShaderNodePreviewSettings settings;
        }

        [SerializeField] internal List<Entry> previews = new();
        public WhimShaderNodePresentationMetadata() { name = string.Empty; }
        internal override bool ExposeToSearcher => false;
        public override bool hasPreview => false;
        public override bool canSetPrecision => false;
        public override bool canDeleteNode => false;
        public override bool canCutNode => false;
        public override bool canCopyNode => false;

        internal static WhimShaderNodePreviewSettings Read(AbstractMaterialNode node)
        {
            if (node?.owner == null) return default;
            var metadata = node.owner.GetNodes<WhimShaderNodePresentationMetadata>().FirstOrDefault();
            var entry = metadata?.previews?.FirstOrDefault(item => item != null && item.nodeId == node.objectId);
            return entry?.settings ?? new WhimShaderNodePreviewSettings
            { outputSlotId = node.GetOutputSlots<MaterialSlot>().FirstOrDefault()?.id ?? 0 };
        }

        internal static void Write(AbstractMaterialNode node, WhimShaderNodePreviewSettings settings)
            => Write(node.owner, node.objectId, settings);

        internal static WhimShaderNodePreviewSettings ReadFinal(GraphData graph)
        {
            var metadata = graph.GetNodes<WhimShaderNodePresentationMetadata>().FirstOrDefault();
            return metadata?.previews?.FirstOrDefault(item => item != null && item.nodeId == FinalId)?.settings ?? default;
        }

        internal static void WriteFinal(GraphData graph, WhimShaderNodePreviewSettings settings)
            => Write(graph, FinalId, settings);

        static void Write(GraphData graph, string id, WhimShaderNodePreviewSettings settings)
        {
            if (graph?.owner == null) return;
            graph.owner.RegisterCompleteObjectUndo("Change Node Preview Presentation");
            var metadata = graph.GetNodes<WhimShaderNodePresentationMetadata>().FirstOrDefault();
            if (metadata == null) { metadata = new WhimShaderNodePresentationMetadata(); graph.AddNode(metadata); }
            metadata.previews ??= new();
            metadata.previews.RemoveAll(item => item == null || string.IsNullOrEmpty(item.nodeId) || item.nodeId != FinalId && graph.GetNodeFromId(item.nodeId) == null);
            var entry = metadata.previews.FirstOrDefault(item => item.nodeId == id);
            if (entry == null) { entry = new Entry { nodeId = id }; metadata.previews.Add(entry); }
            entry.settings = settings;
            metadata.Dirty(ModificationScope.Layout);
            graph.owner.isDirty = true;
        }
    }
}
