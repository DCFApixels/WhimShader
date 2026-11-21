using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Graphing;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Serialization;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    internal sealed partial class WhimShaderParametersPanel
    {
        const string DragKey = "DCFApixels.WhimShader.ParameterItem";
        sealed class DragItem
        {
            internal GraphData graph;
            internal string id, propertyId;
        }
        DragItem CurrentDrag => DragAndDrop.GetGenericData(DragKey) is DragItem drag && drag.graph == graph ? drag : null;

        void AddDropTarget(VisualElement target, string parentId, string itemId, bool allowInside)
        {
            bool Destination(Vector2 mouse, out string parent, out string before)
            {
                parent = parentId; before = itemId;
                var drag = CurrentDrag;
                if (!CanEdit || drag == null) return false;
                var item = layout.Find(itemId);
                if (item != null)
                {
                    float fraction = (mouse.y - target.worldBound.yMin) / Mathf.Max(1, target.worldBound.height);
                    if (allowInside && fraction >= .25f && fraction <= .75f)
                    { parent = item.id; before = null; }
                    else if (fraction > .5f)
                    {
                        var siblings = layout.Children(item.parentId).ToList();
                        int index = siblings.IndexOf(item);
                        before = index + 1 < siblings.Count ? siblings[index + 1].id : null;
                    }
                }
                return layout.CanMove(drag.id, parent, before);
            }
            target.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (CurrentDrag == null) return;
                bool valid = Destination(evt.mousePosition, out var parent, out var before);
                DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Move : DragAndDropVisualMode.Rejected;
                target.EnableInClassList("whimshader-parameter-drop--inside", valid && before == null && parent == itemId);
                target.EnableInClassList("whimshader-parameter-drop--before", valid && before == itemId);
                target.EnableInClassList("whimshader-parameter-drop--after", valid && before != itemId && parent == parentId);
                evt.StopPropagation();
            });
            void ClearHint()
            {
                target.RemoveFromClassList("whimshader-parameter-drop--inside");
                target.RemoveFromClassList("whimshader-parameter-drop--before");
                target.RemoveFromClassList("whimshader-parameter-drop--after");
            }
            target.RegisterCallback<DragLeaveEvent>(_ => ClearHint());
            target.RegisterCallback<DragExitedEvent>(_ => ClearHint());
            target.RegisterCallback<DragPerformEvent>(evt =>
            {
                var drag = CurrentDrag; if (drag == null) return;
                if (Destination(evt.mousePosition, out var parent, out var before))
                {
                    DragAndDrop.AcceptDrag(); DragAndDrop.SetGenericData(DragKey, null);
                    MoveItem(drag.id, parent, before);
                }
                ClearHint(); evt.StopPropagation();
            });
        }

        void GraphDragUpdated(DragUpdatedEvent evt)
        {
            var drag = CurrentDrag; if (drag == null) return;
            var property = GraphInput(drag.propertyId);
            DragAndDrop.visualMode = CanDragInput(property)
                ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Rejected;
            evt.StopImmediatePropagation();
        }

        void GraphDragPerform(DragPerformEvent evt)
        {
            var drag = CurrentDrag; if (drag == null) return;
            evt.StopImmediatePropagation();
            var property = GraphInput(drag.propertyId);
            if (!CanDragInput(property)) return;
            var position = graphView.ChangeCoordinatesTo(graphView.contentViewContainer, evt.localMousePosition);
            graph.owner.graphDataStore.Dispatch(new DragGraphInputAction { graphInputBeingDraggedIn = property, nodePosition = position });
            graph.ValidateGraph();
            DragAndDrop.AcceptDrag(); DragAndDrop.SetGenericData(DragKey, null); changed();
        }

        sealed class ItemDrag : PointerManipulator
        {
            readonly WhimShaderParametersPanel owner;
            readonly string id;
            readonly string inputId;
            readonly Foldout foldout;
            int pointer = -1;
            Vector2 start;
            internal ItemDrag(WhimShaderParametersPanel owner, string id, Foldout foldout, string inputId = null)
            { this.owner = owner; this.id = id; this.foldout = foldout; this.inputId = inputId; }
            protected override void RegisterCallbacksOnTarget()
            {
                target.RegisterCallback<PointerDownEvent>(Down, TrickleDown.TrickleDown);
                target.RegisterCallback<PointerMoveEvent>(Move, TrickleDown.TrickleDown);
                target.RegisterCallback<PointerUpEvent>(Up, TrickleDown.TrickleDown);
                target.RegisterCallback<PointerCaptureOutEvent>(Lost);
            }
            protected override void UnregisterCallbacksFromTarget()
            {
                Release(); target.UnregisterCallback<PointerDownEvent>(Down, TrickleDown.TrickleDown); target.UnregisterCallback<PointerMoveEvent>(Move, TrickleDown.TrickleDown);
                target.UnregisterCallback<PointerUpEvent>(Up, TrickleDown.TrickleDown); target.UnregisterCallback<PointerCaptureOutEvent>(Lost);
            }
            void Down(PointerDownEvent evt)
            {
                if (evt.button != 0 || !owner.CanEdit) return;
                pointer = evt.pointerId; start = evt.position; target.CapturePointer(pointer);
                evt.StopImmediatePropagation();
            }
            void Move(PointerMoveEvent evt)
            {
                if (pointer != evt.pointerId || !target.HasPointerCapture(pointer) || (evt.pressedButtons & 1) == 0 || ((Vector2)evt.position - start).sqrMagnitude < 25) return;
                var item = inputId == null ? owner.layout.Find(id) : null;
                var input = inputId == null ? null : owner.GraphInput(inputId);
                if (item == null && input == null) { Release(); return; }
                Release();
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>(); DragAndDrop.paths = Array.Empty<string>();
                DragAndDrop.SetGenericData("DragSelection", null);
                DragAndDrop.SetGenericData(DragKey, new DragItem { graph = owner.graph, id = input == null ? id : null, propertyId = input?.objectId ?? item?.propertyId });
                DragAndDrop.StartDrag(input?.displayName ?? (item.kind == WhimShaderParameterItemKind.Parameter ? owner.Property(item.propertyId)?.displayName ?? "Parameter" : item.title));
                evt.StopImmediatePropagation();
            }
            void Up(PointerUpEvent evt)
            {
                if (evt.pointerId != pointer || evt.button != 0) return;
                Release();
                if (target.worldBound.Contains(evt.position)) { owner.Select(id); foldout.value = !foldout.value; }
                evt.StopImmediatePropagation();
            }
            void Lost(PointerCaptureOutEvent evt) { if (evt.pointerId == pointer) pointer = -1; }
            void Release() { int current = pointer; pointer = -1; if (current >= 0 && target.HasPointerCapture(current)) target.ReleasePointer(current); }
        }
    }
}
