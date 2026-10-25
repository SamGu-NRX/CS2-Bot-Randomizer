using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace BotRandomizer;

public sealed class BotRandomizerPlugin : BasePlugin
{
    // Opt-in: knives spawned without an owner (e.g. by subclass_create) get a random finish.
    public readonly FakeConVar<bool> DroppedKnifeSkins = new("br_dropped_knife_skins",
        "Give knives spawned without an owner a random finish (0 = off, 1 = on)", false);

    // Opt-in: favor expensive finishes on dropped knives. Bot loadouts are unaffected.
    public readonly FakeConVar<bool> DroppedKnifePremium = new("br_dropped_knife_premium",
        "Strongly favor expensive finishes on dropped knives (0 = off, 1 = on)", false);

    private readonly CosmeticStateStore _states = new();
    // Raw handles of dropped knives this plugin painted; claimed by the human who picks one up.
    private readonly HashSet<uint> _paintedDroppedKnives = [];
    private readonly HashSet<int> _pendingRerolls = [];
    private readonly HashSet<int> _warnedPlayerMusicModes = [];
    private PlayerMusicKitSelection? _playerMusic;
    private readonly Dictionary<int, PlayerMusicSnapshot> _originalPlayerMusic = [];

    private readonly record struct PlayerMusicSnapshot(
        uint ControllerHandle, ushort InventoryMusicId, int MusicKitId, int MusicKitMvps, bool MvpNoMusic);

    public FakeConVar<int> PlayerMusicKit = new(
        "br_player_music_kit", "Human music kit: 0 = off, -1 = random per map, positive = catalog index", 0);

    private CosmeticCatalog? _catalog;
    private CosmeticRoller? _roller;
    private CosmeticApplicator? _applicator;
    private WeaponItemViewStore? _weaponItemViews;
    private bool _giveNamedItemHooked;
    private bool _giveNamedItemErrorLogged;

    public override string ModuleName => "BotRandomizer";
    public override string ModuleVersion => "1.3.4";
    public override string ModuleAuthor => "ed0ard, Misaka17032, unicbm & XBribo";
    public override string ModuleDescription =>
        "Stable per-bot knives, gloves, weapon skins, stickers, charms, agents and music kits";

    public override void Load(bool hotReload)
    {
        LoadCatalog();
        LoadAttributeWriter();
        PlayerMusicKit.ValueChanged += OnPlayerMusicKitChanged;

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);
        RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart, HookMode.Pre);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundMvp>(OnRoundMvp, HookMode.Pre);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventItemPickup>(OnItemPickup);
        if (_weaponItemViews?.NativeAvailable == true)
        {
            VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPre, HookMode.Pre);
            _giveNamedItemHooked = true;
        }

        if (hotReload)
            RestoreAllBots(CosmeticScope.All);
    }

    public override void Unload(bool hotReload)
    {
        PlayerMusicKit.ValueChanged -= OnPlayerMusicKitChanged;
        RestorePlayerMusicKits();
        if (_giveNamedItemHooked)
        {
            VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItemPre, HookMode.Pre);
            _giveNamedItemHooked = false;
        }

        _weaponItemViews?.Dispose();
        _weaponItemViews = null;
    }

    private void LoadCatalog()
    {
        try
        {
            var catalogPath = Path.Combine(ModuleDirectory, "cosmetic_catalog.json");
            var placementPath = Path.Combine(ModuleDirectory, "charm_placements.json");
            _catalog = CosmeticCatalog.Load(catalogPath);
            var charmPlacements = CharmPlacementCatalog.Load(placementPath, _catalog);
            _roller = new CosmeticRoller(_catalog, charmPlacements);
            _playerMusic = new PlayerMusicKitSelection(_catalog.MusicKits);
            Logger.LogInformation(
                "[BotRandomizer] Catalog loaded: {Weapons} weapons, {Paints} weapon paints, {Stickers} stickers, {Charms} charms; {CharmPositions} charm positions for {CharmWeapons} weapons",
                _catalog.WeaponCount,
                _catalog.WeaponPaintCount,
                _catalog.StickerKits.Count,
                _catalog.KeychainDefinitions.Count,
                charmPlacements.PlacementCount,
                charmPlacements.WeaponCount);
        }
        catch (Exception exception)
        {
            _catalog = null;
            _roller = null;
            _playerMusic = null;
            Logger.LogError(
                exception,
                "[BotRandomizer] cosmetic_catalog.json or charm_placements.json is invalid; randomization disabled");
        }
    }

    private void LoadAttributeWriter()
    {
        MemoryFunctionWithReturn<nint, string, float, int>? writer = null;
        try
        {
            // SetOrAddAttributeValueByName
            writer = new MemoryFunctionWithReturn<nint, string, float, int>(
                RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                    ? "55 48 89 E5 41 57 41 56 41 55 49 89 FD 41 54 53 48 89 F3 48 83 EC ? F3 0F 11 85"
                    : "48 89 4C 24 ? 53 41 55 41 56");
        }
        catch (Exception exception)
        {
            Logger.LogError(
                exception,
                "[BotRandomizer] SetOrAddAttributeValueByName signature failed; economic cosmetics disabled");
        }

        _applicator = new CosmeticApplicator(writer, Logger);
        MemoryFunctionWithReturn<nint, nint>? itemViewConstructor = null;
        if (writer is not null)
        {
            try
            {
                // CEconItemViewConstructor
                itemViewConstructor = new MemoryFunctionWithReturn<nint, nint>(
                    RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                        ? "55 48 8D 05 ? ? ? ? 66 0F EF C0 48 89 E5 41 57 41 56 4C 8D 35 ? ? ? ? 41 55 41 54 45 31 E4"
                        : "48 89 5C 24 ? 48 89 6C 24 ? 48 89 74 24 ? 57 41 54 41 55 41 56 41 57 48 83 EC ? 48 8B F9 48 8D 05");
            }
            catch (Exception exception)
            {
                Logger.LogError(
                    exception,
                    "[BotRandomizer] CEconItemView constructor signature failed; weapon cosmetics disabled");
            }
        }
        _weaponItemViews = new WeaponItemViewStore(itemViewConstructor, writer, Logger);
    }

    private void OnEntitySpawned(CEntityInstance entity)
    {
        // Every spawn passes through here; check the cheap conditions and the designer
        // name before reading any schema field.
        if (!DroppedKnifeSkins.Value
            || _roller is null
            || _applicator?.NativeAvailable != true
            || !entity.DesignerName.StartsWith("weapon_knife", StringComparison.Ordinal))
        {
            return;
        }

        // subclass_create knives keep the default knife's definition (42 CT, 59 T) and differ
        // only by subclass. Inventory and bot knives carry their real definition, so they never match.
        var weapon = new CBasePlayerWeapon(entity.Handle);
        var item = weapon.AttributeManager?.Item;
        if (item is null
            || item.ItemDefinitionIndex is not (42 or 59)
            || !KnifeSubclass.TryGetDefIndex(weapon.SubclassID.Value, out var defIndex))
        {
            return;
        }

        // Apply during the spawn, before the knife is first sent to clients: a finish set on a
        // knife clients already received shows in its name but is never drawn.
        if (_applicator.ApplyDroppedKnifeFinish(weapon, _roller, defIndex, DroppedKnifePremium.Value))
            _paintedDroppedKnives.Add(weapon.EntityHandle.Raw);
    }

    private void OnEntityDeleted(CEntityInstance entity)
    {
        if (_paintedDroppedKnives.Count == 0)
            return;

        _paintedDroppedKnives.Remove(entity.EntityHandle.Raw);
    }

    private void OnMapStart(string mapName)
    {
        RestorePlayerMusicKits();
        _states.Reset();
        _paintedDroppedKnives.Clear();
        _pendingRerolls.Clear();
        _roller?.ResetMap();
        _playerMusic?.ResetMap();
        // Resolve now so random mode has one shared selection for the whole map.
        ResolvePlayerMusicKit();
        // Keep constructed item-view storage alive across map transitions. Each view is
        // fully overwritten before reuse and is freed only at slot teardown or unload.
        _applicator?.Reset();
        foreach (var agent in RandomizerAssets.CounterTerroristAgents)
            Server.PrecacheModel(agent.ModelPath);
        foreach (var agent in RandomizerAssets.TerroristAgents)
            Server.PrecacheModel(agent.ModelPath);
    }

    private void OnClientDisconnect(int playerSlot)
    {
        _states.Remove(playerSlot);
        _originalPlayerMusic.Remove(playerSlot);
        _pendingRerolls.Remove(playerSlot);
        _weaponItemViews?.ClearSlot(playerSlot);
        _applicator?.ClearSlot(playerSlot);
    }

    private HookResult OnRoundPrestart(EventRoundPrestart @event, GameEventInfo info)
    {
        foreach (var player in Utilities.GetPlayers())
            ConsumePendingReroll(player);
        // Deferred one frame so this round's intro entities already exist. Rerolls are consumed
        // first, so the intro publishes the loadout the bot will actually spawn with.
        Server.NextFrame(ApplyIntroAgents);
        if (PlayerMusicKit.Value != 0)
            Server.NextFrame(ApplyAllPlayerMusicKits);
        return HookResult.Continue;
    }

    // Updates both teams only while the game is preparing the team intro
    private void ApplyIntroAgents()
    {
        var gameRules = Utilities
            .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()
            ?.GameRules;

        if (gameRules is null || !gameRules.TeamIntroPeriod)
            return;

        ApplyIntroAgentsForTeam(
            "team_intro_counterterrorist",
            RandomizerAssets.CounterTerroristTeam,
            gameRules.CTTeamIntroVariant);
        ApplyIntroAgentsForTeam(
            "team_intro_terrorist",
            RandomizerAssets.TerroristTeam,
            gameRules.TTeamIntroVariant);
    }

    // Matches bot controllers to active intro positions and publishes their agent items
    private void ApplyIntroAgentsForTeam(string designerName, byte team, int introVariant)
    {
        var teamPlayers = Utilities.GetPlayers()
            .Where(player => player.IsValid && player.TeamNum == team)
            .OrderBy(player => player.Slot)
            .ToList();
        var bots = teamPlayers
            .Where(player => player is { IsBot: true, IsHLTV: false })
            .ToList();

        if (bots.Count == 0)
            return;

        var previews = Utilities
            .FindAllEntitiesByDesignerName<CCSGO_TeamPreviewCharacterPosition>(designerName)
            .Where(preview => preview.IsValid && preview.Variant == introVariant)
            .OrderBy(preview => preview.Ordinal)
            .Take(teamPlayers.Count)
            .ToList();
        var unmatchedBots = new List<CCSPlayerController>(bots);
        var unmatchedPreviews = new List<CCSGO_TeamPreviewCharacterPosition>(previews);

        foreach (var bot in bots.Where(bot => bot.SteamID != 0))
        {
            var preview = unmatchedPreviews.FirstOrDefault(candidate => candidate.Xuid == bot.SteamID);
            if (preview is null)
                continue;

            ApplyIntroAgent(preview, bot);
            unmatchedBots.Remove(bot);
            unmatchedPreviews.Remove(preview);
        }

        var botPreviews = unmatchedPreviews
            .Where(preview => preview.Xuid == 0)
            .OrderBy(preview => preview.Ordinal)
            .ToList();
        var pairCount = Math.Min(unmatchedBots.Count, botPreviews.Count);

        for (var i = 0; i < pairCount; i++)
            ApplyIntroAgent(botPreviews[i], unmatchedBots[i]);
    }

    private void ApplyIntroAgent(
        CCSGO_TeamPreviewCharacterPosition preview,
        CCSPlayerController bot)
    {
        var state = GetOrCreateState(bot);
        if (state is null)
            return;

        // Publishes the very agent this bot's pawn is given at spawn, so the intro and the
        // in-game model always match.
        preview.AgentItem.ItemDefinitionIndex = state.Loadout.AgentDefIndex;
        Utilities.SetStateChanged(
            preview,
            "CCSGO_TeamPreviewCharacterPosition",
            "m_agentItem");
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        ApplyPlayerMusicKit(@event.Userid);
        ConsumePendingReroll(@event.Userid);
        var state = GetOrCreateState(@event.Userid);
        if (state is null)
            return HookResult.Continue;

        var slot = state.Slot;
        var userId = state.UserId;
        var generation = state.Generation;
        Server.NextFrame(() =>
        {
            if (!TryResolveCurrentBot(slot, userId, generation, out var player, out var pawn, out var current))
                return;

            ApplyIdentity(player, pawn, current, CosmeticScope.Agent | CosmeticScope.MusicKit);
            ApplyWearables(player, pawn, current);
            ScheduleWearableRetry(slot, userId, generation, 0.10f);
            ScheduleWearableRetry(slot, userId, generation, 0.25f);
        });

        return HookResult.Continue;
    }

    private HookResult OnGiveNamedItemPre(DynamicHook hook)
    {
        if (_catalog is null
            || _roller is null
            || _weaponItemViews is null)
        {
            return HookResult.Continue;
        }

        try
        {
            var itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
            var designerName = hook.GetParam<string>(1);
            if (string.IsNullOrWhiteSpace(designerName))
                return HookResult.Continue;

            var player = GetPlayerFromItemServices(itemServices);
            if (player is null)
                return HookResult.Continue;

            var state = GetOrCreateState(player);
            if (state is null || !_catalog.TryGetWeapon(designerName, out var weapon))
            {
                return HookResult.Continue;
            }

            var selection = _roller.GetOrCreateWeapon(state.Loadout, weapon.DefIndex);
            if (selection is not null
                && _weaponItemViews.TryPrepare(
                    state,
                    weapon,
                    selection,
                    includeStickers: true,
                    includeCharms: true,
                    player.SteamID,
                    out var itemViewHandle))
            {
                hook.SetParam(3, itemViewHandle);
            }
        }
        catch (Exception exception)
        {
            if (!_giveNamedItemErrorLogged)
            {
                _giveNamedItemErrorLogged = true;
                Logger.LogError(exception, "[BotRandomizer] GiveNamedItem pre-hook failed");
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnItemPickup(EventItemPickup @event, GameEventInfo info)
    {
        if (_applicator is null)
            return HookResult.Continue;
        if (string.IsNullOrEmpty(@event.Item)
            || (!@event.Item.Contains("knife", StringComparison.Ordinal)
                && !@event.Item.Contains("bayonet", StringComparison.Ordinal)))
        {
            return HookResult.Continue;
        }

        if (@event.Userid is { IsValid: true, IsBot: false } human && _paintedDroppedKnives.Count > 0)
        {
            var steamId = human.SteamID;
            var pawnHandle = human.PlayerPawn.Raw;
            Server.NextFrame(() => ClaimPaintedKnives(pawnHandle, steamId));
            return HookResult.Continue;
        }

        var state = GetOrCreateState(@event.Userid);
        if (state is null)
            return HookResult.Continue;

        ScheduleKnifeSync(state.Slot, state.UserId, state.Generation, nextFrame: true);
        ScheduleKnifeSync(state.Slot, state.UserId, state.Generation, delay: 0.10f);
        ScheduleKnifeSync(state.Slot, state.UserId, state.Generation, delay: 0.25f);
        return HookResult.Continue;
    }

    private void ClaimPaintedKnives(uint pawnHandle, ulong steamId)
    {
        if (_applicator is null)
            return;
        var pawn = new CHandle<CCSPlayerPawn>(pawnHandle).Value;
        var weapons = pawn is { IsValid: true } ? pawn.WeaponServices?.MyWeapons : null;
        if (weapons is null)
            return;
        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon is { IsValid: true } && _paintedDroppedKnives.Remove(weapon.EntityHandle.Raw))
                _applicator.ClaimDroppedKnife(weapon, steamId);
        }
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!@event.Disconnect)
            ApplyPlayerMusicKit(player);
        if (player is not { IsValid: true, IsBot: true } || player.UserId is not int userId)
            return HookResult.Continue;

        if (@event.Disconnect)
        {
            OnClientDisconnect(player.Slot);
            return HookResult.Continue;
        }

        if (_roller is null || !IsPlayableTeam(@event.Team))
        {
            _states.BumpGeneration(player.Slot);
            return HookResult.Continue;
        }

        _states.Reroll(
            player.Slot,
            userId,
            (byte)@event.Team,
            preserveMusic: true,
            music => _roller.RollLoadout((byte)@event.Team, music));
        var slot = player.Slot;
        AddTimer(
            0.10f,
            () => RestoreBot(
                slot,
                CosmeticScope.Agent | CosmeticScope.Knife | CosmeticScope.Gloves),
            TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        var playerKitId = ApplyPlayerMusicKit(@event.Userid);
        if (playerKitId != 0)
        {
            @event.Musickitid = playerKitId;
            @event.Musickitmvps = 0;
            @event.Nomusic = 0;
            return HookResult.Continue;
        }

        var state = GetOrCreateState(@event.Userid);
        var player = @event.Userid;
        if (state is null || player is null)
        {
            return HookResult.Continue;
        }

        ApplyMusicKit(player, state.Loadout.MusicKit, 0);
        @event.Musickitid = state.Loadout.MusicKit;
        @event.Musickitmvps = 0;
        @event.Nomusic = 0;
        return HookResult.Continue;
    }

    private SlotCosmeticState? GetOrCreateState(CCSPlayerController? player)
    {
        if (_roller is null
            || player is not { IsValid: true, IsBot: true, IsHLTV: false }
            || player.UserId is not int userId
            || !IsPlayableTeam(player.TeamNum))
        {
            return null;
        }

        var team = (byte)player.TeamNum;
        return _states.GetOrCreate(
            player.Slot,
            userId,
            team,
            music => _roller.RollLoadout(team, music));
    }

    private void ApplyIdentity(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        SlotCosmeticState state,
        CosmeticScope scope)
    {
        if (_applicator is null)
            return;

        if ((scope & CosmeticScope.Agent) != 0)
        {
            _applicator.ApplyAgent(pawn, state.Loadout.AgentModel);
        }

        if ((scope & CosmeticScope.MusicKit) != 0)
        {
            ApplyMusicKit(player, state.Loadout.MusicKit, 0);
        }
    }

    private void ApplyWearables(
        CCSPlayerController player,
        CCSPlayerPawn pawn,
        SlotCosmeticState state,
        CosmeticScope scope = CosmeticScope.Knife | CosmeticScope.Gloves)
    {
        if (_applicator is null
            || player is not { IsValid: true, IsBot: true }
            || !pawn.IsValid)
        {
            return;
        }

        if ((scope & CosmeticScope.Knife) != 0)
        {
            _applicator.ApplyKnife(player, pawn, state.Loadout.Knife);
        }

        if ((scope & CosmeticScope.Gloves) != 0)
        {
            if (_applicator.ApplyGloves(player, pawn, state.Loadout.Glove))
            {
                var generation = state.Generation;
                var pawnHandle = pawn.Handle;
                AddTimer(0.2f, () =>
                {
                    if (TryResolveCurrentBot(
                            state.Slot,
                            state.UserId,
                            generation,
                            out _,
                            out var currentPawn,
                            out _)
                        && currentPawn.Handle == pawnHandle)
                    {
                        _applicator.ShowGloves(currentPawn);
                    }
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
        }
    }

    private void ScheduleWearableRetry(
        int slot,
        int userId,
        long generation,
        float delay,
        CosmeticScope scope = CosmeticScope.Knife | CosmeticScope.Gloves)
    {
        AddTimer(delay, () =>
        {
            if (TryResolveCurrentBot(slot, userId, generation, out var player, out var pawn, out var state))
                ApplyWearables(player, pawn, state, scope);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void ScheduleKnifeSync(
        int slot,
        int userId,
        long generation,
        float? delay = null,
        bool nextFrame = false)
    {
        void Callback()
        {
            if (_applicator is not null
                && TryResolveCurrentBot(slot, userId, generation, out _, out var pawn, out _))
            {
                _applicator.SyncPickedUpKnife(pawn);
            }
        }

        if (nextFrame)
            Server.NextFrame(Callback);
        else if (delay is float seconds)
            AddTimer(seconds, Callback, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private bool TryResolveCurrentBot(
        int slot,
        int userId,
        long generation,
        out CCSPlayerController player,
        out CCSPlayerPawn pawn,
        out SlotCosmeticState state)
    {
        player = null!;
        pawn = null!;
        state = null!;
        if (!_states.IsCurrent(slot, userId, generation))
            return false;

        var resolved = Utilities.GetPlayerFromSlot(slot);
        if (resolved is not { IsValid: true, IsBot: true, IsHLTV: false }
            || resolved.UserId != userId
            || resolved.PlayerPawn?.Value is not { IsValid: true } resolvedPawn
            || !_states.TryGet(slot, out var resolvedState))
        {
            return false;
        }

        player = resolved;
        pawn = resolvedPawn;
        state = resolvedState;
        return true;
    }

    private void RestoreBot(int slot, CosmeticScope scope)
    {
        var player = Utilities.GetPlayerFromSlot(slot);
        if (_roller is null
            || player is not { IsValid: true, IsBot: true, IsHLTV: false }
            || player.UserId is not int userId
            || !IsPlayableTeam(player.TeamNum))
        {
            return;
        }

        var state = GetOrCreateState(player);
        if (state is null)
            return;

        var generation = state.Generation;
        Server.NextFrame(() =>
        {
            if (!TryResolveCurrentBot(slot, userId, generation, out var currentPlayer, out var pawn, out var current))
                return;

            ApplyIdentity(currentPlayer, pawn, current, scope);
            var wearableScope = scope & (CosmeticScope.Knife | CosmeticScope.Gloves);
            if (wearableScope != CosmeticScope.None)
            {
                ApplyWearables(currentPlayer, pawn, current, wearableScope);
                ScheduleWearableRetry(slot, userId, generation, 0.10f, wearableScope);
                ScheduleWearableRetry(slot, userId, generation, 0.25f, wearableScope);
            }
        });
    }

    private void RestoreAllBots(CosmeticScope scope)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player is { IsValid: true, IsBot: true, IsHLTV: false })
                RestoreBot(player.Slot, scope);
        }
    }

    private int ResolvePlayerMusicKit()
    {
        var mode = PlayerMusicKit.Value;
        var kitId = _playerMusic?.Resolve(mode) ?? 0;
        if (_playerMusic is not null && mode != 0 && kitId == 0 && _warnedPlayerMusicModes.Add(mode))
        {
            Logger.LogWarning(
                "[BotRandomizer] Unknown br_player_music_kit index {Index}; human music kit ignored", mode);
        }
        return kitId;
    }

    private bool TryGetPlayerMusicKit(CCSPlayerController? player, out int kitId)
    {
        kitId = 0;
        if (player is not { IsValid: true, IsBot: false, IsHLTV: false })
            return false;
        kitId = ResolvePlayerMusicKit();
        return kitId != 0;
    }

    private int ApplyPlayerMusicKit(CCSPlayerController? player)
    {
        if (!TryGetPlayerMusicKit(player, out var kitId)
            || player!.InventoryServices is not { } inventory)
        {
            return 0;
        }

        // Preserve the first values, not a later reapplication of our own override.
        if (!_originalPlayerMusic.TryGetValue(player.Slot, out var original)
            || original.ControllerHandle != player.EntityHandle.Raw)
        {
            _originalPlayerMusic[player.Slot] = new PlayerMusicSnapshot(
                player.EntityHandle.Raw, inventory.MusicID, player.MusicKitID,
                player.MusicKitMVPs, player.MvpNoMusic);
        }

        ApplyMusicKit(player, kitId, 0);
        return kitId;
    }

    private void RestorePlayerMusicKits()
    {
        foreach (var (slot, original) in _originalPlayerMusic)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is { IsValid: true, IsBot: false, IsHLTV: false }
                && player.EntityHandle.Raw == original.ControllerHandle)
            {
                ApplyMusicKit(player, original.MusicKitId, original.MusicKitMvps,
                    original.MvpNoMusic, original.InventoryMusicId);
            }
        }
        _originalPlayerMusic.Clear();
    }

    private void ApplyAllPlayerMusicKits()
    {
        if (ResolvePlayerMusicKit() == 0)
        {
            RestorePlayerMusicKits();
            return;
        }

        foreach (var player in Utilities.GetPlayers())
            ApplyPlayerMusicKit(player);
    }

    private void OnPlayerMusicKitChanged(object? sender, int value)
        => ApplyAllPlayerMusicKits();

    [ConsoleCommand("br_player_music_kits", "List catalog music kit indexes and English names")]
    public void OnPlayerMusicKitsCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (_catalog is null)
        {
            command.ReplyToCommand("Cosmetic catalog is unavailable.");
            return;
        }

        foreach (var kitId in _catalog.MusicKits)
        {
            command.ReplyToCommand(_catalog.MusicKitNames.TryGetValue(kitId, out var name)
                ? $"{kitId}: {name}"
                : kitId.ToString());
        }
    }

    [ConsoleCommand("br_player_music_reroll", "Reroll the shared human music kit in random mode")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnPlayerMusicRerollCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (PlayerMusicKit.Value != -1)
        {
            command.ReplyToCommand("Set br_player_music_kit to -1 to reroll human music.");
            return;
        }
        if (_playerMusic is null)
        {
            command.ReplyToCommand("Cosmetic catalog is unavailable.");
            return;
        }

        _playerMusic.Reroll();
        ApplyAllPlayerMusicKits();
        command.ReplyToCommand($"Human music kit: {ResolvePlayerMusicKit()}");
    }

    [ConsoleCommand("br_reroll", "Queue new loadouts for the next safe spawn")]
    public void OnRerollCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (_roller is null)
        {
            command.ReplyToCommand("Cosmetic catalog is unavailable.");
            return;
        }

        var target = command.ArgCount >= 2 ? command.GetArg(1) : "all";
        int? targetSlot = null;
        if (target != "all")
        {
            if (!int.TryParse(target, out var slot))
            {
                command.ReplyToCommand("Usage: br_reroll [all|bot slot]");
                return;
            }
            targetSlot = slot;
        }

        var bots = Utilities.GetPlayers()
            .Where(bot =>
                bot is { IsValid: true, IsBot: true, IsHLTV: false }
                && bot.UserId is not null
                && IsPlayableTeam(bot.TeamNum)
                && (targetSlot is null || bot.Slot == targetSlot.Value))
            .ToArray();

        if (bots.Length == 0)
        {
            command.ReplyToCommand("No matching bot slots.");
            return;
        }

        foreach (var bot in bots)
            _pendingRerolls.Add(bot.Slot);

        command.ReplyToCommand(
            $"Queued {bots.Length} bot loadout(s) for the next safe spawn.");
    }

    private void ConsumePendingReroll(CCSPlayerController? player)
    {
        if (_roller is null
            || player is not { IsValid: true, IsBot: true, IsHLTV: false }
            || player.UserId is not int userId
            || !IsPlayableTeam(player.TeamNum)
            || !_pendingRerolls.Remove(player.Slot))
        {
            return;
        }

        var team = (byte)player.TeamNum;
        _states.Reroll(
            player.Slot,
            userId,
            team,
            preserveMusic: false,
            music => _roller.RollLoadout(team, music));
    }

    private static bool IsPlayableTeam(int team)
        => team is RandomizerAssets.TerroristTeam or RandomizerAssets.CounterTerroristTeam;

    private static CCSPlayerController? GetPlayerFromItemServices(CCSPlayer_ItemServices itemServices)
    {
        var pawn = itemServices.Pawn.Value;
        if (pawn is not { IsValid: true } || pawn.Controller.Value is not { IsValid: true } controller)
            return null;

        var player = new CCSPlayerController(controller.Handle);
        return player is { IsValid: true, IsBot: true, IsHLTV: false } ? player : null;
    }

    private static void ApplyMusicKit(
        CCSPlayerController player,
        int kitId,
        int musicKitMvps,
        bool mvpNoMusic = false,
        ushort? inventoryMusicId = null)
    {
        var inventory = player.InventoryServices;
        if (inventory is not null)
        {
            inventory.MusicID = inventoryMusicId ?? checked((ushort)kitId);
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
        }

        player.MusicKitID = kitId;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
        player.MusicKitMVPs = musicKitMvps;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitMVPs");
        player.MvpNoMusic = mvpNoMusic;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_bMvpNoMusic");
    }

}
