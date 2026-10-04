# KitsuneSharedMagazines

When you read a crafting skill magazine or a perk book, everyone in your party
learns it too, wherever they are on the map.

Built for 7DTD V3.3. Ships a DLL: install on the **server and every client**,
with **EAC off** on the server and on each client.

## What gets shared

- **Magazines:** only crafting skills, and only from items tagged `csm`, which
  covers all 24 vanilla skill magazines. The admin max-all magazine is never shared.
  Members get what the magazine is worth (the `PointsPerMagazine` sandbox
  option), even when the reader's own skill is already maxed. Each member is
  capped at their own max level.
- **Perk books** (the 7-volume series, e.g. Lucky Looter 1/7): a member who
  hasn't read that volume gets it unlocked, even if the reader already knew
  it. Members who already have it get nothing. The series bonus is never copied from the reader: a member gets it
  only when the shared volume completes *their* set, same as vanilla.
- Party members must be online. Distance doesn't matter.
- Each member sees the normal crafting-skill notification and unlock messages,
  plus a tooltip: "NonToxThicc shared a Harvesting Tools magazine with you."
  Books show "... shared <book> with you.", with "That completes the series!"
  and the final-book sound when it does.

## How it works

The mod adds no new network packages. All three hops reuse the vanilla
console-command packages and intercept them with Harmony:

1. The reader's client notices a magazine was read for a crafting skill and sends
   `ksm_read <skill> <levels>` to the server. Reading a perk book sends
   `ksm_book <perk> <book item>`.
2. The server catches that before it reaches the console, checks it (crafting
   skill, 1-10 levels, at most one read per 0.5 s per player), and sends
   `ksm_apply <skill> <levels> <reader>` (or `ksm_bookapply <perk> <book item>
   <reader>`, after checking that item really is the book for that perk) to every
   other party member.
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
