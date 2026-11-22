using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEngine;

namespace DCFApixels.WhimShader
{
    internal enum WhimFunctionSource { Body, File, Source }

    [Serializable]
    internal sealed class WhimFunctionPort
    {
        public int id;
        public string name = "In";
        public string type = "Float";
        public SlotType direction;
    }

    [Serializable]
    internal sealed class WhimFunctionDraft
    {
        public WhimFunctionSource mode;
        public string title = "Custom Function";
        public string code = "Out = In;";
        public string fileGuid;
        public string entry;
        public bool usePragmas = true;
        public List<WhimFunctionPort> ports = new()
        {
            new() { id = 0, name = "In" },
            new() { id = 1, name = "Out", direction = SlotType.Output }
        };
        public List<WhimFunctionPort> portIdentities = new();
        internal WhimFunctionDraft Clone() => JsonUtility.FromJson<WhimFunctionDraft>(JsonUtility.ToJson(this));
    }

    [Serializable, Title("HLSL", "Custom Function"), HasDependencies(typeof(WhimShaderCustomFunctionNode.Dependencies))]
    internal sealed class WhimShaderCustomFunctionNode : CustomFunctionNode, IGeneratesFunction
    {
        internal override bool ExposeToSearcher => false;

        [SerializeField] internal WhimFunctionDraft draft = new();
        [SerializeField] internal string appliedSource;
        [SerializeField] internal string appliedInclude;
        [SerializeField] internal bool includePragmas;
        [SerializeField] internal string appliedTitle = "Custom Function";
        [SerializeField] internal bool codeExpanded = true;
        [SerializeField] internal Vector2 codeSize = new(380, 220);
        [SerializeField] bool hasWhimState;

        [Serializable]
        public sealed class Dependencies : IHasDependencies
        {
            [SerializeField] string appliedInclude;
            [SerializeField] string m_FunctionSource;
            public void GetSourceAssetDependencies(AssetCollection assets)
            {
                foreach (string id in new[] { appliedInclude, m_FunctionSource })
                    if (GUID.TryParse(id, out var guid)) assets.AddAssetDependency(guid,
                        AssetCollection.Flags.SourceDependency | AssetCollection.Flags.IncludeInExportPackage);
            }
        }

        public WhimShaderCustomFunctionNode()
        {
            sourceType = HlslSourceType.String;
            functionName = "WhimFunction_" + objectId;
            functionBody = draft.code;
            name = appliedTitle;
            AddSlot(new Vector1MaterialSlot(0, "In", "In", SlotType.Input, .5f));
            AddSlot(new Vector1MaterialSlot(1, "Out", "Out", SlotType.Output, 0));
            synonyms = new[] { "code", "HLSL", "Custom Function", "CodeArea" };
        }

        public override void OnAfterDeserialize()
        {
            base.OnAfterDeserialize();
            name = string.IsNullOrWhiteSpace(appliedTitle) ? "Custom Function" : appliedTitle;
        }

        public override void OnBeforeSerialize() { hasWhimState = true; base.OnBeforeSerialize(); }

        public override void OnAfterMultiDeserialize(string json)
        {
            base.OnAfterMultiDeserialize(json);
            if (hasWhimState) return;
            hasWhimState = true;
            bool empty = !this.GetSlots<MaterialSlot>().Any() && (functionName == defaultFunctionName || string.IsNullOrEmpty(functionName));
            if (empty) { ApplyUnchecked(new WhimFunctionDraft()); return; }
            appliedTitle = functionName == defaultFunctionName ? "Custom Function" : functionName;
            name = appliedTitle;
            draft = new WhimFunctionDraft { title = appliedTitle, mode = sourceType == HlslSourceType.File ? WhimFunctionSource.File : WhimFunctionSource.Body,
                code = functionBody, fileGuid = functionSource, usePragmas = functionSourceUsePragmas,
                ports = this.GetSlots<MaterialSlot>().Select(s => new WhimFunctionPort { id = s.id, name = s.shaderOutputName,
                    direction = s.slotType, type = (s.bareResource ? "Bare " : "") + WhimShaderFunctions.PortType(s.concreteValueType.ToShaderString()).Replace("Bare ", "") }).ToList() };
            if (draft.mode == WhimFunctionSource.File)
            {
                try
                {
                    var function = WhimShaderFunctionDiscovery.Parse(WhimShaderFunctions.Source(draft))
                        .FirstOrDefault(d => d.Name == functionName + "_float" || d.Name == functionName + "_half");
                    if (function != null) draft.entry = WhimShaderFunctions.Key(function);
                }
                catch (Exception) { /* Keep unresolved legacy files editable without losing the original reference. */ }
            }
        }

        public new void GenerateNodeFunction(FunctionRegistry registry, GenerationMode generationMode)
        {
            if (!string.IsNullOrEmpty(appliedInclude))
                registry.RequiresIncludePath(AssetDatabase.GUIDToAssetPath(appliedInclude), shouldIncludeWithPragmas: includePragmas);
            if (!string.IsNullOrEmpty(appliedSource))
            {
                string key = "WhimSource_" + Hash128.Compute(appliedSource);
                registry.ProvideFunction(key, b => { b.AppendLine("void " + key + "() {}"); b.AppendLines(appliedSource); });
            }
            base.GenerateNodeFunction(registry, generationMode);
        }

        public override void ValidateNode()
        {
            base.ValidateNode();
            if (!string.IsNullOrEmpty(appliedInclude) && string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(appliedInclude)))
                owner?.AddValidationError(objectId, "Custom Function HLSL file is missing.");
        }

        internal void ApplyUnchecked(WhimFunctionDraft value)
        {
            WhimShaderFunctions.Validate(value);
            appliedTitle = string.IsNullOrWhiteSpace(value.title) ? "Custom Function" : value.title;
            sourceType = HlslSourceType.String;
            functionName = "WhimFunction_" + objectId;
            functionSource = value.mode == WhimFunctionSource.File ? value.fileGuid : null;
            appliedSource = value.mode == WhimFunctionSource.Source ? value.code : null;
            appliedInclude = value.mode == WhimFunctionSource.File ? value.fileGuid : null;
            includePragmas = value.usePragmas;
            functionBody = value.mode == WhimFunctionSource.Body ? value.code : WhimShaderFunctions.Invocation(value);
            name = appliedTitle;
            var old = this.GetSlots<MaterialSlot>().ToArray();
            foreach (var port in value.ports)
            {
                var slot = WhimShaderFunctions.CreateSlot(port);
                var previous = old.FirstOrDefault(s => s.id == port.id && s.slotType == port.direction);
                if (previous != null) slot.CopyValuesFrom(previous);
                else if (old.Any(s => s.id == port.id)) RemoveSlot(port.id);
                AddSlot(slot, attemptToModifyExistingInstance: false, copyExistingValue: false);
            }
            RemoveSlotsNameNotMatching(value.ports.Select(p => p.id), true);
            SetSlotOrder(value.ports.OrderBy(p => p.direction).Select(p => p.id).ToList());
            draft = value.Clone();
            Concretize();
            Dirty(ModificationScope.Topological);
        }
    }
}
