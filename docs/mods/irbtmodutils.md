# IRBTModUtils

Mod source: [IRBTModUtils](https://github.com/BattletechModders/IRBTModUtils).

Code: the `CoreModule` copy in [`ModBootstrap.cs`](../../tools/mod-bootstrap/ModBootstrap.cs). Kind: Unity 6 difference.

**What broke.** Unity 2018 created block-compressed textures of any size. Unity 6 refuses one whose width or height
isn't a multiple of 4. IRBTModUtils' sprite loader creates a 2×2 DXT5 texture before `LoadImage`, so every PNG portrait
it loads fails, and a save with such a pilot in the timeline stops the ship screen.

**What UCU does.** In modded runs, such a request becomes RGBA32, the format `LoadImage` leaves a PNG in anyway. Other
sizes and formats are untouched. UCU's own sprite loader got the same fix in its game code.

**Upstream:** create the texture as `TextureFormat.RGBA32`, or at a size that's a multiple of 4.
