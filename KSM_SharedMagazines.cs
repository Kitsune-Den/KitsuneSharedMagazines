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
//
// Perk books (the "1/7" series volumes) ride the same three hops with their
// own verbs: "ksm_book <perk> <book item>" up, "ksm_bookapply <perk> <book
// item> <reader name>" down. A book is any item whose "Unlocks" names a
// ProgressionType.Book perk, shared whenever it is read — even by someone who
// already knew it. The series "Complete" bonus is never shared as such: each
// member earns it themselves, the vanilla way, when the shared volume leaves
// them with every other book in the series unlocked.

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

// Step 1: the reader's client. Shares what the magazine is worth, not what
// the reader gained: a reader whose skill is already maxed gains nothing but
// still teaches the party. Vanilla turns level="1" into the PointsPerMagazine
// sandbox option for crafting skills; each member clamps to their own max.
[HarmonyPatch(typeof(MinEventActionAddProgressionLevel))]
[HarmonyPatch("Execute")]
public class KSM_MagazineReadPatch
{
    public static void Prefix(MinEventActionAddProgressionLevel __instance, MinEventParams _params, out int __state)
    {
        __state = 0;
        try
        {
            if (!KSM_Share.IsMagazine(_params)) return;
            EntityPlayerLocal player = KSM_Share.LocalTarget(__instance);
            if (KSM_Share.CraftingSkill(player, __instance.progressionName) == null) return;
            __state = __instance.level == 1
                ? global::SandboxOptions.SandboxOptionManager.GetInt(global::SandboxOptions.SandboxOptions.PointsPerMagazine)
                : __instance.level;
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneSharedMagazines] read check failed: " + ex);
        }
    }

    public static void Postfix(MinEventActionAddProgressionLevel __instance, int __state)
    {
        if (__state <= 0) return;
        try
        {
            EntityPlayerLocal player = KSM_Share.LocalTarget(__instance);
            if (player == null) return;
            KSM_Share.ReportRead(player, __instance.progressionName, __state);
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneSharedMagazines] sharing a read failed: " + ex);
        }
    }
}

// Step 1 for perk books. Hooked on the read itself (ItemActionEat), not on the
// book's unlock effect: that effect is skipped when the reader already knows
// the book (vanilla's ProgressionLevel == 0 requirement), but the book is still
// read and used up, so it should still teach the party. Members who already
// have it are skipped on their side. A held book finishes in consume(); "Read"
// from the inventory goes through ExecuteInstantAction.
[HarmonyPatch(typeof(ItemActionEat))]
[HarmonyPatch("consume")]
public class KSM_BookReadHeldPatch
{
    public static void Prefix(ItemActionData _actionData, out ItemClass __state)
    {
        __state = null;
        try
        {
            if (!(_actionData is ItemActionEat.MyInventoryData data) || !data.bEatingStarted) return;
            if (!(_actionData.invData?.holdingEntity is EntityPlayerLocal player) || player.isEntityRemote) return;
            __state = KSM_Share.BookClass(_actionData.invData.itemStack?.itemValue);
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneSharedMagazines] book check failed: " + ex);
        }
    }

    public static void Postfix(ItemActionData _actionData, ItemClass __state)
    {
        if (__state == null) return;
        try
        {
            KSM_Share.ReportBook(_actionData.invData.holdingEntity as EntityPlayerLocal, __state.Unlocks, __state.GetItemName());
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneSharedMagazines] sharing a book failed: " + ex);
        }
    }
}

[HarmonyPatch(typeof(ItemActionEat))]
[HarmonyPatch("ExecuteInstantAction")]
public class KSM_BookReadInstantPatch
{
    public static void Prefix(EntityAlive ent, ItemStack stack, out ItemClass __state)
    {
        __state = null;
        try
        {
            if (!(ent is EntityPlayerLocal player) || player.isEntityRemote) return;
            __state = KSM_Share.BookClass(stack?.itemValue);
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneSharedMagazines] book check failed: " + ex);
        }
    }

    public static void Postfix(EntityAlive ent, bool __result, ItemClass __state)
    {
        if (__state == null || !__result) return;
        try
        {
            KSM_Share.ReportBook(ent as EntityPlayerLocal, __state.Unlocks, __state.GetItemName());
        }
        catch (Exception ex)
        {
            Log.Error("[KitsuneSharedMagazines] sharing a book failed: " + ex);
        }
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
        bool isRead = cmd != null && cmd.StartsWith(KSM_Share.ReadVerb + " ", StringComparison.Ordinal);
        bool isBook = cmd != null && cmd.StartsWith(KSM_Share.BookVerb + " ", StringComparison.Ordinal);
        if (!isRead && !isBook) return true;
        try
        {
            if (_world != null && isRead) KSM_Share.OnReadReport(_world, __instance.Sender, cmd);
            if (_world != null && isBook) KSM_Share.OnBookReport(_world, __instance.Sender, cmd);
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
        if (!__instance.bExecute || lines == null || lines.Count == 0 || lines[0] == null) return true;
        bool isApply = lines[0].StartsWith(KSM_Share.ApplyVerb + " ", StringComparison.Ordinal);
        bool isBook = lines[0].StartsWith(KSM_Share.BookApplyVerb + " ", StringComparison.Ordinal);
        if (!isApply && !isBook) return true;
        try
        {
            if (_world != null && isApply) KSM_Share.OnApply(_world, lines[0]);
            if (_world != null && isBook) KSM_Share.OnBookApply(_world, lines[0]);
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
    public const string BookVerb = "ksm_book";
    public const string BookApplyVerb = "ksm_bookapply";

    // A magazine read takes about a second (Eat action Delay 1.0), so a faster
    // stream of reports from one player is not real reading.
    private const float MinSecondsBetweenReports = 0.5f;
    private const int MaxLevelsPerRead = 10;

    private static readonly FastTags<TagGroup.Global> MagazineTag = FastTags<TagGroup.Global>.Parse("csm");
    private static readonly Dictionary<int, float> lastReportAt = new Dictionary<int, float>();

    // The local player among an action's targets, if any (null on a dedicated
    // server, where every reader is remote).
    public static EntityPlayerLocal LocalTarget(MinEventActionTargetedBase action)
    {
        List<EntityAlive> targets = action.targets;
        if (targets == null) return null;
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] is EntityPlayerLocal local && !local.isEntityRemote) return local;
        }
        return null;
    }

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

        if (!TakeReportSlot(sender)) return;
        Share(reader, sender.playerName, parts[1], levels);
    }

    // Reading a magazine or a book takes about a second, so a faster stream of
    // reports from one player is not real reading.
    private static bool TakeReportSlot(ClientInfo sender)
    {
        float now = Time.time;
        if (lastReportAt.TryGetValue(sender.entityId, out float last) && now - last < MinSecondsBetweenReports)
        {
            Log.Warning("[KitsuneSharedMagazines] Ignored a read report from " + sender.playerName + " (too fast)");
            return false;
        }
        lastReportAt[sender.entityId] = now;
        return true;
    }

    // Server (or listen host): send the levels to every other party member.
    public static void Share(EntityPlayer reader, string readerName, string progressionName, int levels)
    {
        int shared = SendToParty(reader,
            host => Apply(host, progressionName, levels, readerName),
            ApplyVerb + " " + progressionName + " " + levels + " " + readerName);

        if (shared > 0)
        {
            Log.Out("[KitsuneSharedMagazines] " + readerName + " read " + progressionName + " +" + levels
                + ", shared with " + shared + " party member(s)");
        }
    }

    // Every other party member: the listen host in-process, everyone else as a
    // console line to their client. Returns how many were reached.
    private static int SendToParty(EntityPlayer reader, Action<EntityPlayerLocal> applyHost, string clientLine)
    {
        Party party = reader?.Party;
        if (party == null || party.MemberList == null) return 0;

        ConnectionManager connections = SingletonMonoBehaviour<ConnectionManager>.Instance;
        int shared = 0;
        for (int i = 0; i < party.MemberList.Count; i++)
        {
            EntityPlayer member = party.MemberList[i];
            if (member == null || member.entityId == reader.entityId) continue;

            if (member is EntityPlayerLocal host)
            {
                applyHost(host);
                shared++;
                continue;
            }

            ClientInfo client = connections?.Clients?.ForEntityId(member.entityId);
            if (client == null) continue;
            client.SendPackage(NetPackageManager.GetPackage<NetPackageConsoleCmdClient>().Setup(clientLine, true));
            shared++;
        }
        return shared;
    }

    // ---- Perk books ----

    // The book's item class when this item is a perk book (its Unlocks names a
    // ProgressionType.Book perk), else null.
    public static ItemClass BookClass(ItemValue itemValue)
    {
        ItemClass itemClass = itemValue?.ItemClass;
        if (itemClass == null || string.IsNullOrEmpty(itemClass.Unlocks)) return null;
        Progression.ProgressionClasses.TryGetValue(itemClass.Unlocks, out ProgressionClass progression);
        return progression != null && progression.IsBook ? itemClass : null;
    }

    public static ProgressionValue Book(EntityAlive player, string perkName)
    {
        if (player == null || player.Progression == null || string.IsNullOrEmpty(perkName)) return null;
        ProgressionValue value = player.Progression.GetProgressionValue(perkName);
        if (value == null || value.ProgressionClass == null || !value.ProgressionClass.IsBook) return null;
        return value;
    }

    public static void ReportBook(EntityPlayerLocal reader, string perkName, string bookItem)
    {
        if (reader == null || bookItem == null) return;
        ConnectionManager connections = SingletonMonoBehaviour<ConnectionManager>.Instance;
        if (connections == null) return;
        if (connections.IsServer)
        {
            ShareBook(reader, reader.EntityName, perkName, bookItem);
            return;
        }
        connections.SendToServer(NetPackageManager.GetPackage<NetPackageConsoleCmdServer>()
            .Setup(BookVerb + " " + perkName + " " + bookItem));
    }

    // Server side: "ksm_book <perk> <book item>" from a client.
    public static void OnBookReport(World world, ClientInfo sender, string cmd)
    {
        if (sender == null) return;
        string[] parts = cmd.Split(' ');
        if (parts.Length != 3) return;

        // The named item must really be the book for that perk, and the
        // reader must really have it now.
        ItemClass book = ItemClass.GetItemClass(parts[2]);
        if (book == null || !string.Equals(book.Unlocks, parts[1], StringComparison.Ordinal)) return;
        EntityPlayer reader = world.GetEntity(sender.entityId) as EntityPlayer;
        if (Book(reader, parts[1]) == null) return;

        if (!TakeReportSlot(sender)) return;
        ShareBook(reader, sender.playerName, parts[1], parts[2]);
    }

    public static void ShareBook(EntityPlayer reader, string readerName, string perkName, string bookItem)
    {
        int shared = SendToParty(reader,
            host => ApplyBook(host, perkName, bookItem, readerName),
            BookApplyVerb + " " + perkName + " " + bookItem + " " + readerName);

        if (shared > 0)
        {
            Log.Out("[KitsuneSharedMagazines] " + readerName + " read " + bookItem + ", shared with "
                + shared + " party member(s)");
        }
    }

    // Member side: "ksm_bookapply <perk> <book item> <reader name...>".
    public static void OnBookApply(World world, string line)
    {
        string[] parts = line.Split(new[] { ' ' }, 4);
        if (parts.Length < 3) return;
        string readerName = parts.Length == 4 && parts[3].Length > 0 ? parts[3] : "A party member";
        ApplyBook(world.GetPrimaryPlayer(), parts[1], parts[2], readerName);
    }

    // Mirrors the book's own effects: unlock its perk if this player hasn't
    // read it, then grant the series bonus if that completes their set.
    public static void ApplyBook(EntityPlayerLocal player, string perkName, string bookItem, string readerName)
    {
        ProgressionValue value = Book(player, perkName);
        if (value == null || value.Level > 0) return;
        value.Level = value.ProgressionClass.MaxLevel;

        ProgressionValue bonus = SeriesBonusIfComplete(player, value.ProgressionClass.Parent);
        if (bonus != null) bonus.Level = bonus.ProgressionClass.MaxLevel;

        player.Progression.bProgressionStatsChanged = true;
        player.bPlayerStatsChanged = true;

        string bookName = ItemClass.GetItemClass(bookItem)?.GetLocalizedItemName();
        if (string.IsNullOrEmpty(bookName)) bookName = bookItem;
        string message = readerName + " shared " + bookName + " with you.";
        if (bonus != null)
        {
            message += " That completes the series!";
            Audio.Manager.PlayInsidePlayerHead("read_skillbook_final", player.entityId);
        }
        GameManager.ShowTooltip(player, message, false, false, 0f);
    }

    // Vanilla grants the series bonus perk (perk...Complete, a book in the same
    // group) once 7 books in the group are unlocked — i.e. all the others.
    // Returns the bonus when this player has just met that, else null.
    private static ProgressionValue SeriesBonusIfComplete(EntityPlayerLocal player, ProgressionClass series)
    {
        if (series == null || series.Children == null) return null;
        ProgressionValue bonus = null;
        for (int i = 0; i < series.Children.Count; i++)
        {
            ProgressionClass child = series.Children[i];
            ProgressionValue childValue = player.Progression.GetProgressionValue(child.Name);
            if (childValue == null) return null;
            if (child.Name.EndsWith("Complete", StringComparison.Ordinal))
            {
                bonus = childValue;
                continue;
            }
            if (childValue.Level <= 0) return null;
        }
        return bonus != null && bonus.Level <= 0 ? bonus : null;
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
