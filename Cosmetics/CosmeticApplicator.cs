using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Microsoft.Extensions.Logging;

namespace BotRandomizer;

internal sealed class CosmeticApplicator
{
    private readonly MemoryFunctionWithReturn<nint, string, float, int>? _setAttributeByName;
    private readonly ILogger _logger;
    private readonly Dictionary<int, AppliedKnifeCosmetic> _appliedKnives = [];
    private readonly Dictionary<int, AppliedGloveCosmetic> _appliedGloves = [];

    internal CosmeticApplicator(
        MemoryFunctionWithReturn<nint, string, float, int>? setAttributeByName,
        ILogger logger)
    {
        _setAttributeByName = setAttributeByName;
        _logger = logger;
    }

    internal bool NativeAvailable => _setAttributeByName is not null;

    internal void ClearSlot(int slot)
    {
        _appliedKnives.Remove(slot);
        _appliedGloves.Remove(slot);
    }

    internal void Reset()
    {
        _appliedKnives.Clear();
        _appliedGloves.Clear();
    }

    internal void ApplyAgent(CCSPlayerPawn pawn, string model)
    {
        if (!pawn.IsValid)
            return;

        pawn.SetModel(model);
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_CBodyComponent");
        var color = pawn.Render;
        pawn.Render = Color.FromArgb(255, color.R, color.G, color.B);
        Utilities.SetStateChanged(pawn, "CBaseModelEntity", "m_clrRender");
    }

    internal void ApplyKnife(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        KnifeSelection selection)
    {
        if (!pawn.IsValid)
            return;

        try
        {
            var weapons = pawn.WeaponServices?.MyWeapons;
            if (weapons is null)
                return;

            foreach (var handle in weapons)
            {
                var weapon = handle.Value;
                if (weapon is not { IsValid: true })
                    continue;
                if (weapon.DesignerName is not ("weapon_knife" or "weapon_knife_t"))
                    continue;

                var item = weapon.AttributeManager?.Item;
                if (item is null)
                    return;

                var fingerprint = KnifeCosmeticFingerprint.From(selection);
                if (_appliedKnives.TryGetValue(player.Slot, out var applied)
                    && applied.PawnHandle == pawn.EntityHandle.Raw
                    && applied.WeaponHandle == weapon.EntityHandle.Raw
                    && applied.Fingerprint == fingerprint
                    && item.ItemID == applied.ItemId
                    && item.ItemDefinitionIndex == selection.DefIndex)
                {
                    return;
                }

                weapon.AcceptInput("ChangeSubclass", value: selection.DefIndex.ToString());
                item.ItemDefinitionIndex = selection.DefIndex;
                ApplyKnifeFinish(weapon, item, selection.PaintKit, selection.Wear);
                _appliedKnives[player.Slot] = new AppliedKnifeCosmetic(
                    pawn.EntityHandle.Raw,
                    weapon.EntityHandle.Raw,
                    fingerprint,
                    item.ItemID);
                return;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[BotRandomizer] Failed to apply knife cosmetics");
        }
    }

    // Returns true when the knife was given a finish (or vanilla) and should be tracked.
    internal bool ApplyDroppedKnifeFinish(CBasePlayerWeapon weapon, CosmeticRoller roller, ushort defIndex, bool premium)
    {
        if (!NativeAvailable || !weapon.IsValid || weapon.OwnerEntity.IsValid)
            return false;
        // Inventory finishes can be resolved through an item ID rather than local
        // paint attributes. Leave previously owned inventory knives alone too.
        if (weapon.OriginalOwnerXuidLow != 0 || weapon.OriginalOwnerXuidHigh != 0)
            return false;

        try
        {
            var item = weapon.AttributeManager?.Item;
            if (item is null
                || weapon.FallbackPaintKit > 0
                || HasPaintAttribute(item.AttributeList)
                || HasPaintAttribute(item.NetworkedDynamicAttributes)
                || !roller.TryRollKnifeFinish(defIndex, allowVanilla: true, premium, out var paintKit, out var wear))
            {
                return false;
            }

            // subclass_create leaves the default knife's definition on the item. Follow the
            // bot path: ChangeSubclass rebuilds the weapon's model data so the finish is drawn.
            weapon.AcceptInput("ChangeSubclass", value: defIndex.ToString());
            item.ItemDefinitionIndex = defIndex;
            // A subclass_create knife's item view starts uninitialized (quality 0, item ID 0), and the
            // client ignores finish attributes on it; knives from GiveNamedItem start initialized.
            item.Initialized = true;
            if (paintKit == 0)
            {
                // Vanilla: a star-quality knife with no finish.
                item.EntityQuality = 3;
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            }
            else
            {
                ApplyKnifeFinish(weapon, item, paintKit, wear);
            }
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[BotRandomizer] Failed to apply a dropped knife finish");
            return false;
        }
    }

    // An initialized item with no owner is replaced at the next round start, so the human who
    // picks a painted dropped knife up becomes its owner, as with an inventory knife.
    internal void ClaimDroppedKnife(CBasePlayerWeapon weapon, ulong steamId)
    {
        var item = weapon.AttributeManager?.Item;
        if (item is null)
            return;
        item.AccountID = AccountIdFromSteamId(steamId);
        weapon.OriginalOwnerXuidLow = (uint)steamId;
        weapon.OriginalOwnerXuidHigh = (uint)(steamId >> 32);
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidLow");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidHigh");
    }

    private static bool HasPaintAttribute(CAttributeList list)
    {
        var attributes = list.Attributes;
        for (var i = 0; i < attributes.Count; i++)
        {
            var attribute = attributes[i];
            // Economy attribute 6 is "set item texture prefab".
            if (attribute.AttributeDefinitionIndex == 6 && attribute.Value > 0)
                return true;
        }
        return false;
    }

    private void ApplyKnifeFinish(CBasePlayerWeapon weapon, CEconItemView item, int paintKit, float wear)
    {
        item.EntityQuality = 3;
        if (_setAttributeByName is not null)
        {
            item.AttributeList.Attributes.RemoveAll();
            item.NetworkedDynamicAttributes.Attributes.RemoveAll();
            AssignItemId(item);
            weapon.FallbackPaintKit = paintKit;
            weapon.FallbackSeed = 0;
            weapon.FallbackWear = wear;
            SetTextureAttributes(item.NetworkedDynamicAttributes, item.AttributeList, paintKit, 0, wear);
        }
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
    }

    internal bool ApplyGloves(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        GloveSelection selection)
    {
        if (_setAttributeByName is null || !pawn.IsValid)
            return false;

        try
        {
            var item = pawn.EconGloves;
            var fingerprint = GloveCosmeticFingerprint.From(selection);
            if (_appliedGloves.TryGetValue(player.Slot, out var applied)
                && applied.PawnHandle == pawn.EntityHandle.Raw
                && applied.Fingerprint == fingerprint
                && item.Initialized
                && item.ItemID == applied.ItemId
                && item.ItemDefinitionIndex == applied.ItemDefinitionIndex
                && item.AccountID == applied.AccountId)
            {
                return true;
            }

            item.ItemDefinitionIndex = selection.DefIndex;
            item.AccountID = AccountIdFromSteamId(player.SteamID);
            item.Initialized = true;
            AssignItemId(item);
            item.NetworkedDynamicAttributes.Attributes.RemoveAll();
            item.AttributeList.Attributes.RemoveAll();
            SetTextureAttributes(
                item.NetworkedDynamicAttributes,
                item.AttributeList,
                selection.PaintKit,
                0,
                selection.Wear);
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_EconGloves");
            pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,0");
            _appliedGloves[player.Slot] = new AppliedGloveCosmetic(
                pawn.EntityHandle.Raw,
                fingerprint,
                item.ItemID,
                item.ItemDefinitionIndex,
                item.AccountID);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[BotRandomizer] Failed to apply glove cosmetics");
            return false;
        }
    }

    internal void ShowGloves(CCSPlayerPawn pawn)
    {
        if (pawn.IsValid)
            pawn.AcceptInput("SetBodygroup", value: "first_or_third_person,1");
    }

    internal void SyncPickedUpKnife(CCSPlayerPawn pawn)
    {
        if (!pawn.IsValid)
            return;

        try
        {
            var weapons = pawn.WeaponServices?.MyWeapons;
            if (weapons is null)
                return;

            foreach (var handle in weapons)
            {
                var weapon = handle.Value;
                if (weapon is not { IsValid: true }
                    || !RandomizerAssets.KnifeDefIndexByName.TryGetValue(
                        weapon.DesignerName,
                        out var defIndex))
                {
                    continue;
                }

                var item = weapon.AttributeManager?.Item;
                if (item is null)
                    continue;

                weapon.AcceptInput("ChangeSubclass", value: defIndex.ToString());
                item.ItemDefinitionIndex = defIndex;
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[BotRandomizer] Failed to synchronize a picked-up knife");
        }
    }

    private void SetTextureAttributes(
        CAttributeList networkedAttributes,
        CAttributeList attributeList,
        int paintKit,
        int seed,
        float wear)
    {
        SetAttribute(networkedAttributes, "set item texture prefab", paintKit);
        SetAttribute(networkedAttributes, "set item texture seed", seed);
        SetAttribute(networkedAttributes, "set item texture wear", wear);
        SetAttribute(attributeList, "set item texture prefab", paintKit);
        SetAttribute(attributeList, "set item texture seed", seed);
        SetAttribute(attributeList, "set item texture wear", wear);
    }

    private void SetAttribute(CAttributeList attributes, string name, float value)
    {
        if (_setAttributeByName is not null && attributes.Handle != IntPtr.Zero)
            _setAttributeByName.Invoke(attributes.Handle, name, value);
    }

    private static void AssignItemId(CEconItemView item)
    {
        var itemId = EconItemIdAllocator.Next();
        item.ItemID = itemId;
        item.ItemIDLow = (uint)(itemId & uint.MaxValue);
        item.ItemIDHigh = (uint)(itemId >> 32);
    }

    private static uint AccountIdFromSteamId(ulong steamId)
    {
        const ulong steamId64AccountBase = 76_561_197_960_265_728;
        if (steamId >= steamId64AccountBase)
        {
            var accountId = steamId - steamId64AccountBase;
            return accountId <= uint.MaxValue ? (uint)accountId : 0;
        }
        return steamId <= uint.MaxValue ? (uint)steamId : 0;
    }

    private readonly record struct KnifeCosmeticFingerprint(
        ushort DefIndex,
        int PaintKit,
        int WearBits)
    {
        internal static KnifeCosmeticFingerprint From(KnifeSelection selection)
            => new(selection.DefIndex, selection.PaintKit, BitConverter.SingleToInt32Bits(selection.Wear));
    }

    private readonly record struct GloveCosmeticFingerprint(
        ushort DefIndex,
        int PaintKit,
        int WearBits)
    {
        internal static GloveCosmeticFingerprint From(GloveSelection selection)
            => new(selection.DefIndex, selection.PaintKit, BitConverter.SingleToInt32Bits(selection.Wear));
    }

    private readonly record struct AppliedKnifeCosmetic(
        uint PawnHandle,
        uint WeaponHandle,
        KnifeCosmeticFingerprint Fingerprint,
        ulong ItemId);

    private readonly record struct AppliedGloveCosmetic(
        uint PawnHandle,
        GloveCosmeticFingerprint Fingerprint,
        ulong ItemId,
        ushort ItemDefinitionIndex,
        uint AccountId);
}
