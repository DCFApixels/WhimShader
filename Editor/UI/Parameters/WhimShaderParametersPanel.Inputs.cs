using System.Collections.Generic;
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEditor.ShaderGraph.Internal;
using UnityEditor.ShaderGraph.Serialization;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    internal sealed partial class WhimShaderParametersPanel
    {
        IEnumerable<ShaderInput> AllInputs() => graph.properties.Cast<ShaderInput>().Concat(graph.keywords).Concat(graph.dropdowns);
        ShaderInput GraphInput(string id) => string.IsNullOrEmpty(id) ? null : AllInputs().FirstOrDefault(input => input.objectId == id);
        static string NativeInputId(ShaderInput input) => "input:" + input.objectId;
        static string InputTypeName(ShaderInput input) => input switch
        {
            AbstractShaderProperty property => property.GetPropertyTypeString(),
            ShaderKeyword keyword => keyword.keywordType + " Keyword",
            ShaderDropdown => "Dropdown",
            _ => "Shader Input"
        };
        bool CanDragInput(ShaderInput input) => CanEdit && input != null && input is not MultiJsonInternal.UnknownShaderPropertyType &&
            graph.IsInputAllowedInGraph(input);

        void AddNativeInputMenu(DropdownMenu menu)
        {
            BlackboardViewModel Catalog() => graphView.GetFirstAncestorOfType<GraphEditorView>()?.blackboardController?.ViewModel;
            var catalog = Catalog();
            if (catalog == null) return;
            AddMap("Keyword/", model => model.defaultKeywordNameToAddActionMap, catalog.defaultKeywordNameToAddActionMap.Keys);
            AddMap("Built-In Keyword/", model => model.builtInKeywordNameToAddActionMap,
                catalog.builtInKeywordNameToAddActionMap.Keys.Concat(catalog.disabledKeywordNameList).Distinct());
            menu.AppendAction("Dropdown (Sub Graph)", _ =>
            {
                if (Catalog()?.defaultDropdownNameToAdd?.Item2 is AddShaderInputAction action) AddProperty(action);
            }, _ => CanEdit && Catalog()?.defaultDropdownNameToAdd?.Item2 is AddShaderInputAction
                ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);

            void AddMap(string prefix, Func<BlackboardViewModel, Dictionary<string, IGraphDataAction>> select, IEnumerable<string> names)
            {
                foreach (string name in names.ToArray())
                {
                    bool Available() => CanEdit && Catalog() is BlackboardViewModel current && select(current).ContainsKey(name);
                    menu.AppendAction(prefix + name, _ =>
                    {
                        var current = Catalog();
                        if (current != null && select(current).TryGetValue(name, out var value) && value is AddShaderInputAction action) AddProperty(action);
                    }, _ => Available() ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                }
            }
        }

        void NotifyInput(ShaderInput input, ModificationScope scope)
        {
            if (input is AbstractShaderProperty property) { NotifyProperty(property, scope); return; }
            foreach (var observer in input.InputObservers.ToArray()) observer.OnShaderInputUpdated(scope);
            if (input is ShaderKeyword) graph.OnKeywordChanged();
            else if (input is ShaderDropdown) graph.OnDropdownChanged();
            else graph.ValidateGraph();
            if (graph.owner != null) graph.owner.isDirty = true;
        }

        void BuildNativeInputDefinitions()
        {
            BuildSection("Keywords", graph.keywords.Cast<ShaderInput>());
            BuildSection("Dropdowns", graph.dropdowns.Cast<ShaderInput>());

            void BuildSection(string title, IEnumerable<ShaderInput> source)
            {
                var inputs = source.ToList();
                if (inputs.Count == 0) return;
                var heading = new Label(title); heading.AddToClassList("whimshader-parameter-root-drop"); definitions.Add(heading);
                foreach (var input in inputs) definitions.Add(NativeInputDefinition(input));
            }
        }

        VisualElement NativeInputDefinition(ShaderInput input)
        {
            string id = NativeInputId(input);
            var foldout = new Foldout
            {
                name = "parameter-item-" + id, text = input.displayName,
                value = !definitionExpanded.TryGetValue(id, out bool open) || open
            };
            foldout.AddToClassList("whimshader-parameter-block");
            foldout.EnableInClassList("whimshader-parameter-block--selected", selectedId == id);
            var header = foldout.Q<Toggle>(); header.AddToClassList("whimshader-parameter-block-header");
            header.RegisterCallback<PointerDownEvent>(_ => Select(id), TrickleDown.TrickleDown);
            foldout.RegisterValueChangedCallback(evt => { if (evt.target == foldout) definitionExpanded[id] = evt.newValue; });
            header.AddManipulator(new ItemDrag(this, id, foldout, input.objectId));
            var type = new Label(InputTypeName(input)) { pickingMode = PickingMode.Ignore }; type.AddToClassList("whimshader-parameter-type"); header.Add(type);
            if (!graph.IsInputAllowedInGraph(input))
            {
                foldout.Add(new HelpBox("This input is not supported in this graph. Dropdown nodes require a Sub Graph; its definition remains available below.", HelpBoxMessageType.Warning));
            }
            // Native drawer owns names, references, exposure/promotion, scope, declaration, stages,
            // entries/default selection, built-in restrictions and their Undo transactions.
            var settings = NativeSettings(input); foldout.Add(settings);
            header.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                evt.menu.AppendAction("Delete " + (input is ShaderKeyword ? "Keyword" : "Dropdown"), _ => DeleteNativeInput(input.objectId),
                    _ => CanEdit ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
                evt.StopPropagation();
            }));
            return foldout;
        }

        void DeleteNativeInput(string id)
        {
            var input = GraphInput(id);
            if (!CanEdit || input is not (ShaderKeyword or ShaderDropdown)) return;
            var usages = graph.GetNodes<AbstractMaterialNode>().Where(node =>
                node is KeywordNode keyword && ReferenceEquals(keyword.keyword, input) ||
                node is DropdownNode dropdown && ReferenceEquals(dropdown.dropdown, input)).ToList();
            if (usages.Count > 0 && !EditorUtility.DisplayDialog("Delete Used Shader Input",
                $"Delete '{input.displayName}' and its {usages.Count} node(s)? Their connections will also be removed. Keywords and dropdowns do not have native concrete-node conversion.",
                "Delete Input and Nodes", "Cancel")) return;
            graph.owner.RegisterCompleteObjectUndo("Delete Shader Input");
            foreach (var node in usages) graph.RemoveNode(node);
            graph.owner.graphDataStore.Dispatch(new DeleteShaderInputAction { shaderInputsToDelete = new List<ShaderInput> { input } });
            graph.owner.isDirty = true; changed(); QueueRebuild();
        }
    }
}
