# KitsuneSharedMagazines

When you read a crafting skill magazine, a perk book or a schematic, everyone in
your party learns it too, wherever they are on the map.

Built for 7DTD V3.3. Ships a DLL: install on the **server and every client**,
with **EAC off** on the server and on each client.

## What gets shared

- **Magazines:** only crafting skills, from items tagged `csm` (all 24 vanilla
  skill magazines) or, from 0.3.1, any item whose `Unlocks` names a crafting
  skill, so modded magazines that skip the tag are shared too. Skill levels
  gained by doing (crafting or harvesting under a learn-by-doing overhaul) are
  not shared. The admin max-all magazine is never shared.
  Members get what the magazine is worth (the `PointsPerMagazine` sandbox
  option), even when the reader's own skill is already maxed. Each member is
  capped at their own max level.
- **Perk books** (the 7-volume series, e.g. Lucky Looter 1/7): a member who
  hasn't read that volume gets it unlocked, even if the reader already knew
  it. Members who already have it get nothing. The series bonus is never copied from the reader: a member gets it
  only when the shared volume completes *their* set, same as vanilla.
- **Schematics** (from 0.3.0): any item whose `Unlocks` names a learnable
  recipe, which covers all 69 vanilla schematics and recipes (mods, armor and
  vehicle mods, Sham Chowder, Grace's corn and so on). A member who doesn't
  know the recipe learns it, even if the reader already knew it. Members who
  already know it get nothing. Members get the recipe only, not the reader's
  XP for reading it.
- **Recipes and mods, all of them:** every recipe in the game is learned one
  of two ways, and both are shared. Recipes that come with crafting skill
  levels (most weapons, tools, armor, food, ammo, workstations and anything
  other mods hang off those skills) arrive with the shared magazine levels.
  Recipes from a schematic (every weapon, armor, vehicle and drone mod, plus
  the recipe schematics) arrive with the shared schematic.
- **Other mods' items:** books and schematics are matched by what they unlock,
  not by name, so a mod that adds its own perk books, schematics or magazines
  in the vanilla style is shared without any changes here.
- Party members must be online. Distance doesn't matter.
- Each member sees the normal crafting-skill notification and unlock messages,
  plus a tooltip: "NonToxThicc shared a Harvesting Tools magazine with you."
  Books show "... shared <book> with you.", with "That completes the series!"
  and the final-book sound when it does. Schematics show "... shared
  <schematic> with you."

## Series bonus self-heal

Vanilla only checks for a series bonus while a volume is being read for the
first time. Reading a volume you already know skips that check. So a player can
end up with all 7 volumes and no bonus, and no later read will fix it.

From 0.2.3 the mod checks again after every book read (even one you already
knew) and each time you load or join a game. If you have every volume in a
series but not its bonus, you get the bonus, the final-book sound and a
"Sniper series complete!" tooltip. An existing stuck save fixes itself the next
time that player joins. This runs on each player's own client, so the client
needs 0.2.3 (0.2.2 shipped this check with a bug that stopped it from ever
granting the bonus).

## How it works

The mod adds no new network packages. All three hops reuse the vanilla
console-command packages and intercept them with Harmony:

1. The reader's client notices a magazine was read for a crafting skill and sends
   `ksm_read <skill> <levels>` to the server. Reading a perk book sends
   `ksm_book <perk> <book item>`, and reading a schematic sends
   `ksm_schem <schematic item>`.
2. The server catches that before it reaches the console, checks it (crafting
   skill, 1-10 levels, at most one read per 0.5 s per player), and sends
   `ksm_apply <skill> <levels> <reader>` (or `ksm_bookapply <perk> <book item>
   <reader>`, after checking that item really is the book for that perk, or
   `ksm_schemapply <schematic item> <reader>`, after checking that item really
   unlocks a learnable recipe) to every other party member.
3. Each member's client catches that and raises the skill the same way the
   magazine would. Progression then syncs back to the server as usual. For a
   schematic it sets the recipe's cvar to 1, exactly as the schematic does,
   and that syncs with the member's player data.

None of these lines is a registered console command, so players can't type them in F1.
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
server. The party flow still needs two players to try it. Schematic sharing
(0.3.0) has only been compiled against the 3.3 client so far.

## License

[CC BY-NC-SA 4.0](LICENSE), with an extra permission for servers that make money: see [NOTICE.md](NOTICE.md). In short: play it, change it and share it, with credit, not for sale, and with changed versions under the same license.
