# Unity source notice

The adapted shader sections in this directory derive from the installed
`com.unity.render-pipelines.universal` **17.7.0**, cache revision
`0f6472166576`:

- `Editor/ShaderGraph/Includes/Varyings.hlsl` → `WhimVaryings.hlsl`.
- `Editor/ShaderGraph/Includes/PBRForwardPass.hlsl` → lighting-context initialization
  and forward-pass framing.
- `Editor/ShaderGraph/Includes/UnlitPass.hlsl` → final Color/Alpha pass framing.
- `Editor/ShaderGraph/Includes/LightingMetaPass.hlsl` → independent baking outputs.
- `ShaderLibrary/Lighting.hlsl` → SceneLighting additional-light traversal.

Modifications: initialize lighting context before surface graph evaluation; make
vertex GI data available to auxiliary passes; evaluate PBR in graph nodes; write
final radiance without a second lighting evaluation; expose scene-light data;
use separate baked albedo/emission. The C# backend composes installed native Lit
pass descriptors and does not copy or reflect their private implementation.

com.unity.render-pipelines.universal copyright © 2020 Unity Technologies ApS

Licensed under the Unity Companion License for Unity-dependent projects--see
[Unity Companion License](http://www.unity3d.com/legal/licenses/Unity_Companion_License).

Unless expressly provided otherwise, the Software under this license is made
available strictly on an “AS IS” BASIS WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED. Please review the license for details on these and other terms and conditions.
