# BTDebug

Mod source: [BTDebug](https://github.com/CWolfs/BTDebug).

Code: [`BTDebugCompat.cs`](../../tools/mod-bootstrap/BTDebugCompat.cs). Kind: Unity 6 difference; BTDebug works on the 2018 game.

**What broke.** `GizmoManager`'s constructor builds materials with `new Material(Shader.Find("..."))`. On UCU,
`Unlit/BT-Stars` and `Unlit/BT-LaserUI` live only in the game's asset bundles, so `Shader.Find` returns null and the
constructor throws inside BTDebug's prefix on `EncounterLayerParent.InitializeContract`. The contract never
initializes and the load never ends.

**What UCU does.** In that one constructor only, each `Shader.Find` call asks `Shader.Find` first, then UCU's bundle
shader resolver, then a plain built-in shader. A gizmo with no shader of its own is drawn flat. `Shader.Find` itself is
not patched.

**Upstream:** null-check the shader before `new Material`.
