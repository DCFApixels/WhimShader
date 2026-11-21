// Native pass composition targets Unity URP 17.7.0. See Shaders/Unity-NOTICE.md.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering.Universal;
using UnityEditor.Rendering.Universal.ShaderGraph;
using UnityEditor.ShaderGraph;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using static Unity.Rendering.Universal.ShaderUtils;

namespace DCFApixels.WhimShader
{
    [GenerateBlocks("WhimShader")]
    internal struct WhimSurfaceBlocks
    {
        public static readonly BlockFieldDescriptor Color = new BlockFieldDescriptor(
            "SurfaceDescription", "WhimColor", "Color", "SURFACEDESCRIPTION_WHIMCOLOR",
            new ColorControl(UnityEngine.Color.grey, true), ShaderStage.Fragment);
        public static readonly BlockFieldDescriptor BakedAlbedo = new BlockFieldDescriptor(
            "SurfaceDescription", "WhimBakedAlbedo", "Baked Albedo", "SURFACEDESCRIPTION_WHIMBAKEDALBEDO",
            new ColorControl(UnityEngine.Color.grey, false), ShaderStage.Fragment);
        public static readonly BlockFieldDescriptor BakedEmission = new BlockFieldDescriptor(
            "SurfaceDescription", "WhimBakedEmission", "Baked Emission", "SURFACEDESCRIPTION_WHIMBAKEDEMISSION",
            new ColorControl(UnityEngine.Color.black, true), ShaderStage.Fragment);
    }

    internal sealed class WhimSurfaceSubTarget : UniversalSubTarget
    {
        internal const string PackageRoot = "Packages/com.dcfapixels.whimshader/";
        internal const string ShaderRoot = PackageRoot + "Shaders/";
        const string NativeRoot = "Packages/com.unity.render-pipelines.universal/";
        const string NativeIncludes = NativeRoot + "Editor/ShaderGraph/Includes/";

        internal static readonly BlockFieldDescriptor[] FragmentBlocks =
        {
            WhimSurfaceBlocks.Color,
            BlockFields.SurfaceDescription.Alpha,
            BlockFields.SurfaceDescription.AlphaClipThreshold,
            BlockFields.SurfaceDescription.NormalWS,
            BlockFields.SurfaceDescription.Smoothness,
        };

        internal static readonly BlockFieldDescriptor[] InitialBlocks =
        {
            BlockFields.VertexDescription.Position, BlockFields.VertexDescription.Normal,
            BlockFields.VertexDescription.Tangent, WhimSurfaceBlocks.Color,
            BlockFields.SurfaceDescription.Alpha, BlockFields.SurfaceDescription.AlphaClipThreshold,
        };

        static readonly BlockFieldDescriptor[] OptionalBlocks =
        {
            BlockFields.SurfaceDescription.NormalWS, BlockFields.SurfaceDescription.Smoothness,
            WhimSurfaceBlocks.BakedAlbedo, WhimSurfaceBlocks.BakedEmission,
        };

        public WhimSurfaceSubTarget() { displayName = "Whim Surface"; }
        public override int latestVersion => 1;
        protected override ShaderID shaderID => ShaderID.SG_Lit;
        public override bool IsActive() => true;
        internal override bool supportsStencilOverride => true;

        // Compose the current engine's descriptors instead of freezing its keyword tables.
        // Never install this helper as the graph's active subtarget.
        UniversalLitSubTarget NativeLit() => new UniversalLitSubTarget
        {
            target = target,
            normalDropOffSpace = NormalDropOffSpace.World,
            workflowMode = WorkflowMode.Metallic,
            clearCoat = false,
            blendModePreserveSpecular = false,
        };

        public override void Setup(ref TargetSetupContext context)
        {
            WhimShaderURP.RequireVersion();
            target.customEditorGUI = WhimShaderMaterialGUI.CustomEditorName;
            base.Setup(ref context);
            var nativeContext = new TargetSetupContext(context.assetCollection);
            // This isolated context must carry the override before Lit installs its default.
            // SetupFinalize embeds it in each copied SubShader, including older saved graphs.
            nativeContext.AddCustomEditorForRenderPipeline(target.customEditorGUI, typeof(UniversalRenderPipelineAsset));
            NativeLit().Setup(ref nativeContext);
            nativeContext.SetupFinalize();
            foreach (var original in nativeContext.subShaders)
            {
                var subShader = original;
                // Forward-only opaque objects must not be classified as deferred Lit.
                subShader.customTags = UniversalTarget.kComplexLitMaterialTypeTag;
                subShader.passes = new PassCollection();
                var forward = original.passes.First(p => p.descriptor.lightMode == "UniversalForward").descriptor;
                foreach (var item in original.passes)
                {
                    var pass = item.descriptor;
                    if (pass.lightMode == "UniversalGBuffer" || pass.lightMode == "Universal2D") continue;
                    bool isForward = pass.lightMode == "UniversalForward";
                    bool isMeta = pass.lightMode == "Meta";
                    if (isForward)
                    {
                        pass.displayName = "Whim Forward";
                        pass.lightMode = "UniversalForwardOnly";
                        // Keep SHADERPASS_FORWARD: native vertex GI paths key off this value.
                        pass.validPixelBlocks = FragmentBlocks;
                    }
                    else if (pass.lightMode == "DepthNormals")
                        pass.lightMode = "DepthNormalsOnly";
                    else if (isMeta)
                        pass.validPixelBlocks = new[] { WhimSurfaceBlocks.BakedAlbedo,
                            WhimSurfaceBlocks.BakedEmission, BlockFields.SurfaceDescription.Alpha,
                            BlockFields.SurfaceDescription.AlphaClipThreshold };

                    // Alpha may depend on a lighting node. Give auxiliary passes the same
                    // context and variants instead of silently substituting preview lighting.
                    pass.requiredFields = MergeFields(pass.requiredFields, forward.requiredFields);
                    pass.keywords = MergeKeywords(pass.keywords, forward.keywords);
                    pass.defines = pass.defines == null ? new DefineCollection() : new DefineCollection(pass.defines);
                    if (!target.receiveShadows && !target.allowMaterialOverride)
                        pass.defines.Add(new KeywordDescriptor { referenceName = "_RECEIVE_SHADOWS_OFF",
                            type = KeywordType.Boolean, definition = KeywordDefinition.Predefined }, 1);
                    pass.includes = BuildIncludes(pass.includes, forward.includes);
                    subShader.passes.Add(pass, item.fieldConditions);
                }
                context.AddSubShader(subShader);
            }
            foreach (string file in new[] { "WhimLightingContext.hlsl", "WhimLighting.hlsl",
                "WhimContextFromVaryings.hlsl", "WhimVaryings.hlsl", "WhimForwardPass.hlsl", "WhimMetaPass.hlsl" })
            {
                string guid = AssetDatabase.AssetPathToGUID(ShaderRoot + file);
                if (!string.IsNullOrEmpty(guid))
                    context.AddAssetDependency(new GUID(guid), AssetCollection.Flags.SourceDependency);
            }
            string scriptGuid = AssetDatabase.AssetPathToGUID(PackageRoot + "Editor/URP/WhimSurfaceSubTarget.cs");
            if (!string.IsNullOrEmpty(scriptGuid))
                context.AddAssetDependency(new GUID(scriptGuid), AssetCollection.Flags.SourceDependency);
        }

        static FieldCollection MergeFields(FieldCollection first, FieldCollection second)
        {
            var result = new FieldCollection();
            var seen = new HashSet<FieldDescriptor>();
            foreach (var collection in new[] { first, second })
                if (collection != null) foreach (var item in collection)
                    if (seen.Add(item.field)) result.Add(item.field);
            return result;
        }

        static KeywordCollection MergeKeywords(KeywordCollection first, KeywordCollection second)
        {
            var result = new KeywordCollection();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var collection in new[] { first, second })
                if (collection != null) foreach (var item in collection)
                    if (seen.Add(item.descriptor.referenceName)) result.Add(item.descriptor, item.fieldConditions);
            return result;
        }

        static IncludeCollection BuildIncludes(IncludeCollection original, IncludeCollection forward)
        {
            var result = new IncludeCollection();
            // APV/DOTS/render-layer pragma includes must precede the core library everywhere.
            foreach (var include in forward)
                if (include.location == IncludeLocation.Pregraph) Copy(include);
            foreach (var include in original)
                if (include.location == IncludeLocation.Pregraph) Copy(include);
            result.Add(ShaderRoot + "WhimLightingContext.hlsl", IncludeLocation.Pregraph);
            foreach (var include in original)
            {
                if (include.location != IncludeLocation.Postgraph) continue;
                string replacement = include.path == NativeIncludes + "Varyings.hlsl" ? "WhimVaryings.hlsl"
                    : include.path == NativeIncludes + "PBRForwardPass.hlsl" ? "WhimForwardPass.hlsl"
                    : include.path == NativeIncludes + "LightingMetaPass.hlsl" ? "WhimMetaPass.hlsl" : null;
                if (replacement != null)
                    result.Add(ShaderRoot + replacement, include.location, include.fieldConditions, include.shouldIncludeWithPragmas);
                else Copy(include);
            }
            return result;

            void Copy(IncludeDescriptor include) => result.AddInternal(include.guid, include.path,
                include.location, include.fieldConditions, include.shouldIncludeWithPragmas);
        }

        public override void GetFields(ref TargetFieldContext context) => NativeLit().GetFields(ref context);
        public override void GetActiveBlocks(ref TargetActiveBlockContext context)
        {
            // UniversalTarget inserts BaseColor before invoking the subtarget.
            context.activeBlocks.Remove(BlockFields.SurfaceDescription.BaseColor);
            context.AddBlock(WhimSurfaceBlocks.Color);
            context.AddBlock(BlockFields.SurfaceDescription.Alpha);
            context.AddBlock(BlockFields.SurfaceDescription.AlphaClipThreshold);
            // Do not let native auto-add populate optional blocks in the editor.
            // During pass generation, still provide virtual defaults for required fields.
            foreach (var optional in OptionalBlocks)
                context.AddBlock(optional, context.pass.HasValue || context.currentBlocks.Contains(optional));
            context.AddBlock(UniversalBlockFields.VertexDescription.MotionVector,
                target.additionalMotionVectorMode == AdditionalMotionVectorMode.Custom);
        }
        public override void CollectShaderProperties(PropertyCollector collector, GenerationMode generationMode)
        {
            NativeLit().CollectShaderProperties(collector, generationMode);
            if (!generationMode.IsPreview()) return;

            // SG 17.7 gathers subshader properties with pass=null, before creating per-pass
            // virtual blocks. Our compact stack deliberately omits those blocks from that
            // query, so supply their preview uniforms here using the native slot descriptors.
            foreach (var descriptor in OptionalBlocks)
            {
                var block = new BlockNode();
                block.Init(descriptor); // Detached: never add a hidden node to the graph.
                var defaults = new PropertyCollector();
                block.FindInputSlot<MaterialSlot>(0).AddDefaultProperty(defaults, generationMode);
                foreach (var property in defaults.properties)
                {
                    // Real blocks have already contributed their properties. Preserve their
                    // values and let PreviewManager/its MPB continue updating them normally.
                    if (collector.properties.Any(existing => existing.referenceName == property.referenceName))
                        continue;
                    // PreviewManager only tracks its own temporary blocks. A hidden ShaderLab
                    // property initializes our detached defaults on every new preview Material,
                    // without a service-side patch or resetting values in ProcessPreviewMaterial.
                    property.generatePropertyBlock = true;
                    property.hidden = true;
                    collector.AddShaderProperty(property);
                }
            }
        }
        public override void ProcessPreviewMaterial(Material material) => NativeLit().ProcessPreviewMaterial(material);
        public override void GetPropertiesGUI(ref TargetPropertyGUIContext context, Action onChange, Action<string> registerUndo)
        {
            target.AddDefaultMaterialOverrideGUI(ref context, onChange, registerUndo);
            target.AddDefaultSurfacePropertiesGUI(ref context, onChange, registerUndo, showReceiveShadows: true);
        }
    }
}
