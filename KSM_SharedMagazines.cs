using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// KitsuneSharedMagazines — reading a crafting skill magazine teaches your
// whole party, wherever they are on the map.
//
// Flow (dedicated server, all three hops ride VANILLA packages so no custom
// NetPackage IDs exist to desync a client that lacks the mod):
//
//   1. Reader's client: postfix on MinEventActionAddProgressionLevel.Execute
//      sees a magazine (item tag "csm") raise one of the local player's
//      crafting skills, and sends "ksm_read <skill> <levels>" to the server
//      inside a NetPackageConsoleCmdServer.
//   2. Server: a prefix on NetPackageConsoleCmdServer.ProcessPackage swallows
//      that line before the console sees it (so it is never a real command
//      anyone can type), checks it, and sends "ksm_apply <skill> <levels>
//      <reader name>" to every other party member's client inside a
//      NetPackageConsoleCmdClient.
//   3. Member's client: a prefix on NetPackageConsoleCmdClient.ProcessPackage
//      swallows that line and raises the skill on the local player, exactly as
//      the vanilla magazine would (notification + unlock messages). The client
//      then syncs its progression to the server the normal way.
//
// A member WITHOUT the mod just gets "unknown command" in their F1 console.
// A server without the mod answers the reader the same way. Neither drops
// anyone. Listen-server hosts are handled in-process (see Share/Apply).
//
// Only crafting skills (ProgressionType.Crafting) are shared, and only from
// items tagged "csm" — the 24 vanilla skill magazines. The admin max-all
// magazine (tag "admin", level -1) is deliberately excluded.

public class KitsuneSharedMagazinesInit : IModApi
{
    private static bool inited;

    public void InitMod(Mod _modInstance)
    {
        if (inited) return;
        inited = true;
        Log.Out("[KitsuneSharedMagazines] Applying shared-reading Harmony patches");
        new Harmony(GetType().ToString()).PatchAll(Assembly.GetExecutingAssembly());
    }
}

// Step 1: the reader's client. Prefix records the local player's level,
// postfix shares whatever the magazine actually added (vanilla applies the
// PointsPerMagazine sandbox option and clamps to MaxLevel, so the real delta
// can differ from the XML's level="1").
[HarmonyPatch(typeof(MinEventActionAddProgressionLevel))]
[HarmonyPatch("Execute")]
public class KSM_MagazineReadPatch
{
    public static void Prefix(MinEventActionAddProgressionLevel __instance, MinEventParams _params, out int __state)
    {
        __state = -1;
        try
        {
            if (!KSM_Share.IsMagazine(_params)) return;
            EntityPlayerLocal player = LocalTarget(__instance);
            ProgressionValue value = KSM_Share.CraftingSkill(player, __instance.progressionName);
            if (value != null) __state = value.Level;
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneSharedMagazines] read check failed: " + ex);
        }
    }

    public static void Postfix(MinEventActionAddProgressionLevel __instance, int __state)
    {
        if (__state < 0) return;
        try
        {
            EntityPlayerLocal player = LocalTarget(__instance);
            ProgressionValue value = KSM_Share.CraftingSkill(player, __instance.progressionName);
            if (value == null) return;
            int gained = value.Level - __state;
            if (gained <= 0) return;
            KSM_Share.ReportRead(player, __instance.progressionName, gained);
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneSharedMagazines] sharing a read failed: " + ex);
        }
    }

    private static EntityPlayerLocal LocalTarget(MinEventActionAddProgressionLevel action)
    {
        List<EntityAlive> targets = action.targets;
        if (targets == null) return null;
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] is EntityPlayerLocal local && !local.isEntityRemote) return local;
        }
        return null;
    }
}

// Step 2: the server. Runs before ConnectionManager.ServerConsoleCommand, so a
// ksm_read line never reaches the console, its permission check or its log.
[HarmonyPatch(typeof(NetPackageConsoleCmdServer))]
[HarmonyPatch("ProcessPackage")]
public class KSM_ServerInboxPatch
{
    public static bool Prefix(NetPackageConsoleCmdServer __instance, World _world)
    {
        string cmd = __instance.cmd;
        if (cmd == null || !cmd.StartsWith(KSM_Share.ReadVerb + " ", StringComparison.Ordinal)) return true;
        try
        {
            if (_world != null) KSM_Share.OnReadReport(_world, __instance.Sender, cmd);
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneSharedMagazines] handling a read report failed: " + ex);
        }
        return false;
    }
}

// Step 3: a party member's client.
[HarmonyPatch(typeof(NetPackageConsoleCmdClient))]
[HarmonyPatch("ProcessPackage")]
public class KSM_ClientInboxPatch
{
    public static bool Prefix(NetPackageConsoleCmdClient __instance, World _world)
    {
        List<string> lines = __instance.lines;
        if (!__instance.bExecute || lines == null || lines.Count == 0 || lines[0] == null
            || !lines[0].StartsWith(KSM_Share.ApplyVerb + " ", StringComparison.Ordinal))
        {
            return true;
        }
        try
        {
            if (_world != null) KSM_Share.OnApply(_world, lines[0]);
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneSharedMagazines] applying a shared magazine failed: " + ex);
        }
        return false;
    }
}

public static class KSM_Share
{
    public const string ReadVerb = "ksm_read";
    public const string ApplyVerb = "ksm_apply";

    // A magazine read takes about a second (Eat action Delay 1.0), so a faster
    // stream of reports from one player is not real reading.
    private const float MinSecondsBetweenReports = 0.5f;
    private const int MaxLevelsPerRead = 10;

    private static readonly FastTags<TagGroup.Global> MagazineTag = FastTags<TagGroup.Global>.Parse("csm");
    private static readonly Dictionary<int, float> lastReportAt = new Dictionary<int, float>();

    public static bool IsMagazine(MinEventParams _params)
    {
        ItemClass itemClass = _params?.ItemValue?.ItemClass;
        return itemClass != null && itemClass.ItemTags.Test_AnySet(MagazineTag);
    }

    public static ProgressionValue CraftingSkill(EntityAlive player, string progressionName)
    {
        if (player == null || player.Progression == null || string.IsNullOrEmpty(progressionName)) return null;
        ProgressionValue value = player.Progression.GetProgressionValue(progressionName);
        if (value == null || value.ProgressionClass == null || !value.ProgressionClass.IsCrafting) return null;
        return value;
    }

    // Reader side: hand the read to whoever runs the party logic.
    public static void ReportRead(EntityPlayerLocal reader, string progressionName, int levels)
    {
        ConnectionManager connections = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (connections == null) return;
        if (connections.IsServer)
        {
            // Listen-server host (or single player, where there is no party).
            Share(reader, reader.EntityName, progressionName, levels);
            return;
        }
        connections.SendToServer(NetPackageManager.GetPackage<NetPackageConsoleCmdServer>()
            .Setup(ReadVerb + " " + progressionName + " " + levels));
    }

    // Server side: "ksm_read <skill> <levels>" from a client.
    public static void OnReadReport(World world, ClientInfo sender, string cmd)
    {
        if (sender == null) return;
        string[] parts = cmd.Split(' ');
        if (parts.Length != 3 || !int.TryParse(parts[2], out int levels)) return;
        if (levels < 1 || levels > MaxLevelsPerRead) return;

        EntityPlayer reader = world.GetEntity(sender.entityId) as EntityPlayer;
        if (CraftingSkill(reader, parts[1]) == null) return;

        float now = Time.time;
        if (lastReportAt.TryGetValue(sender.entityId, out float last) && now - last < MinSecondsBetweenReports)
        {
            Log.Warning("[KitsuneSharedMagazines] Ignored a read report from " + sender.playerName + " (too fast)");
            return;
        }
        lastReportAt[sender.entityId] = now;

        Share(reader, sender.playerName, parts[1], levels);
    }

    // Server (or listen host): send the levels to every other party member.
    public static void Share(EntityPlayer reader, string readerName, string progressionName, int levels)
    {
        Party party = reader?.Party;
        if (party == null || party.MemberList == null) return;

        ConnectionManager connections = SingletonMonoBehaviour<ConnectionManager>.Instance;
        int shared = 0;
        for (int i = 0; i < party.MemberList.Count; i++)
        {
            EntityPlayer member = party.MemberList[i];
            if (member == null || member.entityId == reader.entityId) continue;

            if (member is EntityPlayerLocal host)
            {
                Apply(host, progressionName, levels, readerName);
                shared++;
                continue;
            }

            ClientInfo client = connections?.Clients?.ForEntityId(member.entityId);
            if (client == null) continue;
            client.SendPackage(NetPackageManager.GetPackage<NetPackageConsoleCmdClient>()
                .Setup(ApplyVerb + " " + progressionName + " " + levels + " " + readerName, true));
            shared++;
        }

        if (shared > 0)
        {
            Log.Out("[KitsuneSharedMagazines] " + readerName + " read " + progressionName + " +" + levels
                + ", shared with " + shared + " party member(s)");
        }
    }

    // Member side: "ksm_apply <skill> <levels> <reader name...>".
    public static void OnApply(World world, string line)
    {
        string[] parts = line.Split(new[] { ' ' }, 4);
        if (parts.Length < 3 || !int.TryParse(parts[2], out int levels)) return;
        if (levels < 1 || levels > MaxLevelsPerRead) return;
        string readerName = parts.Length == 4 && parts[3].Length > 0 ? parts[3] : "A party member";
        Apply(world.GetPrimaryPlayer(), parts[1], levels, readerName);
    }

    // Mirrors what vanilla MinEventActionAddProgressionLevel does for the
    // local player, so a shared read looks and syncs like a real one.
    public static void Apply(EntityPlayerLocal player, string progressionName, int levels, string readerName)
    {
        ProgressionValue value = CraftingSkill(player, progressionName);
        if (value == null) return;

        int oldLevel = value.Level;
        int newLevel = Math.Min(oldLevel + levels, value.ProgressionClass.MaxLevel);
        if (newLevel <= oldLevel) return;
        value.Level = newLevel;

        player.Progression.bProgressionStatsChanged = true;
        player.bPlayerStatsChanged = true;

        if (XUiM_Recipes.CraftingProgression)
        {
            player.PlayerUI?.xui?.CollectedItemList?.AddCraftingSkillNotification(value);
            value.ProgressionClass.HandleCheckCrafting(player, oldLevel, newLevel);
        }

        string skillName = Localization.Get(value.ProgressionClass.NameKey, false);
        if (string.IsNullOrEmpty(skillName)) skillName = progressionName;
        GameManager.ShowTooltip(player, readerName + " shared a " + skillName + " magazine with you.", false, false, 0f);
    }
}
