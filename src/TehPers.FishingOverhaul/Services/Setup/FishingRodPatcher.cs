using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Netcode;
using Ninject;
using Ninject.Activation;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;
using StardewValley.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using TehPers.Core.Api.Extensions;
using TehPers.Core.Api.Items;
using TehPers.Core.Api.Setup;
using TehPers.FishingOverhaul.Api;
using TehPers.FishingOverhaul.Api.Content;
using TehPers.FishingOverhaul.Api.Events;
using TehPers.FishingOverhaul.Api.Extensions;
using TehPers.FishingOverhaul.Config;
using TehPers.FishingOverhaul.Extensions;
using TehPers.FishingOverhaul.Extensions.Drawing;
using TehPers.FishingOverhaul.Services;
using SObject = StardewValley.Object;

namespace TehPers.FishingOverhaul.Services.Setup
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal class FishingRodPatcher : Patcher, ISetup
    {
        private static FishingRodPatcher? Instance { get; set; }

        private readonly IModHelper helper;
        private readonly IMonitor monitor;
        private readonly FishingTracker fishingTracker;
        private readonly FishingApi fishingApi;
        private readonly ICustomBobberBarFactory customBobberBarFactory;
        private readonly FishConfig fishConfig;
        private readonly INamespaceRegistry namespaceRegistry;

        private readonly IReflectedField<Multiplayer> game1MultiplayerField;
        private readonly Queue<Action> postUpdateActions;

        private FishingRodPatcher(
            IModHelper helper,
            IMonitor monitor,
            Harmony harmony,
            FishingTracker fishingTracker,
            FishingApi fishingApi,
            ICustomBobberBarFactory customBobberBarFactory,
            FishConfig fishConfig,
            INamespaceRegistry namespaceRegistry
        )
            : base(harmony)
        {
            this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
            this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            this.fishingTracker = fishingTracker ?? throw new ArgumentNullException(nameof(fishingTracker));
            this.fishingApi = fishingApi ?? throw new ArgumentNullException(nameof(fishingApi));
            this.customBobberBarFactory = customBobberBarFactory ?? throw new ArgumentNullException(nameof(customBobberBarFactory));
            this.fishConfig = fishConfig ?? throw new ArgumentNullException(nameof(fishConfig));
            this.namespaceRegistry = namespaceRegistry ?? throw new ArgumentNullException(nameof(namespaceRegistry));

            this.game1MultiplayerField = helper.Reflection.GetField<Multiplayer>(typeof(Game1), "multiplayer");
            this.postUpdateActions = new();
        }

        public static FishingRodPatcher Create(IContext context)
        {
            FishingRodPatcher.Instance ??= new(
                context.Kernel.Get<IModHelper>(),
                context.Kernel.Get<IMonitor>(),
                context.Kernel.Get<Harmony>(),
                context.Kernel.Get<FishingTracker>(),
                context.Kernel.Get<FishingApi>(),
                context.Kernel.Get<ICustomBobberBarFactory>(),
                context.Kernel.Get<FishConfig>(),
                context.Kernel.Get<INamespaceRegistry>()
            );
            return FishingRodPatcher.Instance;
        }

        public override void Setup()
        {
            FrenzyQuery.Register();
            FrenzyQuery.Configuration = this.fishConfig;

            this.Patch(
                AccessTools.Method(typeof(FishingRod), nameof(FishingRod.tickUpdate)),
                prefix: new(AccessTools.Method(typeof(FishingRodPatcher), nameof(FishingRodPatcher.TickUpdate_Prefix))),
                postfix: new(AccessTools.Method(typeof(FishingRodPatcher), nameof(FishingRodPatcher.TickUpdate_Postfix)))
            );
            this.Patch(
                AccessTools.Method(typeof(FishingRod), nameof(FishingRod.DoFunction)),
                prefix: new(AccessTools.Method(typeof(FishingRodPatcher), nameof(FishingRodPatcher.DoFunction_Prefix)))
            );
            this.Patch(
                AccessTools.Method(typeof(FishingRod), nameof(FishingRod.draw)),
                prefix: new(AccessTools.Method(typeof(FishingRodPatcher), nameof(FishingRodPatcher.Draw_Prefix)))
            );
        }

        private void StartFishingMinigame(
            FishingInfo fishingInfo,
            Item fishItem,
            FishingRod rod,
            FishEntry fishEntry,
            bool fromFishPond
        )
        {
            this.fishingTracker.ActiveFisherData[fishingInfo.User] = new(
                rod,
                new FishingState.Fishing(fishingInfo, fishEntry.FishKey)
            );

            var beginReelingEvent = this.helper.Reflection.GetField<NetEvent0>(rod, "beginReelingEvent").GetValue();
            beginReelingEvent.Fire();
            rod.isReeling = true;
            rod.hit = false;

            switch (fishingInfo.User.FacingDirection)
            {
                case 1:
                    fishingInfo.User.FarmerSprite.setCurrentSingleFrame(48);
                    break;
                case 3:
                    fishingInfo.User.FarmerSprite.setCurrentSingleFrame(48, flip: true);
                    break;
            }

            // --- 1.6 BAIT DETECTION ---
            var bait = rod.GetBait();
            var baitId = bait?.QualifiedItemId;
            var isChallengeBait = baitId == "(O)ChallengeBait";
            var isDeluxeBait = baitId == "(O)DeluxeBait";

            // Deluxe Bait Effect: +2 Fishing Level (Increases Bar Size)
            var effectiveFishingInfo = isDeluxeBait
                ? fishingInfo with { FishingLevel = fishingInfo.FishingLevel + 2 }
                : fishingInfo;

            var sizeDepthFactor = 1f * (effectiveFishingInfo.BobberDepth / 5f);
            var sizeLevelFactor = 1 + effectiveFishingInfo.FishingLevel / 2;
            var sizeFactor = sizeDepthFactor * Game1.random.Next(sizeLevelFactor, Math.Max(6, sizeLevelFactor)) / 5f;
            if (rod.favBait)
            {
                sizeFactor *= 1.2f;
            }

            var fishSizePercent = Math.Clamp(sizeFactor * (1.0f + Game1.random.Next(-10, 11) / 100.0f), 0.0f, 1.0f);
            var treasure = !Game1.isFestival() && fishingInfo.User.fishCaught?.Count() > 1 && Game1.random.NextDouble() < this.fishingApi.GetChanceForTreasure(effectiveFishingInfo);

            // Crash Fix: Filter null tackle using Where clause
            var tackleIds = new List<string>();
            foreach (var tackle in rod.GetTackle())
            {
                if (tackle != null)
                {
                    var id = tackle.ItemId;
                    var qId = tackle.QualifiedItemId;

                    if (!string.IsNullOrEmpty(id))
                    {
                        tackleIds.Add(id);
                    }

                    if (!string.IsNullOrEmpty(qId) && qId != id)
                    {
                        tackleIds.Add(qId);
                    }

                    if (id.StartsWith("(O)"))
                    {
                        tackleIds.Add(id[3..]);
                    }
                }
            }

            var customBobber = this.customBobberBarFactory.Create(
                effectiveFishingInfo,
                fishEntry,
                fishItem,
                fishSizePercent,
                treasure,
                tackleIds,
                fromFishPond
            );

            if (customBobber is null)
            {
                this.monitor.Log("Error creating fishing minigame GUI.", LogLevel.Error);
                Game1.showGlobalMessage($"Error starting fishing minigame for {fishEntry.FishKey}.");
                fishingInfo.User.Halt();
                fishingInfo.User.completelyStopAnimatingOrDoingAction();
                fishingInfo.User.armOffset = Vector2.Zero;
                rod.castedButBobberStillInAir = false;
                rod.fishCaught = false;
                rod.isReeling = false;
                rod.isFishing = false;
                rod.pullingOutOfWater = false;
                fishingInfo.User.canReleaseTool = false;
                return;
            }

            var initialStreak = this.fishingApi.GetStreak(fishingInfo.User);

            customBobber.LostFish += (_, _) =>
            {
                if (this.fishConfig.GetQualityIncrease(initialStreak) > 0)
                {
                    Game1.showGlobalMessage(this.helper.Translation.Get("text.streak.lost", new { streak = initialStreak }));
                }
                this.fishingApi.SetStreak(fishingInfo.User, 0);
            };

            customBobber.StateChanged += (_, state) =>
            {
                if (!state.IsPerfect && state.Treasure == TreasureState.NotCaught)
                {
                    if (this.fishConfig.GetQualityIncrease(initialStreak) > 0)
                    {
                        Game1.showGlobalMessage(this.helper.Translation.Get("text.streak.warning", new { streak = initialStreak }));
                    }
                }
            };

            customBobber.CatchFish += (_, info) =>
            {
                // --- CHALLENGE BAIT LOGIC ---
                if (isChallengeBait && info is CatchInfo.FishCatch fishCatch)
                {
                    // Perfect = 3 fish
                    // Not Perfect = 1 fish
                    var amount = info.State.IsPerfect ? 3 : 1;
                    info = fishCatch with { NumberOfFishCaught = amount };
                }

                switch (info.State)
                {
                    case (true, _): // Perfect
                        this.fishingApi.SetStreak(fishingInfo.User, initialStreak + 1);
                        info = info with { FishQuality = info.FishQuality + this.fishConfig.GetQualityIncrease(initialStreak + 1) + 1 };
                        break;
                    case (false, TreasureState.Caught): // Restored
                        if (this.fishConfig.GetQualityIncrease(initialStreak) > 0)
                        {
                            Game1.showGlobalMessage(this.helper.Translation.Get("text.streak.restored", new { streak = initialStreak }));
                        }
                        info = info with { FishQuality = info.FishQuality + this.fishConfig.GetQualityIncrease(initialStreak) };
                        break;
                    default: // Normal
                        if (this.fishConfig.GetQualityIncrease(initialStreak) > 0)
                        {
                            Game1.showGlobalMessage(this.helper.Translation.Get("text.streak.lost", new { streak = initialStreak }));
                        }
                        this.fishingApi.SetStreak(fishingInfo.User, 0);
                        break;
                }

                info = this.fishConfig.ClampQuality(info);
                this.CatchItem(rod, info);
            };

            Game1.activeClickableMenu = customBobber;
        }

        private void CatchItem(FishingRod rod, CatchInfo info)
        {
            var (fishingInfo, item, fromFishPond) = info;
            var newState = new FishingState.Caught(fishingInfo, info);
            this.fishingTracker.ActiveFisherData[fishingInfo.User] = new(rod, newState);

            if (item == null)
            {
                this.monitor.Log("CatchItem called with null item. Defaulting to Trash.", LogLevel.Error);
                item = ItemRegistry.Create("(O)168", 1);
            }

            var itemId = item.QualifiedItemId ?? "(O)168";

            try
            {
                var field = this.helper.Reflection.GetField<NetString>(rod, "whichFish");
                if (field != null)
                {
                    var netString = field.GetValue();
                    if (netString != null)
                    {
                        netString.Value = itemId;
                    }
                }
            }
            catch (Exception ex)
            {
                this.monitor.LogOnce($"Failed to set whichFish (harmless if fishing works): {ex.Message}", LogLevel.Trace);
            }

            if (info is CatchInfo.FishCatch(_, _, _, var fishSize, var isLegendary, var fishQuality, var fishDifficulty, var (isPerfect, treasureState), _, var numberOfFishCaught))
            {
                if (item is SObject obj)
                {
                    obj.Quality = fishQuality;
                    obj.Stack = numberOfFishCaught;
                }

                var wasTreasureCaught = treasureState is TreasureState.Caught;
                rod.treasureCaught = wasTreasureCaught;
                rod.fishSize = fishSize;
                rod.fishQuality = Math.Max(fishQuality, 0);
                rod.fromFishPond = fromFishPond;
                rod.numberOfFishCaught = numberOfFishCaught;

                // --- RECATCHABLE LEGENDARIES DATE ---
                if (isLegendary && !fromFishPond && fishingInfo.User.IsLocalPlayer)
                {
                    // Save the current day (DaysPlayed) for this specific fish
                    fishingInfo.User.modData[$"Hiztaar.FishingOverhaulRevived/LastCaught_{itemId}"] = Game1.stats.DaysPlayed.ToString();
                }

                if (!Game1.isFestival() && fishingInfo.User.IsLocalPlayer && !fromFishPond)
                {
                    rod.bossFish = isLegendary;
                    var experience = Math.Max(1, (fishQuality + 1) * 3 + fishDifficulty / 3)
                        * (wasTreasureCaught ? 2.2 : 1)
                        * (isPerfect ? 2.4 : 1)
                        * (rod.bossFish ? 5.0 : 1);
                    fishingInfo.User.gainExperience(1, (int)experience);
                }
            }
            else
            {
                rod.treasureCaught = false;
                rod.fishSize = -1;
                rod.fishQuality = -1;
                rod.fromFishPond = fromFishPond;
                rod.numberOfFishCaught = 1;
            }

            this.fishingApi.OnCaughtItem(new(info));
            var onCatch = info switch
            {
                CatchInfo.FishCatch c => c.FishEntry.OnCatch,
                CatchInfo.TrashCatch c => c.TrashEntry.OnCatch,
                _ => throw new InvalidOperationException($"Unknown catch type {info}"),
            };
            onCatch?.OnCatch(this.fishingApi, info);

            var itemData = ItemRegistry.GetData(itemId);
            var textureName = itemData?.GetTextureName() ?? "Maps\\springobjects";
            var sourceRect = itemData?.GetSourceRect() ?? new Rectangle(0, 0, 16, 16);

            float animationInterval;
            if (fishingInfo.User.FacingDirection is 1 or 3)
            {
                var distToBobber = Vector2.Distance(rod.bobber.Value, fishingInfo.User.Position);
                const float y1 = 1f / 1000f;
                var num6 = 128.0f - (fishingInfo.User.Position.Y - rod.bobber.Y + 10.0f);
                const double a1 = 4.0 * Math.PI / 11.0;
                var f1 = (float)(distToBobber * y1 * Math.Tan(a1) / Math.Sqrt(2.0 * distToBobber * y1 * Math.Tan(a1) - 2.0 * y1 * num6));
                if (float.IsNaN(f1))
                {
                    f1 = 0.6f;
                }

                var num7 = f1 * (float)(1.0 / Math.Tan(a1));
                animationInterval = distToBobber / num7;

                rod.animations.Add(new TemporaryAnimatedSprite(textureName, sourceRect, animationInterval, 1, 0, rod.bobber.Value, false, false)
                {
                    layerDepth = rod.bobber.Y / 10000f,
                    alphaFade = 0.0f,
                    color = Color.White,
                    scale = 4f,
                    scaleChange = 0.0f,
                    rotation = 0.0f,
                    motion = new((fishingInfo.User.FacingDirection == 3 ? -1f : 1f) * -num7, -f1),
                    acceleration = new(0.0f, y1),
                    timeBasedMotion = true,
                    endFunction = _ => this.FinishFishing(fishingInfo.User, rod, info),
                    endSound = "tinyWhip"
                });
            }
            else
            {
                var num11 = rod.bobber.Y - (fishingInfo.User.StandingPixel.Y - 64);
                var num12 = Math.Abs((float)(num11 + 256.0 + 32.0));
                if (fishingInfo.User.FacingDirection == 0)
                {
                    num12 += 96f;
                }

                const float y3 = 3f / 1000f;
                var num13 = (float)Math.Sqrt(2.0 * y3 * num12);
                animationInterval = (float)(Math.Sqrt(2.0 * (num12 - (double)num11) / y3) + num13 / (double)y3);
                var x1 = 0.0f;
                if (animationInterval != 0.0)
                {
                    x1 = (fishingInfo.User.Position.X - rod.bobber.X) / animationInterval;
                }

                rod.animations.Add(new TemporaryAnimatedSprite(textureName, sourceRect, animationInterval, 1, 0, new(rod.bobber.X, rod.bobber.Y), false, false)
                {
                    layerDepth = rod.bobber.Y / 10000f,
                    alphaFade = 0.0f,
                    color = Color.White,
                    scale = 4f,
                    scaleChange = 0.0f,
                    rotation = 0.0f,
                    motion = new(x1, -num13),
                    acceleration = new(0.0f, y3),
                    timeBasedMotion = true,
                    endFunction = _ => this.FinishFishing(fishingInfo.User, rod, info),
                    endSound = "tinyWhip"
                });
            }

            if (fishingInfo.User.IsLocalPlayer)
            {
                fishingInfo.User.currentLocation.playSound("pullItemFromWater");
                fishingInfo.User.currentLocation.playSound("dwop");
            }

            rod.castedButBobberStillInAir = false;
            rod.pullingOutOfWater = true;
            rod.isFishing = false;
            rod.isReeling = false;
            fishingInfo.User.FarmerSprite.PauseForSingleAnimation = false;
            var animation = fishingInfo.User.FacingDirection switch
            {
                0 => 299,
                1 => 300,
                2 => 301,
                3 => 302,
                _ => 299,
            };
            fishingInfo.User.FarmerSprite.animateBackwardsOnce(animation, animationInterval);
        }

        private void FinishFishing(Farmer user, FishingRod rod, CatchInfo info)
        {
            user.Halt();
            user.armOffset = Vector2.Zero;
            rod.castedButBobberStillInAir = false;
            rod.isReeling = false;
            rod.isFishing = false;
            rod.pullingOutOfWater = false;
            user.canReleaseTool = false;

            var fishCaught = this.helper.Reflection.GetField<bool>(rod, "fishCaught");
            fishCaught.SetValue(false);
            this.postUpdateActions.Enqueue(() => fishCaught.SetValue(true));

            if (this.fishingTracker.ActiveFisherData.TryGetValue(user, out var fisherData) && fisherData.State is FishingState.Caught(var fishingInfo, var catchInfo))
            {
                this.fishingTracker.ActiveFisherData[user] = new(rod, new FishingState.Holding(fishingInfo, catchInfo));
            }

            if (!user.IsLocalPlayer)
            {
                return;
            }

            string? itemId = null;
            var fishSize = -1;
            var fromFishPond = false;
            var stack = 1;
            var hasData = false;

            if (info is CatchInfo.FishCatch fishCatch && fishCatch.Item is SObject fishObj)
            {
                itemId = fishObj.ItemId;
                fishSize = fishCatch.FishSize;
                fromFishPond = fishCatch.FromFishPond;
                stack = fishObj.Stack;
                hasData = true;
            }
            else if (info is CatchInfo.TrashCatch trashCatch && trashCatch.Item is SObject trashObj)
            {
                itemId = trashObj.ItemId;
                fishSize = 0;
                fromFishPond = trashCatch.FromFishPond;
                stack = trashObj.Stack;
                hasData = true;
            }

            if (!Game1.isFestival())
            {
                if (hasData && itemId != null)
                {
                    rod.recordSize = user.caughtFish(itemId, fishSize, fromFishPond, stack);

                    // --- FIX: TROUT DERBY GOLDEN TAGS ---
                    // Rainbow Trout ID is "138". Check for unqualified form only since ItemId
                    // strips the "(O)" prefix. Summer 20 & 21 at Cindersap Forest.
                    if ((itemId == "138" || itemId == "(O)138") && !fromFishPond && user.currentLocation is StardewValley.Locations.Forest)
                    {
                        if (Game1.currentSeason == "summer" && (Game1.dayOfMonth == 20 || Game1.dayOfMonth == 21))
                        {
                            // Loop for each fish caught (e.g. 3 times if Challenge Bait was perfect)
                            for (var i = 0; i < stack; i++)
                            {
                                if (Game1.random.NextDouble() < 0.33)
                                {
                                    var tag = ItemRegistry.Create("(O)TroutDerbyTag", 1);
                                    if (user.addItemToInventoryBool(tag))
                                    {
                                        user.currentLocation.playSound("coin");
                                        Game1.addHUDMessage(new HUDMessage("Caught a Golden Tag!", 1));
                                    }
                                    else
                                    {
                                        Game1.createItemDebris(tag, user.Position, 2);
                                    }
                                }
                            }
                        }
                    }
                    // ------------------------------------
                }
                user.faceDirection(2);
            }
            else if (user.currentLocation.currentEvent is { } currentEvent)
            {
                if (hasData && itemId != null)
                {
                    currentEvent.caughtFish(itemId, fishSize, user);
                }
                rod.fishCaught = false;
                rod.doneFishing(user);
            }

            // Fixed CS8602: added null check for info.Item
            if (info is CatchInfo.FishCatch { IsLegendary: true } && info.Item != null)
            {
                Game1.showGlobalMessage(Game1.content.LoadString(@"Strings\StringsFromCSFiles:FishingRod.cs.14068"));
                this.game1MultiplayerField.GetValue().globalChatInfoMessage("CaughtLegendaryFish", Game1.player?.Name ?? "Player", info.Item.DisplayName);
            }
            else if (rod.recordSize)
            {
                rod.sparklingText = new(Game1.dialogueFont, Game1.content.LoadString(@"Strings\StringsFromCSFiles:FishingRod.cs.14069"), Color.LimeGreen, Color.Azure);
                user.currentLocation.localSound("newRecord");
            }
            else
            {
                user.currentLocation.localSound("fishSlap");
            }
        }

        private void OpenTreasureMenuEndFunction(FishingInfo fishingInfo, FishingRod rod, IEnumerable<CaughtItem> treasure, int bobberDepth)
        {
            fishingInfo.User.gainExperience(5, 10 * (bobberDepth + 1));
            fishingInfo.User.UsingTool = false;
            fishingInfo.User.completelyStopAnimatingOrDoingAction();
            rod.doneFishing(fishingInfo.User, true);

            var eventArgs = new OpeningChestEventArgs(fishingInfo, treasure.ToList());
            this.fishingApi.OnOpeningChest(eventArgs);

            var treasureItems = eventArgs.CaughtItems.Select(caughtItem => caughtItem.Item).ToList();
            if (treasureItems.Any())
            {
                var menu = new ItemGrabMenu(treasureItems, rod) { source = 3 }.setEssential(true);
                Game1.activeClickableMenu = menu;
            }

            fishingInfo.User.completelyStopAnimatingOrDoingAction();
            this.fishingTracker.ActiveFisherData[fishingInfo.User] = new(rod, new FishingState.NotFishing());
        }

        public static bool DoFunction_Prefix(
            GameLocation location,
            Farmer who,
            FishingRod __instance,
            ref bool ___lastCatchWasJunk
        )
        {
            if (FishingRodPatcher.Instance is not { } patcher)
            {
                return true;
            }

            if (!patcher.fishingTracker.ActiveFisherData.TryGetValue(who, out var activeFisher))
            {
                activeFisher = new(__instance, FishingState.Start());
                patcher.fishingTracker.ActiveFisherData[who] = activeFisher;
            }

            if (activeFisher.Rod != __instance)
            {
                activeFisher = new(__instance, FishingState.Start());
                patcher.fishingTracker.ActiveFisherData[who] = activeFisher;
            }

            var fishingInfo = patcher.fishingApi.CreateDefaultFishingInfo(who);

            switch (activeFisher.State)
            {
                case FishingState.NotFishing:
                    patcher.fishingTracker.ActiveFisherData[who] = new(__instance, new FishingState.WaitingForBite(fishingInfo));
                    return true;

                case FishingState.WaitingForBite:
                    who.FarmerSprite.PauseForSingleAnimation = false;
                    int? nextAnim = who.FacingDirection switch { 0 => 299, 1 => 300, 2 => 301, 3 => 302, _ => null };
                    if (nextAnim is { } anim)
                    {
                        who.FarmerSprite.animateBackwardsOnce(anim, 35f);
                    }

                    if (!__instance.isNibbling)
                    {
                        return true;
                    }

                    // Only the local player's machine should compute catch selection.
                    // For non-local players (e.g. a farmhand simulated on the host), TFO on the
                    // farmhand's own machine is already handling this. If we call GetPossibleCatch
                    // and CatchItem here on the host, CatchItem sets net-synced rod fields
                    // (pullingOutOfWater, isFishing, fishCaught, whichFish) which propagate to the
                    // farmhand and corrupt their fishing state, causing 100% trash.
                    // Returning true lets vanilla's non-local-player path run (which is largely a no-op).
                    if (!who.IsLocalPlayer)
                    {
                        return true;
                    }

                    var bobberTile = patcher.helper.Reflection.GetMethod(__instance, "calculateBobberTile").Invoke<Vector2>();
                    var fromFishPond = location.isTileBuildingFishable((int)bobberTile.X, (int)bobberTile.Y);

                    // For fish pond catches, call CatchFish() directly and use the returned
                    // SObject as-is. This preserves any quality set by pond enhancement mods
                    // like Aquarism (DaLion.Ponds), which tracks persistent fish qualities per
                    // pond and may set quality on the returned object. The old approach called
                    // GetFishPondFish (which internally calls CatchFish) but then discarded the
                    // returned item and created a fresh one via factory, losing Aquarism quality.
                    if (fromFishPond)
                    {
                        var pondFish = location.buildings
                            .OfType<FishPond>()
                            .FirstOrDefault(p => p.isTileFishable(bobberTile))
                            ?.CatchFish();
                        if (pondFish is SObject pondFishObj)
                        {
                            var fishKey = NamespacedKey.SdvObject(pondFishObj.ParentSheetIndex);
                            patcher.CatchItem(__instance, new CatchInfo.FishCatch(fishingInfo, new(fishKey, new(0.0)), pondFishObj, -1, false, 0, 0, new(false, TreasureState.None), true));
                            return false;
                        }
                    }

                    var possibleCatch = patcher.fishingApi.GetPossibleCatch(fishingInfo);

                    while (true)
                    {
                        switch (possibleCatch)
                        {
                            case PossibleCatch.Fish(var fishEntry):
                                ___lastCatchWasJunk = false;
                                if (__instance.hit || !who.IsLocalPlayer)
                                {
                                    return false;
                                }

                                if (!fishEntry.TryCreateItem(fishingInfo, patcher.namespaceRegistry, out var caughtFish))
                                {
                                    var trashEntry = patcher.fishingApi.GetTrashChances(fishingInfo).ChooseOrDefault(Game1.random)?.Value ?? new TrashEntry(NamespacedKey.SdvObject(0), new(0.0));
                                    possibleCatch = new PossibleCatch.Trash(trashEntry);
                                    continue;
                                }

                                __instance.hit = true;
                                // Propagate SetFlagOnCatch from the selected fish entry into the
                                // fishing info so CustomBobberBar → BobberBar sets the flag after
                                // a successful catch (important for e.g. Golden Walnut).
                                var catchFishingInfo = fishEntry.AvailabilityInfo.SetFlagOnCatch is { } catchFlag
                                    ? fishingInfo with { SetFlagOnCatch = catchFlag }
                                    : fishingInfo;
                                Game1.screenOverlayTempSprites.Add(new("LooseSprites\\Cursors", new(612, 1913, 74, 30), 1500f, 1, 0, Game1.GlobalToLocal(Game1.viewport, __instance.bobber.Value + new Vector2(-140f, -160f)), false, false, 1f, 0.005f, Color.White, 4f, 0.075f, 0.0f, 0.0f, true)
                                {
                                    scaleChangeChange = -0.005f,
                                    motion = new(0.0f, -0.1f),
                                    endFunction = _ => patcher.StartFishingMinigame(catchFishingInfo, caughtFish.Item, __instance, fishEntry, fromFishPond),
                                    id = (int)9.876543E+08f
                                });
                                location.localSound("FishHit");
                                return false;

                            case PossibleCatch.Trash(var trashEntry):
                                ___lastCatchWasJunk = true;
                                if (trashEntry.TryCreateItem(fishingInfo, patcher.namespaceRegistry, out var caughtTrash))
                                {
                                    patcher.CatchItem(__instance, new CatchInfo.TrashCatch(fishingInfo, trashEntry, caughtTrash.Item, fromFishPond));
                                }
                                else
                                {
                                    patcher.CatchItem(__instance, new CatchInfo.TrashCatch(fishingInfo, trashEntry, ItemRegistry.Create("(O)168", 1), fromFishPond));
                                }
                                return false;

                            default:
                                throw new InvalidOperationException($"Unknown catch type: {possibleCatch}");
                        }
                    }

                default:
                    return false;
            }
        }

        public static bool TickUpdate_Prefix(
            FishingRod __instance,
            GameTime time,
            Farmer who,
            ref int ___recastTimerMs,
            int ___clearWaterDistance
        )
        {
            if (FishingRodPatcher.Instance is not { } patcher)
            {
                return true;
            }
            if (__instance.getLastFarmerToUse() is not { } user || user.CurrentTool != __instance)
            {
                return true;
            }
            if (!patcher.fishingTracker.ActiveFisherData.TryGetValue(user, out var fisherData))
            {
                fisherData = new(__instance, FishingState.Start());
                patcher.fishingTracker.ActiveFisherData[user] = fisherData;
            }
            if (!__instance.inUse())
            {
                patcher.fishingTracker.ActiveFisherData[user] = new(__instance, FishingState.Start());
            }

            switch (fisherData.State)
            {
                case FishingState.Caught(var fishingInfo, var catchInfo):
                    if (!__instance.bobber.Value.Equals(Vector2.Zero) && (__instance.isFishing || __instance.pullingOutOfWater || __instance.castedButBobberStillInAir) && user.FarmerSprite.CurrentFrame is not 57 && (user.FacingDirection is not 0 || !__instance.pullingOutOfWater) || !__instance.fishCaught)
                    {
                        return true;
                    }
                    patcher.fishingTracker.ActiveFisherData[user] = new(__instance, new FishingState.Holding(fishingInfo, catchInfo));
                    return FishingRodPatcher.TickUpdate_Prefix(__instance, time, who, ref ___recastTimerMs, ___clearWaterDistance);

                case FishingState.Holding(var fishingInfo, var catchInfo):
                    if (!user.IsLocalPlayer || Game1.input.GetMouseState().LeftButton != ButtonState.Pressed && !Game1.didPlayerJustClickAtAll() && !Game1.isOneOfTheseKeysDown(Game1.oldKBState, Game1.options.useToolButton))
                    {
                        return true;
                    }

                    var item = catchInfo switch
                    {
                        CatchInfo.FishCatch fishCatch => fishCatch.Item,
                        CatchInfo.TrashCatch(_, var trashItem, _) => trashItem,
                        _ => throw new InvalidOperationException($"Unknown catch info type: {catchInfo}"),
                    };

                    if (item is SObject caughtObj)
                    {
                        if (item.ItemId == GameLocation.CAROLINES_NECKLACE_ITEM_QID)
                        {
                            caughtObj.questItem.Value = true;
                        }
                        if (item.ItemId is "((O)79" or "79" or "((O)842" or "842")
                        {
                            item = user.currentLocation.tryToCreateUnseenSecretNote(user);
                            if (item == null)
                            {
                                return false;
                            }
                        }
                    }

                    user.currentLocation.localSound("coin");
                    var fromFishPond = __instance.fromFishPond;
                    if (!Game1.isFestival() && !fromFishPond && Game1.player.team.specialOrders is { } specialOrders)
                    {
                        foreach (var specialOrder in specialOrders)
                        {
                            specialOrder.onFishCaught?.Invoke(Game1.player, item);
                        }
                    }

                    if (catchInfo is not CatchInfo.FishCatch { State: { Treasure: TreasureState.Caught } } caughtFish)
                    {
                        ___recastTimerMs = 200;
                        user.completelyStopAnimatingOrDoingAction();
                        __instance.doneFishing(user, !fromFishPond);
                        if (Game1.isFestival() || user.addItemToInventoryBool(item))
                        {
                            patcher.fishingTracker.ActiveFisherData[user] = new(__instance, new FishingState.NotFishing());
                            // REMOVED: __instance.isFishing = true;
                            // This was likely causing state desyncs in MP/SplitScreen
                            return false;
                        }
                        Game1.activeClickableMenu = new ItemGrabMenu(new List<Item> { item }, __instance).setEssential(true);
                        return false;
                    }
                    else
                    {
                        __instance.fishCaught = false;
                        __instance.showingTreasure = true;
                        user.UsingTool = true;
                        var treasure = patcher.fishingApi.GetPossibleTreasure(caughtFish).SelectMany(entry =>
                        {
                            entry.OnCatch?.OnCatch(patcher.fishingApi, catchInfo);
                            return entry.TryCreateItem(fishingInfo, patcher.namespaceRegistry, out var caughtItem) ? caughtItem.Yield() : Enumerable.Empty<CaughtItem>();
                        });

                        if (!user.addItemToInventoryBool(item))
                        {
                            treasure = treasure.Append(new(item));
                        }

                        __instance.animations.Add(new TemporaryAnimatedSprite(@"LooseSprites\Cursors", new(64, 1920, 32, 32), 500f, 1, 0, user.Position + new Vector2(-32f, -160f), false, false)
                        {
                            layerDepth = user.StandingPixel.Y / 10000.0f + 1.0f / 1000.0f,
                            alphaFade = -1f / 500f,
                            alpha = 0.0f,
                            color = Color.White,
                            scale = 4f,
                            scaleChange = 0.0f,
                            rotation = 0.0f,
                            motion = new(0.0f, -0.128f),
                            timeBasedMotion = true,
                            endFunction = _ =>
                            {
                                user.currentLocation.localSound("openChest");
                                __instance.sparklingText = null;
                                __instance.animations.Add(new TemporaryAnimatedSprite(@"LooseSprites\Cursors", new(64, 1920, 32, 32), 200f, 4, 0, user.Position + new Vector2(-32f, -228f), false, false)
                                {
                                    layerDepth = user.StandingPixel.Y / 10000.0f + 1.0f / 1000.0f,
                                    color = Color.White,
                                    scale = 4f,
                                    endFunction = _ => patcher.OpenTreasureMenuEndFunction(fishingInfo, __instance, treasure, ___clearWaterDistance)
                                });
                            }
                        });

                        patcher.fishingTracker.ActiveFisherData[user] = new(__instance, new FishingState.OpeningTreasure());
                        return false;
                    }
            }
            return true;
        }

        public static void TickUpdate_Postfix()
        {
            if (FishingRodPatcher.Instance is not { } patcher)
            {
                return;
            }
            while (patcher.postUpdateActions.TryDequeue(out var action))
            {
                action();
            }
        }

        public static bool Draw_Prefix(SpriteBatch b, FishingRod __instance)
        {
            if (FishingRodPatcher.Instance is not { } patcher)
            {
                return true;
            }
            if (__instance.getLastFarmerToUse() is not { } user || user.CurrentTool != __instance)
            {
                return true;
            }
            if (!patcher.fishingTracker.ActiveFisherData.TryGetValue(user, out var fisherData))
            {
                return true;
            }

            switch (fisherData.State)
            {
                case FishingState.Holding(_, var info):
                    var y = (float)(4.0 * Math.Round(Math.Sin(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 250.0), 2));
                    var layerDepth = user.StandingPixel.Y / 10000.0f + 0.06f;

                    b.Draw(Game1.mouseCursors, Game1.GlobalToLocal(Game1.viewport, user.Position + new Vector2(-120f, y - 288f)), new Rectangle(31, 1870, 73, 49), Color.White * 0.8f, 0.0f, Vector2.Zero, 4f, SpriteEffects.None, layerDepth);
                    info.Item.DrawInMenuCorrected(b, Game1.GlobalToLocal(Game1.viewport, user.Position + new Vector2(-124f, y - 284f) + new Vector2(44f, 68f)), 1f, 1f, layerDepth + 0.0001f, StackDrawType.Draw, Color.White, false, new TopLeftDrawOrigin());

                    var count = info.Item is SObject { Stack: var stack } ? stack : 1;
                    count = Math.Min(1, count);
                    foreach (var _ in Enumerable.Range(0, count))
                    {
                        info.Item.DrawInMenuCorrected(b, Game1.GlobalToLocal(Game1.viewport, user.Position + new Vector2(0.0f, -56f)), 3f / 4f, 1f, user.StandingPixel.Y / 10000.0f + 1.0f / 500.0f + 0.06f, StackDrawType.Hide, Color.White, false, new CenterDrawOrigin());
                    }

                    var isLegendary = info is CatchInfo.FishCatch { IsLegendary: true };
                    b.DrawString(Game1.smallFont, info.Item.DisplayName, Game1.GlobalToLocal(Game1.viewport, user.Position + new Vector2((float)(26.0 - Game1.smallFont.MeasureString(info.Item.DisplayName).X / 2.0), y - 278f)), isLegendary ? new(126, 61, 237) : Game1.textColor, 0.0f, Vector2.Zero, 1f, SpriteEffects.None, user.StandingPixel.Y / 10000.0f + 1.0f / 500.0f + 0.06f);

                    if (info is CatchInfo.FishCatch { FishSize: var fishSize })
                    {
                        b.DrawString(Game1.smallFont, Game1.content.LoadString("Strings\\StringsFromCSFiles:FishingRod.cs.14082"), Game1.GlobalToLocal(Game1.viewport, user.Position + new Vector2(20f, y - 214f)), Game1.textColor, 0.0f, Vector2.Zero, 1f, SpriteEffects.None, user.StandingPixel.Y / 10000.0f + 1.0f / 500.0f + 0.06f);
                        b.DrawString(Game1.smallFont, Game1.content.LoadString("Strings\\StringsFromCSFiles:FishingRod.cs.14083", LocalizedContentManager.CurrentLanguageCode != LocalizedContentManager.LanguageCode.en ? Math.Round(fishSize * 2.54) : fishSize), Game1.GlobalToLocal(Game1.viewport, user.Position + new Vector2((float)(85.0 - Game1.smallFont.MeasureString(Game1.content.LoadString("Strings\\StringsFromCSFiles:FishingRod.cs.14083", LocalizedContentManager.CurrentLanguageCode != LocalizedContentManager.LanguageCode.en ? Math.Round(fishSize * 2.54) : fishSize)).X / 2.0), y - 179f)), __instance.recordSize ? Color.Blue * Math.Min(1f, (float)(y / 8.0 + 1.5)) : Game1.textColor, 0.0f, Vector2.Zero, 1f, SpriteEffects.None, user.StandingPixel.Y / 10000.0f + 1.0f / 500.0f + 0.06f);
                    }
                    return false;
            }
            return true;
        }
    }
}
