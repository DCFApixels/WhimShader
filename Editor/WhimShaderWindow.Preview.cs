using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    public sealed partial class WhimShaderWindow
    {
        WhimShaderPreviewService previewService;
        internal WhimShaderPreviewService PreviewService => previewService;

        internal void ClosePreviewValidationFixture()
        {
            hasUnsavedChanges = false;
            Close();
        }

        // After GraphEditorView and workspace/layout exist. This owns NodePresentation too.
        internal void InitializePreviews()
        {
            DisposePreviews();
            if (editor == null || Graph == null || layout == null) return;
            var viewport = rootVisualElement.Q("whimshader-body") ?? editor.parent;
            if (viewport == null) return;
            layout.Validate();
            previewService = new WhimShaderPreviewService(Graph, editor, viewport, layout.preview,
                () => EditorPrefs.SetString("WhimShader.Workspace", JsonUtility.ToJson(layout)),
                () => { hasUnsavedChanges = true; Repaint(); });
            previewService.SetEdgeConnectorListener(new CatalogEdgeListener(this));
        }

        // After editor.HandleGraphChanges, BEFORE Graph.ClearChanges.
        internal void UpdatePreviews()
        {
            if (editor == null || Graph == null) { DisposePreviews(); return; }
            if (previewService == null || !ReferenceEquals(previewService.Graph, Graph) || !ReferenceEquals(previewService.Editor, editor))
                InitializePreviews();
            previewService?.Update();
        }

        // First in DisposeView, before native editor/workspace disposal and LUT release.
        internal void DisposePreviews()
        {
            previewService?.Dispose();
            previewService = null;
        }
    }
}
