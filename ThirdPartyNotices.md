# Third-party notices

WhimShader uses Unity Shader Graph and Universal Render Pipeline 17.7.0 as Editor dependencies. The graph model, built-in node implementations, shader generation, and stage validation execute from those packages. Unity-dependent source adaptations in `Editor/URP`, `Shaders`, `Editor/WhimShaderImporter.cs` and `Editor/WhimShaderWindow.SubGraph.cs` retain the Unity Companion License and their source attribution.

- Unity Graphics: https://github.com/Unity-Technologies/Graphics
- Unity Companion License: https://unity.com/legal/licenses/unity-companion-license

The docking workspace, node/preview controls, height renderer, visual styles, and catalog thumbnail atlas originate in WhimTex, copyright (c) 2026 Mikhail, under the MIT License. Its notice is retained in `Editor/UI/Workspace/LICENSE.WhimTex.md`.

- WhimTex: https://github.com/DCFApixels/WhimTex

The root MIT license covers WhimShader's original code and MIT-derived workspace code; it does not replace the licenses of Unity package dependencies or source adaptations.

The CodeArea controls, HLSL declaration discovery and automatic node arrangement algorithm are adapted from WhimTex under MIT. The editor bundles Microsoft's Cascadia Mono font under the SIL Open Font License 1.1; font license and notices are retained in `Editor/UI/Code/Fonts/CascadiaMono`.
