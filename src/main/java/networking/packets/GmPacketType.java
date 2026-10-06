package networking.packets;

import java.util.Arrays;

/**
 * Client-side packet ids, decoded from the shipped assembly's {@code GmPacketType} enum.
 *
 * <p>The id is the enum's ordinal, which is why the order below matters and why entries must
 * never be reordered. The enum is shared by both directions: the client and the game server use
 * the same id space, split by which class carries which id (for example {@code GmHello} is
 * client-to-server and {@code GmHelloResp} is server-to-client).
 *
 * <p>{@code Payload[0]} of a framed packet carries this id; the outer 4-byte big-endian length
 * includes that byte (see {@link networking.Relay}).
 */
public final class GmPacketType {

    private GmPacketType() {
    }

    private static final String[] NAMES = {
            "Unknown",                             // 0
            "Update",
            "CharInfo",
            "Register",
            "RegisterResp",
            "MapInfo",
            "Shoot",
            "Message",
            "Swap",
            "TakeItem",
            "Tiles",                               // 10
            "Chat",
            "Drop",
            "StartUpdate",
            "ChangeName",
            "Buy",
            "Quest",
            "Goto",
            "Death",
            "GcAuth",
            "GcSwitch",                            // 20
            "GcReq",
            "GcRes",
            "Tick",
            "TradeRequest",
            "TradeFinished",
            "TradeChanged",
            "TradeStart",
            "HelloResp",
            "LeaderboardResp",
            "GetLeaderboard",                      // 30
            "GetProducts",
            "ProductsResp",
            "VerifyIap",
            "PlayEffect",
            "GetServers",
            "Reconnect",
            "CreateGuild",
            "InviteGuild",
            "JoinGuild",
            "GetNews",                             // 40
            "NewsResp",
            "UpdateAssets",
            "AssetVersions",
            "GotoResp",
            "Revive",
            "ReviveResp",
            "GetDeaths",
            "DeathsResp",
            "VerifyIapResp",
            "CollectGold",                         // 50
            "DailyGold",
            "VerifyDroidIap",
            "UpdateGold",
            "Ad",
            "Hello",
            "ServerList",
            "LeaveGuild",
            "ChallengeUpdated",
            "Aoe",
            "UseItem",                             // 60
            "Create",
            "GuildListResp",
            "Messages",
            "ProjectilesAck",
            "AllyHit",
            "Escape",
            "UseItemAck",
            "GuildModify",
            "GuildTakeBanner",
            "HealthUpdate",                        // 70
            "Load",
            "Pong",
            "EnterPortal",
            "Move",
            "IosDeviceToken",
            "GuildMessage",
            "GuildModifyResp",
            "LinkEmail",
            "LinkEmailResp",
            "RecoverEmail",                        // 80
            "RecoverEmailResp",
            "TickAck",
            "UpdateAck",
            "ActivateObject",
            "Disconnect",
            "Projectiles",
            "EditEssence",
            "Chats",
            "SelectEssenceResp",
            "SwapAck",                             // 90
            "CreateResp",
            "Ping",
            "TradeItems",
            "UnlockedClass",
            "BiomeDisplay",
            "UpdateEssences",
            "DiscoverEssence",
            "GetGuildList",
            "ExchangeEssence",
            "ExchangeEssenceResp",                 // 100
            "ExchangeGift",
            "ProjHit",
            "ExchangeGiftAck",
            "Failure",
            "WorldDisplay",
            "CheckPing",
            "CheckPingAck",
            "SetMusic",
            "MapInfoAck",
            "SetGameTime",                         // 110
            "UpdateAttainmentSaveData",
            "MoveTowards",
            "MoveTowardsAck",
            "MoveTowardInterrupted",
            "MoveTowardFinished",
            "ClaimAttainmentReward",
            "ClaimAttainmentRewardResult",
            "UpdateDailyGold",
            "ActionTimer",
            "CancelActionTimer",                   // 120
            "CameraTarget",
            "ManipulateTarget",
            "ManipulateTargetAck",
            "PlaySfx",
            "WorldPopup",
            "UpdateEmotes",
            "UpdateSelectedEmotes",
            "UpdateSelectedGravestones",
            "AddLight",
            "RemoveLight",                         // 130
            "Shockwave",
            "StartReforge",
            "Reforge",
            "CloseReforge",
            "StartReforgeAck",
            "CloseReforgeAck",
            "SetZHeight",
            "UpdateInstanceGauges",
            "RemoveInstanceGauges",
            "RemoveCameraTarget",                  // 140
            "CameraTargetAck",
            "UpdateTether",
            "RemoveTether",
            "UpdateTitlesUnlocked",
            "SetTitle",
            "SetTitleAck",
            "BuyItem",
            "BuyItemAck",
            "BuyItemBatch",
            "BuyItemBatchAck",                     // 150
            "Attainment",
            "StartPurchase",
            "EndPurchase",
            "Reroll",
            "RerollAck",
            "SetRerollStatus",
            "Aoes",
            "EscapeAck",
            "AddFog",
            "RemoveFog",                           // 160
            "Aoes2",
            "Aoes2Ack",
            "Aoe2Expired",
            "AddContributionDisplay",
            "MarketBoardSearch",
            "MarketBoardSearchAck",
            "MarketBoardPurchase",
            "MarketBoardPurchaseAck",
            "MarketBoardEnlist",
            "MarketBoardEnlistAck",                // 170
            "MarketBoardEdit",
            "MarketBoardEditAck",
            "MarketBoardRemove",
            "MarketBoardRemoveAck",
            "MarketBoardStartEnlisting",
            "MarketBoardStartEnlistingAck",
            "MarketBoardEndEnlisting",
            "NpcShopOpen",
            "NpcShopData",
            "NpcShopPurchase",                     // 180
            "NpcShopPurchaseAck",
            "LootAnnounce",
            "EnemyAnnounce",
            "ForcedEscape",
            "Kicked",
            "GemStorageStart",
            "GemStorageStartAck",
            "GemStorageEnd",
            "GemStorageEndAck",
            "GemStorageUpgrade",                   // 190
            "GemStorageUpgradeAck",
            "GemStorageFill",
            "GemStorageUse",
            "GemStorageUpdate",
            "GemStorageUseAck",
            "GetVaultData",
            "GetVaultDataAck",
            "VaultDataChanged",
            "EquipCosmetic",
            "EquipCosmeticAck",                    // 200
            "UnequipCosmetic",
            "UnequipCosmeticAck",
            "RemoveQuest",
            "SetBossHealthBar",
            "SpeedrunState",
            "TrackAttainment",
            "UntrackAttainment",
            "UpdateTrackedAttainments",
            "ClaimAllAttainments",
            "ClaimAllAttainmentsAck",              // 210
            "GetTrialLeaderboard",
            "SetGravestones",
            "BuyGravestonePack",
            "BuyGravestonePackAck",
            "UpdatePurchasedGravestonePacks",
            "BuyIndividualGravestone",
            "BuyIndividualGravestoneAck",
            "UpdatePurchasedIndividualGravestones",
            "TrialLeaderboardResp",
            "GetScoreLeaderboard",                 // 220
            "ScoreLeaderboardResp",
            "AgreePlayerGuidelines",
            "PlayerGuidelinesStatus",
            "BuyShieldPack",
            "BuyShieldPackAck",
            "UpdatePurchasedShieldPacks",
            "BuyIndividualShield",
            "BuyIndividualShieldAck",
            "UpdatePurchasedIndividualShields",
            "SetShields",                          // 230
            "UpdateSelectedShields",
            "BuyDisplayShieldSlot",
            "BuyDisplayShieldSlotAck",
            "SetDisplayShield",
            "UpdateDisplayShieldConfig",
            "BuyBagPack",
            "BuyBagPackAck",
            "UpdatePurchasedBagPacks",
            "BuyIndividualBag",
            "BuyIndividualBagAck",                 // 240
            "UpdatePurchasedIndividualBags",
            "SetBags",
            "UpdateSelectedBags",
            "SetPetCustomization",
            "SetPetCustomizationAck",
            "PurchasePetSlotUnlock",
            "PurchasePetSlotUnlockAck",
            "SetFeaturedPet",
            "SetFeaturedPetAck",
            "GetSeededRunNames",                   // 250
            "GetSeededRunNamesResp",
            "ContributionRank",
            "PostChannelInvisDuration",
            "ProcCooldown",
            "MarketBoardPriceHistory",
            "MarketBoardPriceHistoryAck",
            "MarketBoardRecentSales",
            "MarketBoardRecentSalesAck",
            "AssetsChanged",
            "PartyUpdate",                         // 260
            "PartyJoinPrompt",
            "PartyFinderSearch",
            "PartyFinderSearchAck",
            "PartyFinderPost",
            "PartyFinderJoin",
            "PartyFinderClose",
            "Lasers",
            "LasersAck",
            "LaserHit",
            "LasersEnd",                           // 270
            "LaserRetarget",
            "PlayerLaser",
            "AllyLasers",
            "PlayerLaserHit",
            "Sweeps",
            "SweepsAck",
            "SweepHit",
            "PlayerSweep",
            "AllySweeps",
            "PlayerSweepHits",                     // 280
            "WaveState",
            "ArenaJoinPrompt",
            "PurchaseCharacterSlot",
            "PurchaseCharacterSlotAck",
            "DeleteCharacter",
            "DeleteCharacterAck",
            "RemoveTotems",
            "RemoveTotemsAck",
            "ImpermanenceNotice",
            "EscapeCastState",                     // 290
            "SafeAreaState",
            "DebugPoints",
            "DmSync",
            "DmConversations",
            "DmSend",
            "DmSendAck",
            "DmMessage",
            "DmHistoryRequest",
            "DmHistory",
            "DmMarkRead",                          // 300
            "DmClaim",
            "DmAttachmentState",
            "DmResolve",
            "DmResolveAck",
            "DmHide",
            "DmBlock",
            "FriendAction",
            "FriendList",
            "FriendUpdate",
            "FriendActionAck",                     // 310
            "RenderDistance",
            "MusicCue",
            "KnockUp",
            "KnockUpAck",
            "VaultMove",
            "VaultMoveAck",
            "VaultAction",
            "VaultActionAck",
            "DmRetract",
            "JumpScare"                            // 320
    };

    /** @return the packet name for an id, or {@code Unknown(<id>)} when out of range. */
    public static String name(int id) {
        if (id < 0 || id >= NAMES.length) {
            return "Unknown(" + id + ")";
        }
        return NAMES[id];
    }

    /** @return the id for a packet name, or -1 when the name is not known. */
    public static int id(String name) {
        for (int i = 0; i < NAMES.length; i++) {
            if (NAMES[i].equals(name)) {
                return i;
            }
        }
        return -1;
    }

    public static int count() {
        return NAMES.length;
    }

    /** Ids the queue service uses instead; it has its own enum (see {@link QPacketType}). */
    public static final int HELLO = 55;
    public static final int HELLO_RESP = 28;
    public static final int MAP_INFO = 5;
    public static final int MAP_INFO_ACK = 109;
    public static final int HEALTH_UPDATE = 70;
    public static final int ESCAPE = 66;
    public static final int ESCAPE_ACK = 158;
    public static final int ESCAPE_CAST_STATE = 290;
    public static final int SAFE_AREA_STATE = 291;
    public static final int FORCED_ESCAPE = 184;
    public static final int KICKED = 185;
    public static final int RECONNECT = 36;
    public static final int PING = 92;
    public static final int PONG = 72;
    public static final int MOVE = 74;
    public static final int SHOOT = 6;

    static String[] names() {
        return Arrays.copyOf(NAMES, NAMES.length);
    }
}
