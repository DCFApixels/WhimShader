using System;
using System.Linq;
using UnityEditor.ShaderGraph.Internal;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    internal sealed partial class WhimShaderParametersPanel
    {
        void BuildDefinitions()
        {
            var rootDrop = new VisualElement(); rootDrop.AddToClassList("whimshader-parameter-root-drop");
            rootDrop.RegisterCallback<PointerDownEvent>(_ => selectedId = null);
            AddDropTarget(rootDrop, null, null, allowInside: true); definitions.Add(rootDrop);
            foreach (var item in layout.Children(null).ToList()) definitions.Add(Definition(item));
            if (layout.items.Count == 0) definitions.Add(new Label("Add a parameter or group."));
            var end = new VisualElement(); end.AddToClassList("whimshader-parameter-drop-end");
            AddDropTarget(end, null, null, allowInside: true); definitions.Add(end);
        }

        VisualElement Definition(WhimShaderParameterItem item)
        {
            var property = item.kind == WhimShaderParameterItemKind.Parameter ? Property(item.propertyId) : null;
            var foldout = new Foldout
            {
                text = property?.displayName ?? item.title,
                value = definitionExpanded.TryGetValue(item.id, out bool expanded) ? expanded : item.kind != WhimShaderParameterItemKind.Parameter,
                name = "parameter-item-" + item.id
            };
            foldout.AddToClassList("whimshader-parameter-block");
            foldout.EnableInClassList("whimshader-parameter-block--selected", selectedId == item.id);
            var header = foldout.Q<Toggle>(); header.AddToClassList("whimshader-parameter-block-header");
            header.RegisterCallback<PointerDownEvent>(_ => Select(item.id), TrickleDown.TrickleDown);
            foldout.RegisterValueChangedCallback(evt => { if (evt.target == foldout) definitionExpanded[item.id] = evt.newValue; });
            header.AddManipulator(new ItemDrag(this, item.id, foldout));
            var type = new Label(property?.GetPropertyTypeString() ?? item.kind.ToString()) { pickingMode = PickingMode.Ignore };
            type.AddToClassList("whimshader-parameter-type"); header.Add(type);
            if (property != null)
            {
                header.RegisterCallback<MouseEnterEvent>(_ => HoverProperty(property.objectId, true));
                header.RegisterCallback<MouseLeaveEvent>(_ => HoverProperty(property.objectId, false));
                var exposed = new Toggle("Exposed") { value = property.isExposed };
                exposed.AddToClassList("whimshader-parameter-exposed");
                exposed.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
                exposed.SetEnabled(property.isExposable && !property.isAlwaysExposed && (!graph.isSubGraph || property.promoteToFinalShader));
                exposed.RegisterValueChangedCallback(evt =>
                {
                    EditValue(property, "Change Exposed Parameter", () =>
                    {
                        if (!property.overrideHLSLDeclaration || property.hlslDeclarationOverride == HLSLDeclaration.DoNotDeclare)
                        { property.hlslDeclarationOverride = property.GetDefaultHLSLDeclaration(); property.overrideHLSLDeclaration = true; }
                        property.generatePropertyBlock = evt.newValue;
                    });
                    QueueRebuild();
                });
                foldout.Add(exposed);
                PropertyDefinition(foldout, property);
            }
            else
            {
                var title = new TextField("Title") { value = item.title, isDelayed = true };
                title.AddToClassList(BaseField<string>.alignedFieldUssClassName);
                title.RegisterValueChangedCallback(evt => EditLayout("Rename Parameter Group", model => model.Find(item.id).title =
                    string.IsNullOrWhiteSpace(evt.newValue) ? item.kind.ToString() : evt.newValue.Trim()));
                foldout.Add(title);
                if (item.kind == WhimShaderParameterItemKind.Foldout)
                {
                    var defaults = new Toggle("Expanded by default") { value = item.expandedByDefault };
                    defaults.RegisterValueChangedCallback(evt =>
                    {
                        foldoutExpanded.Remove(item.id);
                        EditLayout("Change Foldout Default", model => model.Find(item.id).expandedByDefault = evt.newValue);
                    });
                    foldout.Add(defaults);
                }
                if (item.kind == WhimShaderParameterItemKind.Tabs)
                {
                    var tabs = layout.Children(item.id).ToList();
                    if (tabs.Count > 0)
                    {
                        var defaults = new PopupField<WhimShaderParameterItem>("Default tab", tabs,
                            Math.Max(0, tabs.FindIndex(x => x.id == item.defaultTabId)), x => x.title, x => x.title);
                        defaults.AddToClassList(BaseField<WhimShaderParameterItem>.alignedFieldUssClassName);
                        defaults.RegisterValueChangedCallback(evt =>
                        {
                            activeTabs.Remove(item.id);
                            EditLayout("Change Default Tab", model => model.Find(item.id).defaultTabId = evt.newValue.id);
                        });
                        foldout.Add(defaults);
                    }
                    foldout.Add(new Button(() => { selectedId = item.id; AddGroup(WhimShaderParameterItemKind.Tab); }) { text = "Add Tab" });
                }
                foreach (var child in layout.Children(item.id).ToList()) foldout.Add(Definition(child));
                var end = new VisualElement(); end.AddToClassList("whimshader-parameter-group-drop");
                AddDropTarget(end, item.id, null, allowInside: true); foldout.Add(end);
            }
            header.AddManipulator(new ContextualMenuManipulator(evt => { ItemMenu(evt.menu, item); evt.StopPropagation(); }));
            AddDropTarget(header, item.parentId, item.id, allowInside: property == null);
            return foldout;
        }

        void Select(string id)
        {
            if (selectedId == id) return;
            selectedId = id;
            foreach (var element in definitions.Query<Foldout>(className: "whimshader-parameter-block").ToList())
                element.EnableInClassList("whimshader-parameter-block--selected", element.name == "parameter-item-" + id);
        }

        void PropertyDefinition(VisualElement container, AbstractShaderProperty property)
        {
            var name = new TextField("Name") { value = property.displayName, isDelayed = true };
            name.AddToClassList(BaseField<string>.alignedFieldUssClassName); name.SetEnabled(property.isRenamable);
            name.RegisterValueChangedCallback(evt =>
            {
                EditValue(property, "Rename Shader Parameter", () => property.SetDisplayNameAndSanitizeForGraph(graph, evt.newValue));
                name.SetValueWithoutNotify(property.displayName); QueueRebuild();
            });
            container.Add(name);
            var reference = new TextField("Reference") { value = property.referenceNameForEditing, isDelayed = true };
            reference.AddToClassList(BaseField<string>.alignedFieldUssClassName); reference.SetEnabled(property.isReferenceRenamable);
            reference.RegisterValueChangedCallback(evt =>
            {
                EditValue(property, "Change Parameter Reference", () => property.SetReferenceNameAndSanitizeForGraph(graph, evt.newValue));
                reference.SetValueWithoutNotify(property.referenceNameForEditing);
            });
            container.Add(reference);
            container.Add(ValueField(property, "Default"));
            var settings = new Foldout { text = "Native settings", value = false };
            bool built = false;
            settings.RegisterValueChangedCallback(evt =>
            {
                if (evt.target != settings || !evt.newValue || built) return;
                built = true; settings.Add(NativeSettings(property));
            });
            container.Add(settings);
        }

        void ItemMenu(DropdownMenu menu, WhimShaderParameterItem item)
        {
            var siblings = layout.Children(item.parentId).ToList(); int index = siblings.IndexOf(item);
            menu.AppendAction("Move Up", _ => MoveItem(item.id, item.parentId, siblings[index - 1].id),
                index > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction("Move Down", _ => MoveItem(item.id, item.parentId, index + 2 < siblings.Count ? siblings[index + 2].id : null),
                index + 1 < siblings.Count ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.AppendAction("Move To/Root", _ => MoveItem(item.id, null, null),
                layout.CanMove(item.id, null) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            foreach (var destination in layout.items.Where(x => x.kind != WhimShaderParameterItemKind.Parameter && layout.CanMove(item.id, x.id)))
            {
                string id = destination.id;
                menu.AppendAction("Move To/" + GroupPath(destination), _ => MoveItem(item.id, id, null));
            }
            menu.AppendSeparator();
            if (item.kind == WhimShaderParameterItemKind.Parameter)
                menu.AppendAction("Delete Parameter", _ => DeleteProperty(item.propertyId));
            else menu.AppendAction("Ungroup (Keep Parameters)", _ => EditLayout("Ungroup Parameters", model => model.Ungroup(item.id)));
        }

        string GroupPath(WhimShaderParameterItem item)
        {
            string path = item.title;
            for (var parent = layout.Find(item.parentId); parent != null; parent = layout.Find(parent.parentId)) path = parent.title + "/" + path;
            return path;
        }

        void MoveItem(string id, string parent, string before)
        {
            if (!layout.CanMove(id, parent, before)) return;
            EditLayout("Move Parameter Item", model => model.Move(id, parent, before));
        }

        void BuildInspector()
        {
            foreach (var item in layout.Children(null).ToList()) InspectorItem(inspector, item);
            if (layout.items.Count == 0) inspector.Add(new Label("No parameters."));
        }

        void InspectorItem(VisualElement parent, WhimShaderParameterItem item)
        {
            if (item.kind == WhimShaderParameterItemKind.Parameter)
            {
                var property = Property(item.propertyId);
                if (property != null) parent.Add(ValueField(property, property.displayName));
                return;
            }
            if (item.kind == WhimShaderParameterItemKind.Tabs)
            {
                var view = new TabView(); view.AddToClassList("whimshader-parameter-tabs"); parent.Add(view);
                string active = activeTabs.TryGetValue(item.id, out var current) ? current : item.defaultTabId;
                foreach (var child in layout.Children(item.id))
                {
                    var tab = new Tab(child.title) { userData = child.id }; view.Add(tab);
                    foreach (var nested in layout.Children(child.id)) InspectorItem(tab, nested);
                    if (child.id == active) view.activeTab = tab;
                }
                view.activeTabChanged += (_, selected) => { if (selected != null) activeTabs[item.id] = (string)selected.userData; };
                return;
            }
            VisualElement content;
            if (item.kind == WhimShaderParameterItemKind.Foldout)
            {
                var foldout = new Foldout { text = item.title,
                    value = foldoutExpanded.TryGetValue(item.id, out bool value) ? value : item.expandedByDefault };
                foldout.RegisterValueChangedCallback(evt => { if (evt.target == foldout) foldoutExpanded[item.id] = evt.newValue; });
                content = foldout;
            }
            else
            {
                content = new VisualElement(); var title = new Label(item.title);
                title.AddToClassList("whimshader-parameter-heading"); content.Add(title);
            }
            content.AddToClassList("whimshader-parameter-value-group"); parent.Add(content);
            foreach (var child in layout.Children(item.id)) InspectorItem(content, child);
        }
    }
}
