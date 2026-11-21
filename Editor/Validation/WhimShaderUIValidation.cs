using System;
using System.Linq;
using UnityEditor;
using UnityEditor.ShaderGraph;
using UnityEditor.ShaderGraph.Drawing;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCFApixels.WhimShader
{
    public static class WhimShaderUIValidation
    {
        public static string CatalogThumbnails(WhimShaderWindow window)
        {
            if (window?.PreviewService == null) throw new InvalidOperationException("No initialized WhimShader window.");
            return window.CatalogThumbnailReport();
        }

        public static object Snapshot(WhimShaderWindow window = null)
        {
            window ??= EditorWindow.focusedWindow as WhimShaderWindow;
            window ??= Resources.FindObjectsOfTypeAll<WhimShaderWindow>().FirstOrDefault(w => w.PreviewService != null);
            if (window?.PreviewService == null) throw new InvalidOperationException("No initialized WhimShader window.");
            var root = window.rootVisualElement;
            var view = window.PreviewService.Editor.graphView;
            var errors = WhimShaderPortReplacement.Validate(view);
            var previewables = view.Query<MaterialNodeView>().ToList().Where(n => WhimShaderNodePresentation.SupportsPreview(n.node)).ToList();
            var final = root.Q<WhimShaderFinalPreviewPanel>();
            if (final == null) throw new InvalidOperationException("Missing final preview panel.");
            var channels = final.Q("layer-preview-channels").Query<Button>().ToList();
            if (channels.Count != 4 || final.Q("layer-preview-image") == null || final.Q("layer-preview-height") == null)
                throw new InvalidOperationException("The WhimTex final preview controls are incomplete.");
            if (root.Query<DropdownField>("preview-mode").ToList().Count > 0)
                throw new InvalidOperationException("Legacy mode dropdown remains in the preview UI.");
            if (errors.Length > 0) throw new InvalidOperationException(string.Join("\n", errors));
            return new
            {
                window = window.titleContent.text,
                nativePorts = view.Query<ShaderPort>().ToList().Count,
                portErrors = errors,
                finalControls = channels.Select(b => new { b.name, bounds = b.worldBound.ToString(), clipped = !final.worldBound.Contains(b.worldBound.center) }).ToArray(),
                nodePreviews = previewables.Select(n => new { n.node.name, n.node.previewExpanded,
                    presentation = window.PreviewService.DescribePreview(n.node), bounds = n.worldBound.ToString() }).ToArray(),
                catalogRows = root.Query(className: "whimshader-shader-graph-palette-row").ToList().Count,
                legacyModeSelectors = root.Query<DropdownField>("preview-mode").ToList().Count,
                finalBounds = final.worldBound.ToString()
            };
        }
    }
}
