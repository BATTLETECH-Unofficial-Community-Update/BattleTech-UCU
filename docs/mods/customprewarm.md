# CustomPrewarm

Mod source: [CustomPrewarm](https://github.com/CMiSSioN/CustomPrewarm).

Code: [`CustomPrewarmCompat.cs`](../../tools/mod-bootstrap/CustomPrewarmCompat.cs), plus the facade resolver in
[`ModBootstrap.cs`](../../tools/mod-bootstrap/ModBootstrap.cs). Kind: mod bug, fails on the 2018 game too.

**What broke.** `SimGameResultAction.additionalValues` is optional in def JSON, so it's null wherever a def leaves it
out. CustomPrewarm's cache copies it with `new List<string>(src.additionalValues)`, which throws on null; the first
cache build dies and the mod turns caching off. Separately, its bundled `System.Threading.Tasks.Extensions` needs a
netstandard 1.x facade that Unity 6 doesn't ship (see [modtek.md](modtek.md)).

**What UCU does.** A null list is lent an empty one for the copy, and the cache remembers it was null, so the game gets
back exactly what it had: null stays null, empty stays empty.

**Upstream:** treat a null `additionalValues` as null in both directions.
