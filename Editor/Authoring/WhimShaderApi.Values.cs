using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.Rendering.Universal.ShaderGraph;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    public static partial class WhimShaderApi
    {
        internal static float[] Numbers(JToken token, int count)
        {
            var values = token is JArray a ? a.ToArray() : Enumerable.Repeat(token, count).ToArray();
            Require(values.Length == count, "Expected " + count + " numeric components.");
            return values.Select(v => { Require(v.Type is JTokenType.Float or JTokenType.Integer, "Expected a number.");
                float f = (float)v; Require(float.IsFinite(f), "Numbers must be finite."); return f; }).ToArray();
        }
        static Vector4 Vector(JToken value, int count)
        {
            Require(count >= 1 && count <= 4, "Vectors require 1..4 components.");
            var v = Numbers(value, count); return new Vector4(v[0], count > 1 ? v[1] : 0, count > 2 ? v[2] : 0, count > 3 ? v[3] : 0);
        }
        static Matrix4x4 Matrix(JToken value, int size)
        {
            var data = Numbers(value, size * size); var result = Matrix4x4.zero;
            for (int r = 0; r < size; r++) for (int c = 0; c < size; c++) result[r, c] = data[r * size + c];
            return result;
        }
        static T Asset<T>(JToken value) where T : UnityEngine.Object
        {
            Require(value is JObject obj && obj.Count == 1 && obj["asset"]?.Type is JTokenType.String or JTokenType.Null, "Resource values use {asset: project/path}.");
            if (value["asset"].Type == JTokenType.Null) return null;
            var asset = AssetDatabase.LoadAssetAtPath<T>((string)value["asset"]);
            Require(asset != null, "Missing or wrong asset type: " + value["asset"]); return asset;
        }
        static void SetValue(MaterialSlot slot, JToken value)
        {
            switch (slot)
            {
                case BooleanMaterialSlot s: Require(value.Type == JTokenType.Boolean, "Expected true or false."); s.value = (bool)value; break;
                case Vector1MaterialSlot s: s.value = Numbers(value, 1)[0]; break;
                case Vector2MaterialSlot s: s.value = Vector(value, 2); break;
                case Vector3MaterialSlot s: s.value = Vector(value, 3); break;
                case Vector4MaterialSlot s: s.value = Vector(value, 4); break;
                case DynamicVectorMaterialSlot s: s.value = Vector(value, value is JArray v ? v.Count : 4); break;
                case DynamicValueMaterialSlot s: s.value = new Matrix4x4(Vector(value, value is JArray d ? d.Count : 4), Vector4.zero, Vector4.zero, Vector4.zero); break;
                case Matrix2MaterialSlot s: s.value = Matrix(value, 2); break;
                case Matrix3MaterialSlot s: s.value = Matrix(value, 3); break;
                case Matrix4MaterialSlot s: s.value = Matrix(value, 4); break;
                case DynamicMatrixMaterialSlot s: s.value = Matrix(value, 4); break;
                case Texture2DInputMaterialSlot s: s.texture = Asset<Texture2D>(value); break;
                case Texture3DInputMaterialSlot s: s.texture = Asset<Texture3D>(value); break;
                case Texture2DArrayInputMaterialSlot s: s.textureArray = Asset<Texture2DArray>(value); break;
                case CubemapInputMaterialSlot s: s.cubemap = Asset<Cubemap>(value); break;
                default: throw new FormatException("This input requires a connection: " + slot.RawDisplayName());
            }
            slot.owner.Dirty(ModificationScope.Node);
        }
        static JToken Value(MaterialSlot slot) => slot switch
        {
            IMaterialSlotHasValue<bool> s => new JValue(s.value), IMaterialSlotHasValue<float> s => new JValue(s.value),
            IMaterialSlotHasValue<Vector2> s => new JArray(s.value.x, s.value.y),
            IMaterialSlotHasValue<Vector3> s => new JArray(s.value.x, s.value.y, s.value.z),
            IMaterialSlotHasValue<Vector4> s => new JArray(s.value.x, s.value.y, s.value.z, s.value.w),
            Texture2DInputMaterialSlot s => new JObject { ["asset"] = AssetDatabase.GetAssetPath(s.texture) },
            _ => JValue.CreateNull()
        };

        // Unity serialization supplies node-specific settings without reflecting private members.
        // Shared node identity, slots, UI state and object references are never writable through this surface.
        static readonly HashSet<string> BaseFields = new(JObject.Parse(JsonUtility.ToJson(new WhimShaderNodePresentationMetadata())).Properties().Select(p => p.Name), StringComparer.Ordinal);
        static bool Simple(JToken token) => token.Type is JTokenType.Boolean or JTokenType.Integer or JTokenType.Float or JTokenType.String ||
            token is JObject obj && obj.Count > 0 && obj.Properties().All(p => new[] { "x", "y", "z", "w", "r", "g", "b", "a" }.Contains(p.Name) && p.Value.Type is JTokenType.Float or JTokenType.Integer);
        static JObject Configuration(AbstractMaterialNode node)
        {
            if (node is PropertyNode or SubGraphNode or CustomFunctionNode or BlockNode) return new JObject();
            return new JObject(JObject.Parse(JsonUtility.ToJson(node)).Properties().Where(p => !BaseFields.Contains(p.Name) && Simple(p.Value)).Select(p => new JProperty(p.Name, p.Value)));
        }
        static void Configure(AbstractMaterialNode node, JObject settings)
        {
            var available = Configuration(node); var patch = new JObject();
            foreach (var field in settings.Properties())
            {
                var matches = available.Properties().Where(p => Normalize(p.Name.StartsWith("m_") ? p.Name.Substring(2) : p.Name) == Normalize(field.Name)).ToArray();
                Require(matches.Length == 1, "Unknown setting '" + field.Name + "' on " + node.name + ". Use Catalog(node) for available settings.");
                var expected = matches[0];
                Require(Simple(field.Value) && (expected.Value.Type == field.Value.Type || expected.Value.Type == JTokenType.Float && field.Value.Type == JTokenType.Integer), "Wrong setting type: " + field.Name);
                foreach (var v in field.Value is JContainer c ? c.Descendants().OfType<JValue>() : new[] { (JValue)field.Value })
                    if (v.Type is JTokenType.Float or JTokenType.Integer) Numbers(v, 1);
                patch[expected.Name] = field.Value;
            }
            JsonUtility.FromJsonOverwrite(patch.ToString(Formatting.None), node);
            node.UpdateNodeAfterDeserialization(); node.Dirty(ModificationScope.Topological);
        }
        static JObject Describe(AbstractMaterialNode node)
        {
            JObject Slots(bool output) => new((output ? node.GetOutputSlots<MaterialSlot>() : node.GetInputSlots<MaterialSlot>()).Where(s => !s.hidden).Select(s =>
                new JProperty(s.RawDisplayName(), new JObject { ["id"] = s.id, ["type"] = s.valueType.ToString(), ["stage"] = s.stageCapability.ToString(), ["default"] = output ? null : Value(s) })));
            return new JObject { ["type"] = node.name, ["in"] = Slots(false), ["out"] = Slots(true),
                ["settings"] = new JObject(Configuration(node).Properties().Select(p => new JProperty(p.Name.StartsWith("m_") ? p.Name.Substring(2) : p.Name, p.Value))) };
        }
        static JObject Snapshot(GraphData graph, string path, bool details, JArray ids)
        {
            var names = Names(graph).GroupBy(p => p.Value).ToDictionary(g => g.Key, g => g.First().Key);
            string Id(AbstractMaterialNode n) => names.TryGetValue(n.objectId, out var name) ? name : ShortId(graph, n);
            var nodes = new JObject();
            var selected = ids == null ? null : new HashSet<string>(ids.Select(id => Resolve(graph, (string)id).objectId));
            foreach (var n in graph.GetNodes<AbstractMaterialNode>().Where(WhimShaderArrangement.Visible).Where(n => selected == null || selected.Contains(n.objectId)))
            {
                if (n is BlockNode block)
                {
                    string stage = block.descriptor.shaderStage == ShaderStage.Vertex ? "vertex" : "fragment";
                    if (!details) nodes[stage] = block.descriptor.shaderStage.ToString();
                    else
                    {
                        var entry = nodes[stage] as JObject ?? new JObject { ["type"] = block.descriptor.shaderStage.ToString(), ["in"] = new JObject() };
                        ((JObject)entry["in"])[block.descriptor.displayName] = Value(block.FindInputSlot<MaterialSlot>(0)); nodes[stage] = entry;
                    }
                    continue;
                }
                if (!details) { nodes[Id(n)] = n.name; continue; }
                var detail = Describe(n); detail.Remove("out");
                detail["in"] = new JObject(n.GetInputSlots<MaterialSlot>().Where(s => !s.hidden && !s.isConnected && !s.isDefaultValue).Select(s => new JProperty(s.RawDisplayName(), Value(s))));
                if (n is PropertyNode pn) detail["property"] = pn.property.referenceName;
                if (n is BlockNode b) detail["stage"] = b.descriptor.shaderStage.ToString();
                nodes[Id(n)] = detail;
            }
            string EndpointName(SlotReference slot) => slot.node is BlockNode b ? (b.descriptor.shaderStage == ShaderStage.Vertex ? "vertex." : "fragment.") + b.descriptor.displayName :
                Id(slot.node) + "." + slot.node.FindSlot<MaterialSlot>(slot.slotId).RawDisplayName();
            var result = new JObject { ["path"] = path, ["revision"] = Revision(graph), ["nodes"] = nodes,
                ["edges"] = new JArray(graph.edges.Where(e => selected == null || selected.Contains(e.outputSlot.node.objectId) || selected.Contains(e.inputSlot.node.objectId)).Select(e => new JArray(EndpointName(e.outputSlot), EndpointName(e.inputSlot)))) };
            if (details) result["parameters"] = new JArray(graph.properties.Select(p => new JObject { ["id"] = p.objectId, ["name"] = p.displayName, ["reference"] = p.referenceName, ["type"] = p.GetPropertyTypeString(), ["value"] = ReadProperty(p) }));
            return result;
        }
        static AbstractShaderProperty FindProperty(GraphData graph, string name)
        {
            string id = graph.GetNodes<WhimShaderNodePresentationMetadata>().FirstOrDefault()?.authoringParameters.FirstOrDefault(p => p.name == name)?.nodeId;
            if (id != null && graph.properties.FirstOrDefault(p => p.objectId == id) is { } aliased) return aliased;
            var found = graph.properties.Where(p => p.objectId == name || p.referenceName == name || p.displayName == name).ToArray();
            Require(found.Length == 1, "Unknown or ambiguous parameter: " + name); return found[0];
        }
        static void Parameters(GraphData graph, JObject parameters)
        {
            foreach (var item in parameters.Properties())
            {
                Require(item.Value is JObject, "Parameter definition must be an object."); var spec = (JObject)item.Value;
                Keys(spec, "type", "name", "reference", "value", "exposed");
                var metadata = graph.GetNodes<WhimShaderNodePresentationMetadata>().FirstOrDefault();
                string existingId = metadata?.authoringParameters.FirstOrDefault(p => p.name == item.Name)?.nodeId;
                var property = graph.properties.FirstOrDefault(p => p.referenceName == item.Name || p.displayName == item.Name || p.objectId == item.Name || p.objectId == existingId);
                if (property == null)
                {
                    property = ((string)spec["type"] ?? "Float") switch {
                        "Float" => new Vector1ShaderProperty(), "Float2" or "Vector2" => new Vector2ShaderProperty(), "Float3" or "Vector3" => new Vector3ShaderProperty(),
                        "Float4" or "Vector4" => new Vector4ShaderProperty(), "Color" => new ColorShaderProperty(), "Boolean" => new BooleanShaderProperty(),
                        "Texture2D" => new Texture2DShaderProperty(), "Texture3D" => new Texture3DShaderProperty(), "Texture2DArray" => new Texture2DArrayShaderProperty(), "Cubemap" => new CubemapShaderProperty(),
                        "Matrix2" => new Matrix2ShaderProperty(), "Matrix3" => new Matrix3ShaderProperty(), "Matrix4" => new Matrix4ShaderProperty(),
                        "Gradient" => new GradientShaderProperty(), "Gradient LUT" => new WhimGradientShaderProperty(), "Curve LUT" => new WhimCurveShaderProperty(),
                        _ => throw new FormatException("Unsupported parameter type: " + spec["type"]) };
                    property.displayName = (string)spec["name"] ?? item.Name;
                    property.overrideReferenceName = (string)spec["reference"] ?? "_" + item.Name;
                    Require(System.Text.RegularExpressions.Regex.IsMatch(property.referenceName, @"\A[A-Za-z_][A-Za-z0-9_]*\z"), "Invalid parameter reference.");
                    Require(!graph.properties.Any(p => p.referenceName == property.referenceName), "Duplicate parameter reference: " + property.referenceName);
                    graph.AddGraphInput(property);
                    if (metadata == null) { metadata = new WhimShaderNodePresentationMetadata(); graph.AddNode(metadata); }
                    metadata.authoringParameters.RemoveAll(p => p.name == item.Name || !graph.properties.Any(prop => prop.objectId == p.nodeId));
                    metadata.authoringParameters.Add(new() { name = item.Name, nodeId = property.objectId });
                }
                else Require(spec["type"] == null && spec["name"] == null && spec["reference"] == null, "Existing parameter updates accept value and exposed; omit type/name/reference.");
                if (spec["exposed"] != null) property.generatePropertyBlock = (bool)spec["exposed"];
                if (spec["value"] is JToken value) PropertyValue(property, value);
                foreach (var node in graph.GetNodes<PropertyNode>().Where(n => n.property == property)) node.Dirty(ModificationScope.Graph);
            }
        }
        static void PropertyValue(AbstractShaderProperty property, JToken value)
        {
            switch (property)
            {
                case WhimLutShaderProperty p:
                    var data = p.Data;
                    if (p is WhimGradientShaderProperty) data.gradient = Gradient(value);
                    else data.curve = Curve(value);
                    p.SetData(data); break;
                case Vector1ShaderProperty p: p.value = Numbers(value, 1)[0]; break;
                case Vector2ShaderProperty p: p.value = Vector(value, 2); break;
                case Vector3ShaderProperty p: p.value = Vector(value, 3); break;
                case Vector4ShaderProperty p: p.value = Vector(value, 4); break;
                case ColorShaderProperty p: p.value = (Color)Vector(value, 4); break;
                case BooleanShaderProperty p: Require(value.Type == JTokenType.Boolean, "Expected boolean parameter value."); p.value = (bool)value; break;
                case Texture2DShaderProperty p: p.value.texture = Asset<Texture2D>(value); break;
                case Texture3DShaderProperty p: p.value.texture = Asset<Texture3D>(value); break;
                case Texture2DArrayShaderProperty p: p.value.textureArray = Asset<Texture2DArray>(value); break;
                case CubemapShaderProperty p: p.value.cubemap = Asset<Cubemap>(value); break;
                case Matrix2ShaderProperty p: p.value = Matrix(value, 2); break;
                case Matrix3ShaderProperty p: p.value = Matrix(value, 3); break;
                case Matrix4ShaderProperty p: p.value = Matrix(value, 4); break;
                case GradientShaderProperty p: p.value = Gradient(value); break;
                default: throw new FormatException("Unsupported parameter value: " + property.GetPropertyTypeString());
            }
        }
        static JToken ReadProperty(AbstractShaderProperty property)
        {
            JArray MatrixValues(Matrix4x4 matrix, int size) => new(Enumerable.Range(0, size * size).Select(i => matrix[i / size, i % size]));
            JObject Resource(UnityEngine.Object asset) => new() { ["asset"] = asset == null ? null : AssetDatabase.GetAssetPath(asset) };
            return property switch {
                Vector1ShaderProperty p => new JValue(p.value), Vector2ShaderProperty p => new JArray(p.value.x, p.value.y),
                Vector3ShaderProperty p => new JArray(p.value.x, p.value.y, p.value.z), Vector4ShaderProperty p => new JArray(p.value.x, p.value.y, p.value.z, p.value.w),
                ColorShaderProperty p => new JArray(p.value.r, p.value.g, p.value.b, p.value.a), BooleanShaderProperty p => new JValue(p.value),
                WhimLutShaderProperty p => JObject.Parse(JsonUtility.ToJson(p.Data)),
                Texture2DShaderProperty p => Resource(p.value.texture), Texture3DShaderProperty p => Resource(p.value.texture),
                Texture2DArrayShaderProperty p => Resource(p.value.textureArray), CubemapShaderProperty p => Resource(p.value.cubemap),
                Matrix2ShaderProperty p => MatrixValues(p.value, 2), Matrix3ShaderProperty p => MatrixValues(p.value, 3), Matrix4ShaderProperty p => MatrixValues(p.value, 4),
                _ => JValue.CreateNull() };
        }
        static Gradient Gradient(JToken value)
        {
            Require(value is JArray a && a.Count >= 2 && a.Count <= 8, "Gradient needs 2..8 [time,r,g,b,a] keys.");
            var keys = ((JArray)value).Select(k => Numbers(k, 5)).OrderBy(k => k[0]).ToArray();
            Require(keys.All(k => k[0] >= 0 && k[0] <= 1), "Gradient times must be in 0..1.");
            var gradient = new Gradient(); gradient.SetKeys(keys.Select(k => new GradientColorKey(new Color(k[1], k[2], k[3], k[4]), k[0])).ToArray(),
                keys.Select(k => new GradientAlphaKey(k[4], k[0])).ToArray()); return gradient;
        }
        static AnimationCurve Curve(JToken value)
        {
            Require(value is JArray a && a.Count >= 2 && a.Count <= 256, "Curve needs 2..256 [time,value,inTangent,outTangent] keys.");
            return new AnimationCurve(((JArray)value).Select(k => { var v = Numbers(k, 4); return new Keyframe(v[0], v[1], v[2], v[3]); }).ToArray());
        }
        static T EnumValue<T>(JToken value) where T : struct, Enum
        {
            Require(value.Type == JTokenType.String && Enum.TryParse<T>((string)value, true, out var _) && Enum.IsDefined(typeof(T), Enum.Parse<T>((string)value, true)),
                "Expected " + string.Join(" / ", Enum.GetNames(typeof(T)))); return Enum.Parse<T>((string)value, true);
        }
        static void Settings(GraphData graph, JObject settings)
        {
            Keys(settings, "surface", "blend", "alphaClip", "castShadows", "receiveShadows", "materialOverride");
            var target = graph.activeTargets.OfType<UniversalTarget>().Single();
            if (settings["surface"] != null) target.surfaceType = EnumValue<UnityEditor.Rendering.Universal.ShaderGraph.SurfaceType>(settings["surface"]);
            if (settings["blend"] != null) target.alphaMode = EnumValue<UnityEditor.Rendering.Universal.ShaderGraph.AlphaMode>(settings["blend"]);
            if (settings["alphaClip"] != null) target.alphaClip = (bool)settings["alphaClip"];
            if (settings["castShadows"] != null) target.castShadows = (bool)settings["castShadows"];
            if (settings["receiveShadows"] != null) target.receiveShadows = (bool)settings["receiveShadows"];
            if (settings["materialOverride"] != null) target.allowMaterialOverride = (bool)settings["materialOverride"];
            graph.UpdateActiveBlocks(graph.GetActiveBlocksForAllActiveTargets());
        }
        static void Function(AbstractMaterialNode node, JObject function)
        {
            Require(node is WhimShaderCustomFunctionNode, "function requires Custom Function.");
            Keys(function, "mode", "code", "file", "entry", "ports", "title");
            var n = (WhimShaderCustomFunctionNode)node; var draft = n.draft.Clone();
            if (function["mode"] != null) draft.mode = EnumValue<WhimFunctionSource>(function["mode"]);
            if (function["code"] != null) draft.code = (string)function["code"];
            if (function["title"] != null) draft.title = (string)function["title"];
            if (function["file"] != null) { draft.fileGuid = AssetDatabase.AssetPathToGUID((string)function["file"]); Require(!string.IsNullOrEmpty(draft.fileGuid), "HLSL file not found."); }
            if (function["entry"] != null) draft.entry = (string)function["entry"];
            if (function["ports"] is JArray ports)
            {
                int next = draft.ports.Select(p => p.id).DefaultIfEmpty(-1).Max() + 1;
                draft.ports = ports.Select(p => {
                    Require(p is JObject, "Each function port must be an object."); Keys((JObject)p, "name", "type", "direction");
                    string name = (string)p["name"]; var direction = EnumValue<SlotType>(p["direction"]);
                    return new WhimFunctionPort { name = name, type = (string)p["type"] ?? "Float", direction = direction,
                        id = n.draft.ports.FirstOrDefault(old => old.name == name && old.direction == direction)?.id ?? next++ }; }).ToList();
            }
            n.draft = draft; WhimShaderFunctions.CompileAndApply(n);
        }
    }
}
