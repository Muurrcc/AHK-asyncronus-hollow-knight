using System;
using System.Collections.Generic;
using System.Linq;

namespace Hkmp.Game.Client.Save;

/// <summary>
/// Pure (game independent) rules that decide which PlayerData fields are individual per character and which
/// progress is shared by every player of the server, plus the merge rules used when a player's stored progress
/// meets the shared progress of the server. Linked into the unit tests.
/// <para>Criterion (see docs/DEVELOPMENT.md, "Shared progress"):</para>
/// <list type="bullet">
/// <item>Local only (never synced): the live combat state of the character (health, lifeblood, soul) and values
/// the game derives from other fields (maxHealth from maxHealthBase and charms, maxMP, MPReserveCap).</item>
/// <item>Per player (<c>SyncType.Player</c>): position/respawn/shade/dreamgate, money and consumables (geo, pale
/// ore, simple keys, rancid eggs, relics, essence), nail upgrades, charms (owned, equipped, notches) and map
/// markers.</item>
/// <item>Shared (<c>SyncType.Server</c>): everything a player obtains that is progress of the playthrough: masks
/// and mask shards, soul vessels and fragments, spells, abilities (movement, dream nail, nail arts), key items, maps,
/// zones visited, NPC dialogues and story events. Bosses and world state were already shared.</item>
/// </list>
/// </summary>
internal static class SharedProgressRules {
    /// <summary>
    /// The amount of soul one full soul vessel adds to MPReserveMax.
    /// </summary>
    public const int SoulPerVessel = 33;

    /// <summary>
    /// Fragments needed for one soul vessel.
    /// </summary>
    public const int FragmentsPerVessel = 3;

    /// <summary>
    /// Mask shards needed for one mask.
    /// </summary>
    public const int ShardsPerMask = 4;

    /// <summary>
    /// PlayerData fields that hold the live combat state of the local character (health, lifeblood, soul, hazard
    /// respawn, ...) or values derived from other fields. They are never networked, neither as global (Server)
    /// nor as player (Player) save data: every player keeps its own values, and they are recomputed by the game on
    /// respawn/bench (CharmUpdate + MaxHealth derive maxHealth/health from maxHealthBase and the equipped charms).
    /// When a shared max value (maxHealthBase, MPReserveMax) changes, the client adjusts these live.
    /// </summary>
    public static readonly HashSet<string> LocalOnlyPlayerDataFields = [
        "health", "maxHealth", "prevHealth", "healthBlue", "joniHealthBlue", "damagedBlue", "blockerHits",
        "MPCharge", "MPReserve", "maxMP", "MPReserveCap", "focusMP_amount",
        "isInvincible", "atBench", "hazardRespawnLocation", "hazardRespawnFacingRight"
    ];

    /// <summary>
    /// PlayerData fields that are individual per character but persistent (geo, charms, respawn point, shade,
    /// dreamgate, nail). If they are synced, they must be stored per player on the server
    /// (<c>SyncType.Player</c>, never broadcast to the other players); a Server sync type would make one player's
    /// value overwrite everybody's.
    /// </summary>
    public static readonly HashSet<string> PlayerSpecificDataFields = BuildPlayerSpecificDataFields();

    private static HashSet<string> BuildPlayerSpecificDataFields() {
        var set = new HashSet<string> {
            "soulLimited",
            "geo", "geoPool", "nailDamage",
            "charmSlots", "charmSlotsFilled", "equippedCharms", "overcharmed",
            "fragileHealth_unbreakable", "fragileGreed_unbreakable", "fragileStrength_unbreakable",
            "respawnScene", "respawnMarkerName", "respawnType", "mapZone",
            "shadeScene", "shadeMapZone", "shadePositionX", "shadePositionY", "shadeHealth", "shadeMP",
            "shadeFireballLevel", "shadeQuakeLevel", "shadeScreamLevel", "shadeSpecialType",
            "dreamgateMapPos", "dreamGateScene", "dreamGateX", "dreamGateY"
        };
        for (var i = 1; i <= 40; i++) {
            set.Add($"equippedCharm_{i}");
        }

        return set;
    }

    /// <summary>
    /// Masks: max masks, mask shards and the shops/rewards that sell or give mask shards (so a shard is not
    /// claimed once per player).
    /// </summary>
    public static readonly string[] MaskFields = [
        "maxHealthBase", "heartPieces", "heartPieceCollected", "heartPieceMax",
        "slyShellFrag1", "slyShellFrag2", "slyShellFrag3", "slyShellFrag4"
    ];

    /// <summary>
    /// Soul vessels: max reserve soul, vessel fragments and the shops/events that give vessel fragments.
    /// </summary>
    public static readonly string[] SoulVesselFields = [
        "MPReserveMax", "vesselFragments", "vesselFragmentCollected", "vesselFragmentMax",
        "slyVesselFrag1", "slyVesselFrag2", "vesselFragStagNest", "fountainVesselSummoned", "fountainGeo"
    ];

    /// <summary>
    /// Spells and their levels.
    /// </summary>
    public static readonly string[] SpellFields = [
        "hasSpell", "fireballLevel", "quakeLevel", "screamLevel"
    ];

    /// <summary>
    /// Abilities: movement, dream nail, nail arts and the lumafly lantern.
    /// </summary>
    public static readonly string[] AbilityFields = [
        "hasDash", "hasWalljump", "hasSuperDash", "hasShadowDash", "hasAcidArmour", "hasDoubleJump",
        "hasDreamNail", "hasDreamGate", "dreamNailUpgraded",
        "hasNailArt", "hasCyclone", "hasDashSlash", "hasUpwardSlash", "hasAllNailArts",
        "hasLantern"
    ];

    /// <summary>
    /// Key items that unlock story progress.
    /// </summary>
    public static readonly string[] KeyItemFields = [
        "hasKingsBrand", "hasTramPass", "hasCityKey", "hasSlykey", "gaveSlykey", "hasWhiteKey", "usedWhiteKey",
        "hasWaterwaysKey", "hasSpaKey", "hasLoveKey", "hasJournal", "hasHuntersMark", "hasGodfinder"
    ];

    /// <summary>
    /// Maps, quill and map pins, visited zones and zone events (trams, colosseum, Godhome).
    /// </summary>
    public static readonly string[] MapAndZoneFields = [
        "hasMap", "hasQuill",
        "mapDirtmouth", "mapCrossroads", "mapGreenpath", "mapFogCanyon", "mapRoyalGardens", "mapFungalWastes",
        "mapCity", "mapWaterways", "mapMines", "mapDeepnest", "mapCliffs", "mapOutskirts", "mapRestingGrounds",
        "mapAbyss",
        "hasPin", "hasPinBench", "hasPinCocoon", "hasPinDreamPlant", "hasPinGuardian", "hasPinBlackEgg",
        "hasPinShop", "hasPinSpa", "hasPinStag", "hasPinTram", "hasPinGhost",
        "visitedCrossroads", "visitedGreenpath", "visitedFungus", "visitedHive", "visitedCrossroadsInfected",
        "visitedRuins", "visitedMines", "visitedRoyalGardens", "visitedFogCanyon", "visitedDeepnest",
        "visitedRestingGrounds", "visitedWaterways", "visitedAbyss", "visitedOutskirts", "visitedWhitePalace",
        "visitedCliffs", "visitedAbyssLower", "visitedGodhome", "visitedMines10",
        "openedTramLower", "openedTramRestingGrounds", "eggTempleVisited", "enteredDreamWorld",
        "enteredGGAtrium", "seenGGWastes", "scenesEncounteredDreamPlantC", "unlockedCompletionRate",
        "seenColosseumTitle", "colosseumBronzeOpened", "colosseumBronzeCompleted", "colosseumSilverOpened",
        "colosseumSilverCompleted", "colosseumGoldOpened", "colosseumGoldCompleted",
        "unlockedBossScenes", "bossDoorEntranceTextSeen"
    ];

    /// <summary>
    /// NPC dialogues, encounters and story events.
    /// </summary>
    public static readonly string[] StoryFields = [
        "elderbug", "metElderbug", "elderbugReintro", "elderbugHistory", "elderbugHistory1", "elderbugHistory2",
        "elderbugHistory3", "elderbugSpeechSly", "elderbugSpeechStation", "elderbugSpeechEggTemple",
        "elderbugSpeechMapShop", "elderbugSpeechBretta", "elderbugSpeechJiji", "elderbugSpeechMinesLift",
        "elderbugSpeechKingsPass", "elderbugSpeechInfectedCrossroads", "elderbugSpeechFinalBossDoor",
        "elderbugRequestedFlower", "elderbugGaveFlower", "elderbugFirstCall",
        "metRelicDealer", "metRelicDealerShop", "marmOutsideConvo", "marmConvo1", "marmConvo2", "marmConvo3",
        "marmConvoNailsmith",
        "metCornifer", "corniferIntroduced", "metIselda", "iseldaCorniferHomeConvo", "iseldaConvo1",
        "metSlyShop", "slyConvoNailArt", "slyConvoMapper", "slyConvoNailHoned",
        "jijiMet", "jijiShadeOffered", "jijiShadeCharmConvo",
        "metJinn", "jinnConvo1", "jinnConvo2", "jinnConvo3", "jinnConvoKingBrand", "jinnConvoShadeCharm",
        "zotePrecept", "zoteTownConvo",
        "shaman", "shamanScreamConvo", "shamanQuakeConvo", "shamanFireball2Convo", "shamanScream2Convo",
        "shamanQuake2Convo",
        "metMiner", "minerEarly", "hornetFountainEncounter",
        "metMoth", "ignoredMoth", "mothDeparted", "completedRGDreamPlant", "dreamReward1", "dreamReward2",
        "dreamReward3", "dreamReward4", "dreamReward5", "dreamReward5b", "dreamReward6", "dreamReward7",
        "dreamReward8", "dreamReward9", "dreamMothConvo1", "dreamNailConvo",
        "metGiraffe", "metCharmSlug", "salubraConvoCombo", "salubraConvoOvercharm", "salubraConvoTruth",
        "metNailmasterMato", "metNailmasterSheo", "metNailmasterOro", "matoConvoSheo", "matoConvoOro",
        "matoConvoSly", "sheoConvoMato", "sheoConvoOro", "sheoConvoSly", "sheoConvoNailsmith", "oroConvoSheo",
        "oroConvoMato", "oroConvoSly",
        "metHunter", "hunterRewardOffered", "huntersMarkOffered", "seenJournalMsg", "seenHunterMsg", "fillJournal",
        "tukMet", "metEmilitia", "emilitiaKingsBrandConvo", "metCloth",
        "bigCatHitTail", "bigCatHitTailConvo", "bigCatMeet", "bigCatTalk1", "bigCatTalk2", "bigCatTalk3",
        "bigCatKingsBrandConvo", "bigCatShadeConvo",
        "maskmakerMet", "maskmakerConvo1", "maskmakerConvo2", "maskmakerUnmasked1", "maskmakerUnmasked2",
        "maskmakerShadowDash", "maskmakerKingsBrand",
        "dungDefenderConvo1", "dungDefenderConvo2", "dungDefenderConvo3", "dungDefenderCharmConvo",
        "dungDefenderIsmaConvo", "dungDefenderAwakeConvo",
        "midwifeMet", "midwifeConvo1", "midwifeConvo2",
        "metQueen", "queenTalk1", "queenTalk2", "queenDung1", "queenDung2", "queenHornet", "queenTalkExtra",
        "queenConvo_grimm1", "queenConvo_grimm2",
        "metXun", "xunFailedConvo1", "xunFailedConvo2", "xunRewardGiven",
        "metStag", "stagConvoTram", "stagConvoTiso", "stagRemember1", "stagRemember2", "stagRemember3",
        "stagEggInspected", "stagHopeConvo", "littleFoolMet", "ranAway", "spiderCapture",
        "XERO_encountered", "ALADAR_encountered", "HU_encountered", "MUMCAT_encountered", "NOEYES_encountered",
        "MARKOTH_encountered", "GALIEN_encountered",
        "elderbugConvoGrimm", "slyConvoGrimm", "iseldaConvoGrimm", "midwifeWeaverlingConvo", "metBrum",
        "nymmSpoken", "nymmCharmConvo", "nymmFinalConvo", "elderbugNymmConvo", "slyNymmConvo", "iseldaNymmConvo",
        "nymmMissedEggOpen", "elderbugTroupeLeftConvo", "elderbugBrettaLeft", "jijiGrimmConvo",
        "metDivine", "divineFinalConvo",
        "scaredFlukeHermitEncountered", "scaredFlukeHermitReturned", "godseekerSpokenAwake",
        "godseekerWaterwaysSpoken1", "godseekerWaterwaysSpoken2", "godseekerWaterwaysSpoken3"
    ];

    /// <summary>
    /// Dream warriors (Xero, Gorb, Elder Hu, Marmu, No Eyes, Markoth, Galien): 0 = not defeated, 1 = defeated (the
    /// wounded ghost waits to be dream nailed), 2 = essence collected (the ghost is gone). The fight runs on the scene
    /// host, but 2 is set by the local ghost NPC of whoever dream nails it, so it must not depend on being scene
    /// host; otherwise the defeated warrior came back for everybody else.
    /// </summary>
    public static readonly string[] DreamWarriorFields = [
        "xeroDefeated", "aladarSlugDefeated", "elderHuDefeated", "mumCaterpillarDefeated", "noEyesDefeated",
        "markothDefeated", "galienDefeated"
    ];

    /// <summary>
    /// Dream bosses defeated inside their dream (Failed Champion, Soul Tyrant, Lost Kin, White Defender, Grey Prince
    /// Zote) and the map pins of the dream warriors. Set by whoever is in the dream / near the ghost, which is not
    /// necessarily the scene host of that scene name, so they must not depend on being scene host either.
    /// </summary>
    public static readonly string[] DreamBossFields = [
        "falseKnightDreamDefeated", "mageLordDreamDefeated", "infectedKnightDreamDefeated", "whiteDefenderDefeated",
        "greyPrinceDefeated",
        "xeroPinned", "aladarPinned", "huPinned", "mumCaterpillarPinned", "noEyesPinned", "markothPinned",
        "galienPinned"
    ];

    /// <summary>
    /// Every PlayerData field that the upstream mapping stored per player and that is now shared progress
    /// (<c>SyncType.Server</c>), plus the dream boss progress that was shared but only accepted from the scene host.
    /// Forced to Server (and not requiring the scene host) by <see cref="SaveDataMapping"/> on load.
    /// </summary>
    public static readonly HashSet<string> SharedProgressFields = [
        ..MaskFields, ..SoulVesselFields, ..SpellFields, ..AbilityFields, ..KeyItemFields, ..MapAndZoneFields,
        ..StoryFields, ..DreamWarriorFields, ..DreamBossFields
    ];

    /// <summary>
    /// Shared booleans that never go back to false in a playthrough (a defeated dream boss stays defeated). The
    /// server ignores (and corrects) an update that would turn them off, e.g. from a player whose data is out of
    /// date; otherwise the boss would show up again.
    /// </summary>
    public static readonly HashSet<string> MonotonicBoolFields = [
        "falseKnightDreamDefeated", "mageLordDreamDefeated", "infectedKnightDreamDefeated", "whiteDefenderDefeated",
        "greyPrinceDefeated"
    ];

    /// <summary>
    /// Shared integer fields that may be changed by several players at once and are therefore networked as a
    /// delta (<c>Additive</c>).
    /// </summary>
    public static readonly HashSet<string> AdditiveSharedFields = ["fountainGeo"];

    /// <summary>
    /// Shared integer fields that never decrease in a playthrough. The server ignores (and corrects) an update
    /// that would lower them, e.g. from a player whose data is out of date.
    /// </summary>
    public static readonly HashSet<string> MonotonicIntFields = [
        "maxHealthBase", "MPReserveMax", "fireballLevel", "quakeLevel", "screamLevel", ..DreamWarriorFields
    ];

    /// <summary>
    /// Shared booleans that can legitimately go back to false (a key that is used up). When merging, the value
    /// already stored on the server wins instead of OR.
    /// </summary>
    public static readonly HashSet<string> ConsumableFlags = [
        "hasSlykey", "hasWhiteKey", "hasLoveKey", "hasCityKey", "hasSpaKey", "hasWaterwaysKey"
    ];

    /// <summary>
    /// Persistent bool items (scene pickups) that the upstream mapping stored per player and that are now shared,
    /// because what they give is shared: mask shards, vessel fragments, spells, abilities and key items. Key is
    /// "id@scene". The grub reward shard at Crossroads_38 (Grubfather) is not included on purpose: the grub rewards
    /// are handled with the grub synchronisation.
    /// </summary>
    public static readonly HashSet<string> SharedPickups = [
        "Heart Piece@Crossroads_13", "Heart Piece@Fungus2_01", "Heart Piece@Fungus2_25", "Heart Piece@Room_Bretta",
        "Heart Piece@Fungus1_36", "Heart Piece@Mines_32", "Heart Piece@Crossroads_09", "Heart Piece@Waterways_04b",
        "Heart Piece@Hive_04", "Heart Piece@Room_Mansion",
        "Vessel Fragment@Crossroads_37", "Vessel Fragment@Abyss_04", "Vessel Fragment@Fungus1_13",
        "Vessel Fragment@Ruins2_09",
        // Vengeful Spirit, Mothwing Cloak, Mantis Claw, Isma's Tear, King's Brand, Shade Cloak
        "Shiny Item@Crossroads_ShamanTemple", "Shiny Item@Fungus1_04", "Shiny Item Stand@Fungus2_14",
        "Shiny Item Acid@Waterways_13", "Shiny Item Stand@Room_Wyrm", "Shiny Item@Abyss_10",
        "Shiny Item Godfinder@GG_Waterways", "Shiny Item HunterMark@Fungus1_08"
    ];

    /// <summary>
    /// Merge one stored value of a shared field with the value of the server: OR for flags (server value wins for
    /// consumable keys), max for integers, union for string lists. Anything else keeps the server value.
    /// </summary>
    /// <param name="name">The PlayerData field name.</param>
    /// <param name="serverValue">The value stored on the server, null if there is none.</param>
    /// <param name="playerValue">The value stored for the player.</param>
    /// <returns>The merged value.</returns>
    public static object MergeValue(string name, object serverValue, object playerValue) {
        if (serverValue == null) {
            return playerValue;
        }

        if (playerValue == null) {
            return serverValue;
        }

        switch (serverValue) {
            case bool serverBool when playerValue is bool playerBool:
                return ConsumableFlags.Contains(name) ? serverBool : serverBool || playerBool;
            case int serverInt when playerValue is int playerInt:
                return System.Math.Max(serverInt, playerInt);
            case List<string> serverList when playerValue is List<string> playerList:
                return serverList.Concat(playerList.Where(s => !serverList.Contains(s))).ToList();
            default:
                return serverValue;
        }
    }

    /// <summary>
    /// Total mask shards that a (maxHealthBase, heartPieces) pair represents.
    /// </summary>
    public static int MaskShardTotal(int maxHealthBase, int heartPieces) => maxHealthBase * ShardsPerMask + heartPieces;

    /// <summary>
    /// Total vessel fragments that a (MPReserveMax, vesselFragments) pair represents.
    /// </summary>
    public static int VesselFragmentTotal(int mpReserveMax, int vesselFragments) =>
        mpReserveMax / SoulPerVessel * FragmentsPerVessel + vesselFragments;

    /// <summary>
    /// Merge two (max, pieces) pairs as one unit: the pair with the most total progress wins (ties keep the
    /// server pair), so masks and shards (or vessels and fragments) never mix into a state no player had.
    /// </summary>
    public static (int Max, int Pieces) MergePair(
        (int Max, int Pieces) server,
        (int Max, int Pieces) player,
        Func<int, int, int> total
    ) {
        return total(player.Max, player.Pieces) > total(server.Max, server.Pieces) ? player : server;
    }

    /// <summary>
    /// Whether an incoming value for a shared field would lower a value that never decreases.
    /// </summary>
    public static bool IsRegression(string name, int storedValue, int incomingValue) =>
        MonotonicIntFields.Contains(name) && incomingValue < storedValue;

    /// <summary>
    /// Whether an incoming value for a shared flag would turn off a flag that never goes back to false.
    /// </summary>
    public static bool IsRegression(string name, bool storedValue, bool incomingValue) =>
        MonotonicBoolFields.Contains(name) && storedValue && !incomingValue;

    /// <summary>
    /// The health of the local character after its max health changed because another player obtained (or the
    /// server corrected) a mask: if the character was at full health it stays at full health with the new
    /// maximum, otherwise its current health is kept (clamped to the new maximum). Unlike picking up a mask
    /// yourself, receiving one does not heal the damage you already took.
    /// </summary>
    public static int HealthAfterMaxHealthChange(int health, int oldMaxHealth, int newMaxHealth) {
        if (health <= 0) {
            return health;
        }

        if (health >= oldMaxHealth) {
            return newMaxHealth;
        }

        return System.Math.Min(health, newMaxHealth);
    }

    /// <summary>
    /// The reserve soul of the local character after MPReserveMax changed: kept, clamped to the new maximum.
    /// </summary>
    public static int ReserveAfterMaxChange(int reserve, int newReserveMax) =>
        System.Math.Max(0, System.Math.Min(reserve, newReserveMax));
}
