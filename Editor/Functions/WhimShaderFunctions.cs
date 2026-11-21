using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.Graphing.Util;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Serialization;
using UnityEditor.Rendering;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    internal static class WhimShaderFunctions
    {
        internal static GraphData UpgradeLegacy(GraphData original)
        {
            if (!original.GetNodes<CustomFunctionNode>().Any(n => n.GetType() == typeof(CustomFunctionNode))) return original;
            string json = MultiJson.Serialize(original);
            json = Regex.Replace(json, "(\"m_Type\"\\s*:\\s*\")UnityEditor.ShaderGraph.CustomFunctionNode(\")",
                "$1" + typeof(WhimShaderCustomFunctionNode).FullName + "$2");
            var result = new GraphData { assetGuid = original.assetGuid, messageManager = new MessageManager() };
            MultiJson.Deserialize(result, json); result.OnEnable();
            return result;
        }

        internal static readonly string[] PortTypes = { "Float", "Float2", "Float3", "Float4", "Boolean",
            "Matrix2x2", "Matrix3x3", "Matrix4x4", "Texture2D", "Texture3D", "Texture2DArray", "Cubemap", "SamplerState",
            "Bare Texture2D", "Bare Texture3D", "Bare Texture2DArray", "Bare Cubemap", "Bare SamplerState" };

        internal static MaterialSlot CreateSlot(WhimFunctionPort port)
        {
            string type = port.type.Replace("Bare ", "");
            var slotType = type switch
            {
                "Float" => SlotValueType.Vector1, "Float2" => SlotValueType.Vector2,
                "Float3" => SlotValueType.Vector3, "Float4" => SlotValueType.Vector4,
                "Boolean" => SlotValueType.Boolean, "Matrix2x2" => SlotValueType.Matrix2,
                "Matrix3x3" => SlotValueType.Matrix3, "Matrix4x4" => SlotValueType.Matrix4,
                "Texture2D" => SlotValueType.Texture2D, "Texture3D" => SlotValueType.Texture3D,
                "Texture2DArray" => SlotValueType.Texture2DArray, "Cubemap" => SlotValueType.Cubemap,
                "SamplerState" => SlotValueType.SamplerState,
                _ => throw new FormatException("Unsupported port type: " + port.type)
            };
            var slot = MaterialSlot.CreateMaterialSlot(slotType, port.id, port.name, port.name, port.direction, Vector4.zero);
            slot.bareResource = port.type.StartsWith("Bare ", StringComparison.Ordinal);
            return slot;
        }

        internal static string PortType(string hlsl) => hlsl switch
        {
            "float" or "half" or "float1" or "half1" => "Float",
            "float2" or "half2" => "Float2", "float3" or "half3" => "Float3", "float4" or "half4" => "Float4",
            "float2x2" or "half2x2" => "Matrix2x2", "float3x3" or "half3x3" => "Matrix3x3", "float4x4" or "half4x4" => "Matrix4x4",
            "bool" => "Boolean", "UnityTexture2D" => "Texture2D", "UnityTexture3D" => "Texture3D",
            "UnityTexture2DArray" => "Texture2DArray", "UnityTextureCube" => "Cubemap", "UnitySamplerState" => "SamplerState",
            "Texture2D" => "Bare Texture2D", "Texture3D" => "Bare Texture3D", "Texture2DArray" => "Bare Texture2DArray",
            "TextureCube" => "Bare Cubemap", "SamplerState" => "Bare SamplerState",
            _ => throw new FormatException("Unsupported HLSL type: " + hlsl)
        };

        internal static string Source(WhimFunctionDraft draft)
        {
            if (draft.mode != WhimFunctionSource.File) return draft.code ?? "";
            string path = AssetDatabase.GUIDToAssetPath(draft.fileGuid);
            string physical = PhysicalPath(path);
            if (string.IsNullOrEmpty(path) || !File.Exists(physical)) throw new FormatException("Choose an existing HLSL file.");
            if (!CustomFunctionNode.s_ValidExtensions.Contains(Path.GetExtension(path))) throw new FormatException("Choose an .hlsl, .cginc or .cg file.");
            return File.ReadAllText(physical);
        }

        static string PhysicalPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return "";
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            return package == null ? Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath) :
                Path.Combine(package.resolvedPath, assetPath.Substring(("Packages/" + package.name + "/").Length));
        }

        internal static string EmbedFile(WhimFunctionDraft draft)
        {
            string text = Source(draft);
            string directory = Path.GetDirectoryName(AssetDatabase.GUIDToAssetPath(draft.fileGuid));
            return Regex.Replace(text, "(?m)^(\\s*#\\s*include(?:_with_pragmas)?\\s*)\"([^\"]+)\"", match =>
            {
                string path = match.Groups[2].Value.Replace('\\', '/');
                if (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)) return match.Value;
                string root = Path.GetDirectoryName(Application.dataPath);
                string full = Path.GetFullPath(Path.Combine(root, directory, path)).Replace('\\', '/');
                string resolved = FileUtil.GetProjectRelativePath(full).Replace('\\', '/');
                if (string.IsNullOrEmpty(resolved) || Path.IsPathRooted(resolved)) throw new FormatException("Cannot embed an include outside the Unity project: " + path);
                return match.Groups[1].Value + "\"" + resolved + "\"";
            });
        }

        internal static WhimShaderFunctionDiscovery.Definition Definition(WhimFunctionDraft draft)
        {
            var definitions = WhimShaderFunctionDiscovery.Parse(Source(draft));
            var matches = definitions.Where(d => Key(d) == draft.entry).ToArray();
            if (matches.Length == 0)
            {
                string name = (draft.entry ?? "").Split('(')[0];
                matches = string.IsNullOrEmpty(name) ? definitions.ToArray() : definitions.Where(d => d.Name == name).ToArray();
            }
            if (matches.Length != 1) throw new FormatException("Select an unambiguous entry function from the HLSL source.");
            return matches[0];
        }

        internal static string Key(WhimShaderFunctionDiscovery.Definition definition) => definition.Name + "(" +
            string.Join(",", definition.Arguments.Select(a => a.Direction + " " + a.Type + " " + a.Name)) + ")";

        internal static void DiscoverPorts(WhimFunctionDraft draft)
        {
            var definition = Definition(draft);
            draft.entry = Key(definition);
            var previous = draft.ports ?? new();
            draft.portIdentities ??= new();
            foreach (var port in previous)
                if (!draft.portIdentities.Any(p => p.name == port.name && p.direction == port.direction))
                    draft.portIdentities.Add(new WhimFunctionPort { id = port.id, name = port.name, type = port.type, direction = port.direction });
            var ports = new List<WhimFunctionPort>();
            int next = draft.portIdentities.Count == 0 ? 0 : draft.portIdentities.Max(p => p.id) + 1;
            void Add(string name, string type, SlotType direction)
            {
                var old = draft.portIdentities.FirstOrDefault(p => p.name == name && p.direction == direction && !ports.Any(current => current.id == p.id));
                ports.Add(new WhimFunctionPort { id = old?.id ?? next++, name = name, type = PortType(type), direction = direction });
            }
            var reserved = new HashSet<string>(definition.Arguments.Select(a => a.Name), StringComparer.Ordinal);
            string Unique(string name) { while (!reserved.Add(name)) name += "_"; return name; }
            foreach (var arg in definition.Arguments)
            {
                if (arg.Direction != "out") Add(arg.Name, arg.Type, SlotType.Input);
                if (arg.Direction != "in") Add(arg.Direction == "inout" ? Unique(arg.Name + "Out") : arg.Name, arg.Type, SlotType.Output);
            }
            if (definition.ReturnType != "void")
            {
                string name = "Result";
                while (ports.Any(p => p.name == name)) name += "_";
                Add(name, definition.ReturnType, SlotType.Output);
            }
            draft.ports = ports;
        }

        internal static string Invocation(WhimFunctionDraft draft)
        {
            var definition = Definition(draft);
            var body = new StringBuilder();
            var args = new List<string>();
            var outputs = draft.ports.Where(p => p.direction == SlotType.Output).ToArray();
            int outputIndex = 0;
            foreach (var arg in definition.Arguments)
            {
                if (arg.Direction == "inout") { string output = outputs[outputIndex].name; body.AppendLine(output + " = " + arg.Name + ";"); args.Add(output); }
                else args.Add(arg.Name);
                if (arg.Direction != "in") outputIndex++;
            }
            if (definition.ReturnType != "void") body.Append(draft.ports.Last().name).Append(" = ");
            body.Append(definition.Name).Append('(').Append(string.Join(", ", args)).AppendLine(");");
            return body.ToString();
        }

        internal static void Validate(WhimFunctionDraft draft)
        {
            if (draft == null || draft.ports == null) throw new FormatException("Function settings are missing.");
            if (draft.ports.Count > 64) throw new FormatException("A function supports at most 64 ports.");
            if (!draft.ports.Any(p => p.direction == SlotType.Output)) throw new FormatException("Add at least one output port.");
            var names = new HashSet<string>(StringComparer.Ordinal); var ids = new HashSet<int>();
            foreach (var port in draft.ports)
            {
                if (!Regex.IsMatch(port.name ?? "", @"^[A-Za-z_][A-Za-z0-9_]*$")) throw new FormatException("Invalid port name: " + port.name);
                if (!names.Add(port.name) || !ids.Add(port.id)) throw new FormatException("Port names and IDs must be unique.");
                if (!PortTypes.Contains(port.type)) throw new FormatException("Unsupported port type: " + port.type);
            }
            if (string.IsNullOrWhiteSpace(Source(draft))) throw new FormatException("Function code is empty.");
            if (draft.mode != WhimFunctionSource.Body) Definition(draft);
        }

        internal static void CompileAndApply(WhimShaderCustomFunctionNode node)
        {
            var candidate = node.draft.Clone();
            if (candidate.mode != WhimFunctionSource.Body) DiscoverPorts(candidate);
            Validate(candidate);
            var graph = new GraphData { messageManager = new MessageManager() };
            Shader shader = null; Material material = null;
            try
            {
                MultiJson.Deserialize(graph, MultiJson.Serialize(node.owner)); graph.OnEnable();
                var copy = (WhimShaderCustomFunctionNode)graph.GetNodeFromId(node.objectId);
                copy.ApplyUnchecked(candidate); graph.ValidateGraph();
                // A texture/matrix output cannot be displayed as a preview color. Consume all
                // outputs in a numeric probe so the native compiler still checks the entire call.
                var probe = new CustomFunctionNode { sourceType = HlslSourceType.String, functionName = "WhimFunctionCheck", functionBody = "Result = 0;" };
                var outputs = copy.GetOutputSlots<MaterialSlot>().ToArray();
                for (int i = 0; i < outputs.Length; i++)
                    probe.AddSlot(MaterialSlot.CreateMaterialSlot(outputs[i].concreteValueType.ToSlotValueType(), i, "P" + i, "P" + i, SlotType.Input, Vector4.zero));
                probe.AddSlot(new Vector1MaterialSlot(outputs.Length, "Result", "Result", SlotType.Output, 0)); graph.AddNode(probe);
                for (int i = 0; i < outputs.Length; i++) graph.Connect(copy.GetSlotReference(outputs[i].id), probe.GetSlotReference(i));
                var relevant = new HashSet<string>(); var queue = new Queue<AbstractMaterialNode>(); queue.Enqueue(probe);
                while (queue.Count != 0)
                {
                    var dependency = queue.Dequeue(); if (!relevant.Add(dependency.objectId)) continue;
                    foreach (var input in dependency.GetInputSlots<MaterialSlot>())
                        foreach (var edge in graph.GetEdges(input.slotReference)) queue.Enqueue(edge.outputSlot.node);
                }
                var generator = new Generator(graph, probe, GenerationMode.Preview, "Hidden/WhimShader/FunctionCheck");
                if (graph.messageManager.HasSeverity(relevant.Contains, ShaderCompilerMessageSeverity.Error) || generator.allGeneratedShaders.Any(s => !string.IsNullOrEmpty(s.errorMessage)))
                    throw new FormatException("Shader Graph rejected the function or its input dependencies. Check the function signature, port names and upstream nodes.");
                shader = ShaderUtil.CreateShaderAsset(generator.generatedShader, false);
                if (shader == null) throw new InvalidOperationException("Unable to compile the function preview.");
                shader.hideFlags = HideFlags.HideAndDontSave;
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                for (int i = 0; i < material.passCount; i++) ShaderUtil.CompilePass(material, i, true);
                var errors = ShaderUtil.GetShaderMessages(shader).Where(m => m.severity == ShaderCompilerMessageSeverity.Error).ToArray();
                if (errors.Length != 0 || ShaderUtil.ShaderHasError(shader))
                    throw new FormatException(errors.Length == 0 ? "HLSL compilation failed." : string.Join("\n", errors.Select(m => m.message)));
            }
            finally
            {
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (shader != null) UnityEngine.Object.DestroyImmediate(shader);
                WhimLutGraphProperties.ReleasePreview(graph);
                graph.OnDisable();
            }
            node.owner.owner.RegisterCompleteObjectUndo("Compile Custom Function");
            node.ApplyUnchecked(candidate);
            node.owner.ClearErrorsForNode(node); node.owner.ValidateGraph(); node.owner.owner.isDirty = true;
        }
    }
}
