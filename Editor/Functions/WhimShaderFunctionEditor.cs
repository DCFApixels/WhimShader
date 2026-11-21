using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Node = UnityEditor.Experimental.GraphView.Node;

namespace DCFApixels.WhimShader
{
    internal sealed class WhimShaderFunctionEditor : VisualElement, IDisposable
    {
        readonly WhimShaderCustomFunctionNode node;
        readonly Node view;
        readonly Action changed;
        readonly DropdownField source;
        readonly TextField title;
        readonly ObjectField file;
        readonly DropdownField entry;
        readonly Toggle pragmas;
        readonly Foldout ports, codeFoldout;
        readonly VisualElement portRows = new();
        readonly Label signature = new();
        readonly HelpBox error = new("", HelpBoxMessageType.Error);
        readonly ShaderCodeField code;
        readonly Button convert;
        string shownPorts, fileCode = "";
        WhimFunctionDraft shownDraft;
        bool syncing;

        internal WhimShaderFunctionEditor(WhimShaderCustomFunctionNode node, Node view, Action changed)
        {
            this.node = node; this.view = view; this.changed = changed;
            name = "custom-function-editor";
            view.AddToClassList("whimshader-shader-graph-function");
            source = new DropdownField("Source", new List<string> { "Body", "File", "Source" }, (int)node.draft.mode);
            source.name = "function-source";
            source.RegisterValueChangedCallback(e => Edit("Change Function Source", d => d.mode = (WhimFunctionSource)source.index, true));
            Add(source);
            title = new TextField("Name") { name = "function-title", isDelayed = true };
            title.RegisterValueChangedCallback(e => Edit("Rename Function", d => d.title = e.newValue)); Add(title);
            file = new ObjectField("File") { name = "function-file", objectType = typeof(ShaderInclude), allowSceneObjects = false };
            file.RegisterValueChangedCallback(e => SetFile(AssetDatabase.GetAssetPath(e.newValue))); Add(file);
            entry = new DropdownField("Function", new List<string>(), -1) { name = "function-entry" };
            entry.RegisterValueChangedCallback(e => Edit("Choose HLSL Function", d => { d.entry = e.newValue; WhimShaderFunctions.DiscoverPorts(d); }, true)); Add(entry);
            pragmas = new Toggle("Include Pragmas") { name = "function-pragmas" };
            pragmas.RegisterValueChangedCallback(e => Edit("Change HLSL Pragmas", d => d.usePragmas = e.newValue)); Add(pragmas);
            var actions = new VisualElement(); actions.AddToClassList("whimshader-shader-graph-function-actions");
            actions.Add(new Button(ChooseFile) { text = "File…" });
            actions.Add(new Button(Compile) { text = "Compile", name = "function-compile" });
            actions.Add(new Button(() => { RefreshSource(); if (node.draft.mode == WhimFunctionSource.File) Compile(); }) { text = "Refresh", name = "function-refresh" });
            convert = new Button(ConvertToCode) { text = "Convert to Code", name = "function-convert" }; actions.Add(convert);
            Add(actions);
            ports = new Foldout { text = "Ports", value = false, name = "function-ports" }; ports.Add(portRows);
            ports.Add(new Button(() => Edit("Add Function Port", d =>
            {
                if (d.ports.Count >= 64) return;
                string portName = "Value" + d.ports.Count; while (d.ports.Any(p => p.name == portName)) portName += "_";
                int nextId = d.ports.Select(p => p.id).Concat(node.GetSlots<MaterialSlot>().Select(p => p.id))
                    .Concat(d.portIdentities.Select(p => p.id)).DefaultIfEmpty(-1).Max() + 1;
                d.ports.Add(new WhimFunctionPort { id = nextId, name = portName });
            }, true)) { text = "Add Port", name = "function-add-port" }); Add(ports);
            codeFoldout = new Foldout { text = "Code", name = "function-code", value = node.codeExpanded };
            codeFoldout.AddToClassList("whimshader-shader-graph-function-code-foldout");
            codeFoldout.RegisterValueChangedCallback(e =>
            {
                if (syncing || e.target != codeFoldout) return;
                Record("Toggle Function Code"); node.codeExpanded = e.newValue; Dirty(); RefreshLayout();
            });
            signature.AddToClassList("whimshader-shader-graph-function-signature"); codeFoldout.Add(signature);
            code = new ShaderCodeField(null, node, () => node.draft.mode == WhimFunctionSource.File ? fileCode : node.draft.code ?? "",
                value => { node.draft.code = value; Dirty(); ShowError(null); }, () => node.draft.mode != WhimFunctionSource.File,
                recordUndo: Record);
            code.AddToClassList("whimshader-shader-graph-function-code");
            var area = new VisualElement(); area.Add(code);
            var resize = new Label("◢"); resize.AddToClassList("whimshader-shader-graph-function-resize"); resize.AddManipulator(new Resizer(this)); area.Add(resize);
            codeFoldout.Add(area); Add(codeFoldout); Add(error); error.style.display = DisplayStyle.None;
            RefreshSource();
            RegisterCallback<KeyDownEvent>(e => { if ((e.ctrlKey || e.commandKey) && e.keyCode == KeyCode.Return) { Compile(); e.StopPropagation(); } });
        }

        void Record(string label) => node.owner?.owner?.RegisterCompleteObjectUndo(label);
        void Dirty() { if (node.owner?.owner == null) return; node.owner.owner.isDirty = true; node.Dirty(ModificationScope.Layout); changed?.Invoke(); }
        void Edit(string label, Action<WhimFunctionDraft> edit, bool rebuild = false)
        {
            if (syncing || node.owner?.owner == null) return;
            try
            {
                var candidate = node.draft.Clone(); edit(candidate);
                Record(label); node.draft = candidate; Dirty(); ShowError(null);
                if (rebuild) RefreshSource(); else Refresh();
            }
            catch (Exception e) { ShowError(e.Message); }
        }
        void ShowError(string message) { error.text = message ?? ""; error.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex; }
        internal void Compile()
        {
            try { WhimShaderFunctions.CompileAndApply(node); changed?.Invoke(); ShowError(null); RefreshSource(); }
            catch (Exception e) { ShowError(e.Message); }
        }
        void ChooseFile()
        {
            string path = EditorUtility.OpenFilePanel("HLSL Function", "Assets", "hlsl,cginc,cg");
            if (!string.IsNullOrEmpty(path)) SetFile(FileUtil.GetProjectRelativePath(path));
        }
        void SetFile(string path)
        {
            if (syncing) return;
            if (!string.IsNullOrEmpty(path) && !path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal))
            { ShowError("Choose an HLSL file inside Assets or Packages."); return; }
            Edit("Choose HLSL File", d => { d.mode = WhimFunctionSource.File; d.fileGuid = AssetDatabase.AssetPathToGUID(path); d.entry = null; }, true);
        }
        void ConvertToCode() => Edit("Embed HLSL Source", d =>
        {
            d.code = WhimShaderFunctions.EmbedFile(d); d.mode = WhimFunctionSource.Source; d.fileGuid = null;
        }, true);

        void RefreshSource()
        {
            try
            {
                if (node.draft.mode != WhimFunctionSource.Body)
                {
                    string text = WhimShaderFunctions.Source(node.draft); fileCode = text;
                    var choices = WhimShaderFunctionDiscovery.Parse(text).Select(WhimShaderFunctions.Key).Distinct().ToList();
                    entry.choices = choices;
                    if (choices.Count == 1 && string.IsNullOrEmpty(node.draft.entry))
                    {
                        Record("Select HLSL Entry"); node.draft.entry = choices[0]; WhimShaderFunctions.DiscoverPorts(node.draft); Dirty();
                    }
                }
            }
            catch (Exception e) { ShowError(e.Message); }
            shownPorts = null; shownDraft = null; Refresh();
        }

        internal void Refresh()
        {
            if (syncing) return;
            syncing = true;
            try
            {
                var d = node.draft;
                source.SetValueWithoutNotify(d.mode.ToString()); title.SetValueWithoutNotify(d.title);
                file.style.display = pragmas.style.display = convert.style.display = d.mode == WhimFunctionSource.File ? DisplayStyle.Flex : DisplayStyle.None;
                entry.style.display = d.mode == WhimFunctionSource.Body ? DisplayStyle.None : DisplayStyle.Flex;
                pragmas.SetValueWithoutNotify(d.usePragmas); entry.SetValueWithoutNotify(d.entry ?? "");
                ports.SetEnabled(d.mode == WhimFunctionSource.Body);
                if (!ReferenceEquals(shownDraft, d))
                {
                    shownDraft = d;
                    file.SetValueWithoutNotify(string.IsNullOrEmpty(d.fileGuid) ? null : AssetDatabase.LoadAssetAtPath<ShaderInclude>(AssetDatabase.GUIDToAssetPath(d.fileGuid)));
                    string key = string.Join("|", d.ports.Select(p => $"{p.id}:{p.name}:{p.type}:{p.direction}"));
                    if (shownPorts != key) { shownPorts = key; RebuildPorts(); }
                    signature.text = "void Function(" + string.Join(", ", d.ports.Select(p => (p.direction == SlotType.Output ? "out " : "") + p.type + " " + p.name)) + ")";
                }
                codeFoldout.SetValueWithoutNotify(node.codeExpanded); code.SyncFromModel(); RefreshLayout();
                code.isReadOnly = d.mode == WhimFunctionSource.File;
            }
            finally { syncing = false; }
        }

        void RebuildPorts()
        {
            portRows.Clear();
            foreach (var port in node.draft.ports)
            {
                int id = port.id;
                var row = new VisualElement(); row.AddToClassList("whimshader-shader-graph-function-port");
                var header = new VisualElement(); header.AddToClassList("whimshader-shader-graph-function-actions");
                var field = new TextField { value = port.name, isDelayed = true }; field.style.flexGrow = 1; field.style.minWidth = 0;
                field.RegisterValueChangedCallback(e => Edit("Rename Function Port", d => d.ports.First(p => p.id == id).name = e.newValue)); header.Add(field);
                header.Add(new Button(() => Edit("Remove Function Port", d => d.ports.RemoveAll(p => p.id == id), true)) { text = "−" }); row.Add(header);
                var type = new DropdownField(WhimShaderFunctions.PortTypes.ToList(), Math.Max(0, Array.IndexOf(WhimShaderFunctions.PortTypes, port.type)));
                type.RegisterValueChangedCallback(e => Edit("Change Function Port Type", d => d.ports.First(p => p.id == id).type = e.newValue)); row.Add(type);
                var direction = new EnumField(port.direction);
                direction.RegisterValueChangedCallback(e => Edit("Change Function Port Direction", d => d.ports.First(p => p.id == id).direction = (SlotType)e.newValue)); row.Add(direction);
                portRows.Add(row);
            }
        }
        void RefreshLayout()
        {
            view.style.width = node.codeExpanded && node.codeSize.x > 0 ? new StyleLength(node.codeSize.x) : StyleKeyword.Null;
            code.style.height = node.codeExpanded && node.codeSize.y > 0 ? new StyleLength(node.codeSize.y) : StyleKeyword.Null;
        }
        public void Dispose() { RemoveFromHierarchy(); view.RemoveFromClassList("whimshader-shader-graph-function"); view.style.width = StyleKeyword.Null; }

        sealed class Resizer : PointerManipulator
        {
            readonly WhimShaderFunctionEditor owner;
            Vector2 origin, size; int pointer = -1;
            internal Resizer(WhimShaderFunctionEditor owner) { this.owner = owner; }
            protected override void RegisterCallbacksOnTarget()
            { target.RegisterCallback<PointerDownEvent>(Down); target.RegisterCallback<PointerMoveEvent>(Move); target.RegisterCallback<PointerUpEvent>(Up); target.RegisterCallback<PointerCaptureOutEvent>(Lost); }
            protected override void UnregisterCallbacksFromTarget()
            { target.UnregisterCallback<PointerDownEvent>(Down); target.UnregisterCallback<PointerMoveEvent>(Move); target.UnregisterCallback<PointerUpEvent>(Up); target.UnregisterCallback<PointerCaptureOutEvent>(Lost); }
            void Down(PointerDownEvent e)
            {
                if (e.button != 0) return;
                origin = e.position; size = new Vector2(owner.view.resolvedStyle.width, owner.code.resolvedStyle.height);
                pointer = e.pointerId; target.CapturePointer(pointer); owner.Record("Resize Function Code"); e.StopPropagation();
            }
            void Move(PointerMoveEvent e)
            {
                if (pointer != e.pointerId || !target.HasPointerCapture(pointer)) return;
                var next = size + ((Vector2)e.position - origin) / Mathf.Max(.001f, owner.view.worldTransform.lossyScale.x);
                owner.node.codeSize = new Vector2(Mathf.Max(200, next.x), Mathf.Max(80, next.y)); owner.Dirty(); owner.RefreshLayout(); e.StopPropagation();
            }
            void Up(PointerUpEvent e) { if (e.pointerId != pointer) return; target.ReleasePointer(pointer); e.StopPropagation(); }
            void Lost(PointerCaptureOutEvent e) { pointer = -1; Undo.FlushUndoRecordObjects(); }
        }
    }
}
