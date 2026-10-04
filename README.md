# KitsuneSharedMagazines

When you read a crafting skill magazine, everyone in your party learns it too,
wherever they are on the map.

Built for 7DTD V3.3. Ships a DLL: install on the **server and every client**,
with **EAC off** on the server and on each client.

## What gets shared

- Only crafting skills, and only from items tagged `csm`, which covers all 24
  vanilla skill magazines. Perk books and the admin max-all magazine are never shared.
- The same number of levels the reader actually gained. That respects the
  `PointsPerMagazine` sandbox option and each skill's max level.
- Party members must be online. Distance doesn't matter.
- Each member sees the normal crafting-skill notification and unlock messages,
  plus a tooltip: "NonToxThicc shared a Harvesting Tools magazine with you."

## How it works

The mod adds no new network packages. All three hops reuse the vanilla
console-command packages and intercept them with Harmony:

1. The reader's client notices a magazine raised a crafting skill and sends
   `ksm_read <skill> <levels>` to the server.
2. The server catches that before it reaches the console, checks it (crafting
   skill, 1-10 levels, at most one read per 0.5 s per player), and sends
   `ksm_apply <skill> <levels> <reader>` to every other party member.
3. Each member's client catches that and raises the skill the same way the
   magazine would. Progression then syncs back to the server as usual.

Neither line is a registered console command, so players can't type them in F1.
A mismatch never drops anyone. A member without the mod just sees
"unknown command" in their console, and so does a reader on a server without it.

## Build

```
dotnet build -c Release
```

This builds against the 3.3 client at `G:\SteamLibrary\steamapps\common\7 Days To Die`
by default. Override that with `-p:GameDir=...`. The deployable mod folder is
`KitsuneSharedMagazines/` (ModInfo.xml + DLL); copy it into `Mods/`.

## Not yet tested in game

Both builds compile: against the 3.3 client and against the 3.3 dedicated
server. The party flow still needs two players to try it.
