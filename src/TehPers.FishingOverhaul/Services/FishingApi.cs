using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Tools;
using TehPers.Core.Api.DI;
using TehPers.Core.Api.Extensions;
using TehPers.Core.Api.Items;
using TehPers.FishingOverhaul.Api;
using TehPers.FishingOverhaul.Api.Content;
using TehPers.FishingOverhaul.Api.Events;
using TehPers.FishingOverhaul.Api.Extensions;
using TehPers.FishingOverhaul.Api.Weighted;
using TehPers.FishingOverhaul.Config;
using TehPers.FishingOverhaul.Integrations.Emp;

namespace TehPers.FishingOverhaul.Services
{
    /// <summary>
    /// Default API for working with fishing.
    /// </summary>
    public sealed partial class FishingApi : BaseFishingApi
    {
        private readonly IModHelper helper;
        private readonly IMonitor monitor;
        private readonly IManifest manifest;
        private readonly FishConfig fishConfig;
        private readonly TreasureConfig treasureConfig;
        private readonly Func<IEnumerable<IFishingContentSource>> contentSourcesFactory;

        private readonly EntryManagerFactory<FishEntry, FishAvailabilityInfo>
            fishEntryManagerFactory;

        private readonly EntryManagerFactory<TrashEntry, AvailabilityInfo> trashEntryManagerFactory;

        private readonly EntryManagerFactory<TreasureEntry, AvailabilityInfo>
            treasureEntryManagerFactory;

        private readonly FishingEffectManagerFactory fishingEffectManagerFactory;

        private readonly Lazy<IOptional<IEmpApi>> empApi;

        internal readonly Dictionary<NamespacedKey, FishTraits> fishTraits;
        internal readonly List<EntryManager<FishEntry, FishAvailabilityInfo>> fishManagers;
        internal readonly List<EntryManager<TrashEntry, AvailabilityInfo>> trashManagers;
        internal readonly List<EntryManager<TreasureEntry, AvailabilityInfo>> treasureManagers;
        internal readonly List<FishingEffectManager> fishingEffectManagers;
        private readonly string stateKey;

        // Thread-safety lock
        private readonly object reloadLock = new();
        private bool reloadRequested;

        internal FishingApi(
            IModHelper helper,
            IMonitor monitor,
            IManifest manifest,
            FishConfig fishConfig,
            TreasureConfig treasureConfig,
            Func<IEnumerable<IFishingContentSource>> contentSourcesFactory,
            EntryManagerFactory<FishEntry, FishAvailabilityInfo> fishEntryManagerFactory,
            EntryManagerFactory<TrashEntry, AvailabilityInfo> trashEntryManagerFactory,
            EntryManagerFactory<TreasureEntry, AvailabilityInfo> treasureEntryManagerFactory,
            FishingEffectManagerFactory fishingEffectManagerFactory,
            Lazy<IOptional<IEmpApi>> empApi
        )
        {
            this.helper = helper ?? throw new ArgumentNullException(nameof(helper));
            this.monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
            this.manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            this.fishConfig = fishConfig ?? throw new ArgumentNullException(nameof(fishConfig));
            this.treasureConfig =
                treasureConfig ?? throw new ArgumentNullException(nameof(treasureConfig));
            this.contentSourcesFactory = contentSourcesFactory
                ?? throw new ArgumentNullException(nameof(contentSourcesFactory));
            this.fishEntryManagerFactory = fishEntryManagerFactory
                ?? throw new ArgumentNullException(nameof(fishEntryManagerFactory));
            this.trashEntryManagerFactory = trashEntryManagerFactory
                ?? throw new ArgumentNullException(nameof(trashEntryManagerFactory));
            this.treasureEntryManagerFactory = treasureEntryManagerFactory
                ?? throw new ArgumentNullException(nameof(treasureEntryManagerFactory));
            this.fishingEffectManagerFactory = fishingEffectManagerFactory
                ?? throw new ArgumentNullException(nameof(fishingEffectManagerFactory));
            this.empApi = empApi ?? throw new ArgumentNullException(nameof(empApi));

            this.fishTraits = new();
            this.fishManagers = new();
            this.trashManagers = new();
            this.treasureManagers = new();
            this.fishingEffectManagers = new();
            this.stateKey = $"{manifest.UniqueID}/fishing-state";

            this.CreatedDefaultFishingInfo += this.ApplyMapOverrides;
            this.CreatedDefaultFishingInfo += this.ApplyEmpOverrides;
            this.CreatedDefaultFishingInfo += FishingApi.ApplyMagicBait;

            this.PreparedFishChances += FishingApi.ApplyCuriosityLure;

            this.reloadRequested = true;
        }

        // --- Helper: ID Matching ---
        private static bool IsIdMatch(string targetId, string fishId)
        {
            var t = targetId.StartsWith("(O)") ? targetId[3..] : targetId;
            var f = fishId.StartsWith("(O)") ? fishId[3..] : fishId;
            return t == f;
        }

        // --- TARGETED BAIT LOGIC ---

        private IEnumerable<IWeightedValue<FishEntry>> ApplyTargetedBaitToWeights(
            FishingInfo fishingInfo,
            IEnumerable<IWeightedValue<FishEntry>> chances)
        {
            if (fishingInfo.User?.CurrentTool is not FishingRod rod || rod.GetBait() is not { } bait)
            {
                return chances;
            }

            if (bait.preservedParentSheetIndex.Value is not { } targetId)
            {
                return chances;
            }

            return chances.ToWeighted(
                weightedValue =>
                {
                    var fishId = weightedValue.Value.FishKey.Key;

                    if (!IsIdMatch(targetId, fishId))
                    {
                        return weightedValue.Weight;
                    }

                    if (IsIdMatch(fishId, "158"))
                    {
                        return weightedValue.Weight + 0.10;
                    }

                    if (IsIdMatch(fishId, "161"))
                    {
                        return weightedValue.Weight + 0.09;
                    }

                    if (IsIdMatch(fishId, "162"))
                    {
                        return weightedValue.Weight + 0.08;
                    }

                    if (IsIdMatch(fishId, "Goby"))
                    {
                        return weightedValue.Weight + 0.20;
                    }

                    return weightedValue.Weight * 200.0;
                },
                weightedValue => weightedValue.Value
            );
        }

        private double ApplyTargetedBaitToChance(FishingInfo fishingInfo, double chance)
        {
            if (fishingInfo.User?.CurrentTool is not FishingRod rod || rod.GetBait() is not { } bait)
            {
                return chance;
            }

            if (bait.preservedParentSheetIndex.Value is not { } targetId)
            {
                return chance;
            }

            var availableFish = FishingApi.GetWeightedEntries(this.fishManagers, fishingInfo);
            var isTargetAvailable = availableFish.Any(f => IsIdMatch(targetId, f.Value.FishKey.Key) && f.Weight > 0);

            if (isTargetAvailable)
            {
                return 1.0d;
            }

            return chance;
        }

        private static (string, float)? GetFarmLocationOverride(Farm farm, IModHelper helper)
        {
            var overrideLocationField =
                helper.Reflection.GetField<string?>(farm, "_fishLocationOverride");
            var overrideChanceField =
                helper.Reflection.GetField<float>(farm, "_fishChanceOverride");

            float overrideChance;
            if (overrideLocationField.GetValue() is not { } overrideLocation)
            {
                var mapProperty = farm.getMapProperty("FarmFishLocationOverride");
                if (mapProperty == string.Empty || mapProperty == null)
                {
                    overrideLocation = string.Empty;
                    overrideChance = 0.0f;
                }
                else
                {
                    var splitProperty = mapProperty.Split(' ');
                    if (splitProperty.Length >= 2 && float.TryParse(splitProperty[1], out overrideChance))
                    {
                        overrideLocation = splitProperty[0];
                    }
                    else
                    {
                        overrideLocation = string.Empty;
                        overrideChance = 0.0f;
                    }
                }
                overrideLocationField.SetValue(overrideLocation);
                overrideChanceField.SetValue(overrideChance);
            }
            else
            {
                overrideChance = overrideChanceField.GetValue();
            }

            if (overrideChance > 0.0)
            {
                return (overrideLocation, overrideChance);
            }
            return null;
        }

        private void ApplyMapOverrides(object? sender, CreatedDefaultFishingInfoEventArgs e)
        {
            if (e.FishingInfo.User.currentLocation is Farm farm
                && GetFarmLocationOverride(farm, this.helper) is var (overrideLocation, overrideChance)
                && Game1.random.NextDouble() < overrideChance)
            {
                e.FishingInfo = e.FishingInfo with
                {
                    Locations = FishingInfo.GetDefaultLocationNames(Game1.getLocationFromName(overrideLocation)).ToImmutableArray(),
                };
            }
        }

        private void ApplyEmpOverrides(object? sender, CreatedDefaultFishingInfoEventArgs e)
        {
            if (!this.empApi.Value.TryGetValue(out var empApi))
            {
                return;
            }

            empApi.GetFishLocationsData(
                e.FishingInfo.User.currentLocation,
                e.FishingInfo.BobberPosition,
                out var empLocationName,
                out var empZone,
                out _
            );

            e.FishingInfo = e.FishingInfo with
            {
                Locations = empLocationName switch
                {
                    null => e.FishingInfo.Locations,
                    _ when Game1.getLocationFromName(empLocationName) is { } empLocation =>
                        FishingInfo.GetDefaultLocationNames(empLocation).ToImmutableArray(),
                    _ => ImmutableArray.Create(empLocationName),
                },
                WaterTypes = empZone switch
                {
                    null => e.FishingInfo.WaterTypes,
                    -1 => WaterTypes.All,
                    0 => WaterTypes.River,
                    1 => WaterTypes.PondOrOcean,
                    2 => WaterTypes.Freshwater,
                    _ => WaterTypes.All,
                },
            };
        }

        private static void ApplyMagicBait(object? sender, CreatedDefaultFishingInfoEventArgs e)
        {
            if (e.FishingInfo.Bait != NamespacedKey.SdvObject(908))
            {
                return;
            }

            e.FishingInfo = e.FishingInfo with
            {
                Seasons = Core.Api.Gameplay.Seasons.All,
                Weathers = Core.Api.Gameplay.Weathers.All,
                Times = Enumerable.Range(600, 2600).ToImmutableArray(),
            };
        }

        private static void ApplyCuriosityLure(object? sender, PreparedFishEventArgs e)
        {
            if (e.FishingInfo.Bobber != NamespacedKey.SdvObject(856))
            {
                return;
            }

            var max = e.FishChances.Max(c => c.Weight);
            e.FishChances = e.FishChances.ToWeighted(
                    weightedValue => weightedValue.Weight >= 0 ? (max * 3 - weightedValue.Weight) : 0,
                    weightedValue => weightedValue.Value
            ).ToList();
        }

        /// <inheritdoc/>
        public override FishingInfo CreateDefaultFishingInfo(Farmer farmer)
        {
            var fishingInfo = new FishingInfo(farmer);
            var eventArgs = new CreatedDefaultFishingInfoEventArgs(fishingInfo);
            this.OnCreatedDefaultFishingInfo(eventArgs);
            return eventArgs.FishingInfo;
        }

        private static IEnumerable<IWeightedValue<TEntry>> GetWeightedEntries<TEntry, TAvailability>(
                IEnumerable<EntryManager<TEntry, TAvailability>> managers,
                FishingInfo fishingInfo
            )
            where TEntry : Entry<TAvailability>
            where TAvailability : AvailabilityInfo
        {
            var chances = managers.SelectMany(
                manager => manager.ChanceCalculator.GetWeightedChance(fishingInfo)
                    .AsEnumerable()
                    .ToWeighted(weight => weight, _ => manager.Entry)
            );
            var highestTier = chances.GroupBy(entry => entry.Value.AvailabilityInfo.PriorityTier)
                .OrderByDescending(group => group.Key)
                .FirstOrDefault();

            return highestTier ?? Enumerable.Empty<IWeightedValue<TEntry>>();
        }

        /// <inheritdoc/>
        public override IEnumerable<IWeightedValue<FishEntry>> GetFishChances(FishingInfo fishingInfo)
        {
            this.ReloadIfRequested();
            var chances = FishingApi.GetWeightedEntries(this.fishManagers, fishingInfo);
            var preparedChancesArgs = new PreparedFishEventArgs(fishingInfo, chances.ToList());
            this.OnPreparedFishChances(preparedChancesArgs);

            return this.ApplyTargetedBaitToWeights(fishingInfo, preparedChancesArgs.FishChances);
        }

        /// <inheritdoc/>
        public override bool TryGetFishTraits(NamespacedKey fishKey, [NotNullWhen(true)] out FishTraits? traits)
        {
            this.ReloadIfRequested();
            if (!this.fishTraits.TryGetValue(fishKey, out traits))
            {
                traits = null;
                return false;
            }
            var dartFrequency = (int)(this.fishConfig.GlobalDartFrequencyFactor * traits.DartFrequency);
            traits = traits with { DartFrequency = dartFrequency };
            return true;
        }

        /// <inheritdoc/>
        public override IEnumerable<IWeightedValue<TrashEntry>> GetTrashChances(FishingInfo fishingInfo)
        {
            this.ReloadIfRequested();
            var chances = FishingApi.GetWeightedEntries(this.trashManagers, fishingInfo);
            var preparedChancesArgs = new PreparedTrashEventArgs(fishingInfo, chances.ToList());
            this.OnPreparedTrashChances(preparedChancesArgs);
            return preparedChancesArgs.TrashChances;
        }

        /// <inheritdoc/>
        public override IEnumerable<IWeightedValue<TreasureEntry>> GetTreasureChances(FishingInfo fishingInfo)
        {
            this.ReloadIfRequested();
            var chances = FishingApi.GetWeightedEntries(this.treasureManagers, fishingInfo);
            var preparedChancesArgs = new PreparedTreasureEventArgs(fishingInfo, chances.ToList());
            this.OnPreparedTreasureChances(preparedChancesArgs);
            return preparedChancesArgs.TreasureChances;
        }

        /// <inheritdoc/>
        public override double GetChanceForFish(FishingInfo fishingInfo)
        {
            var streak = this.GetStreak(fishingInfo.User);
            var chanceForFish = this.fishConfig.FishChances.GetUnclampedChance(fishingInfo.User, streak);
            var eventArgs = new CalculatedFishChanceEventArgs(fishingInfo, chanceForFish);
            this.OnCalculatedFishChance(eventArgs);

            var finalChance = this.ApplyTargetedBaitToChance(fishingInfo, eventArgs.Chance);

            return this.ClampFishChance(fishingInfo, finalChance);
        }

        /// <inheritdoc/>
        public override double GetChanceForTreasure(FishingInfo fishingInfo)
        {
            var streak = this.GetStreak(fishingInfo.User);
            var chanceForTreasure = this.treasureConfig.TreasureChances.GetUnclampedChance(fishingInfo.User, streak);
            var eventArgs = new CalculatedTreasureChanceEventArgs(fishingInfo, chanceForTreasure);
            this.OnCalculatedTreasureChance(eventArgs);
            return this.ClampTreasureChance(fishingInfo, eventArgs.Chance);
        }

        private double ClampFishChance(FishingInfo fishingInfo, double chance)
        {
            var minArgs = new CalculatedFishChanceEventArgs(fishingInfo, this.fishConfig.FishChances.MinChance);
            this.OnCalculatedMinFishChance(minArgs);
            var maxArgs = new CalculatedFishChanceEventArgs(fishingInfo, this.fishConfig.FishChances.MaxChance);
            this.OnCalculatedMaxFishChance(maxArgs);
            return minArgs.Chance > maxArgs.Chance ? maxArgs.Chance : Math.Clamp(chance, minArgs.Chance, maxArgs.Chance);
        }

        private double ClampTreasureChance(FishingInfo fishingInfo, double chance)
        {
            var minArgs = new CalculatedTreasureChanceEventArgs(fishingInfo, this.treasureConfig.TreasureChances.MinChance);
            this.OnCalculatedMinTreasureChance(minArgs);
            var maxArgs = new CalculatedTreasureChanceEventArgs(fishingInfo, this.treasureConfig.TreasureChances.MaxChance);
            this.OnCalculatedMaxTreasureChance(maxArgs);
            return minArgs.Chance > maxArgs.Chance ? maxArgs.Chance : Math.Clamp(chance, minArgs.Chance, maxArgs.Chance);
        }

        /// <inheritdoc/>
        public override bool IsLegendary(NamespacedKey fishKey)
        {
            return this.TryGetFishTraits(fishKey, out var traits) && traits.IsLegendary;
        }

        /// <inheritdoc/>
        public override int GetStreak(Farmer farmer)
        {
            var key = $"{this.stateKey}/streak";
            return farmer.modData.TryGetValue(key, out var rawData) && int.TryParse(rawData, out var streak) ? streak : 0;
        }

        /// <inheritdoc/>
        public override void SetStreak(Farmer farmer, int streak)
        {
            var key = $"{this.stateKey}/streak";
            farmer.modData[key] = streak.ToString();
        }

        /// <inheritdoc/>
        public override PossibleCatch GetPossibleCatch(FishingInfo fishingInfo)
        {
            var fishChance = this.GetChanceForFish(fishingInfo);
            var possibleFish = (IEnumerable<IWeightedValue<FishEntry?>>)this.GetFishChances(fishingInfo).Normalize(fishChance);
            var fishEntry = possibleFish.Append(new WeightedValue<FishEntry?>(null, 1 - fishChance)).ChooseOrDefault(Game1.random)?.Value;

            if (fishEntry is not null)
            {
                return new PossibleCatch.Fish(fishEntry);
            }

            var trashEntry = this.GetTrashChances(fishingInfo).ChooseOrDefault(Game1.random)?.Value;
            if (trashEntry is not null)
            {
                return new PossibleCatch.Trash(trashEntry);
            }

            this.monitor.Log("No valid trash, selecting a default item.", LogLevel.Warn);
            var defaultTrashKey = NamespacedKey.SdvObject(168);
            return new PossibleCatch.Trash(new(defaultTrashKey, new(0.0)));
        }

        /// <inheritdoc/>
        public override IEnumerable<TreasureEntry> GetPossibleTreasure(CatchInfo.FishCatch catchInfo)
        {
            var possibleLoot = this.GetTreasureChances(catchInfo.FishingInfo).ToList();
            if (this.treasureConfig.InvertChancesOnPerfectCatch && catchInfo.State.IsPerfect)
            {
                possibleLoot = possibleLoot.Normalize().ToWeighted(item => 1.0 - item.Weight, item => item.Value).ToList();
            }

            var streak = this.GetStreak(catchInfo.FishingInfo.User);
            var chance = 1d;
            var rewards = 0;
            var additionalLootChance = this.treasureConfig.AdditionalLootChances.GetUnclampedChance(catchInfo.FishingInfo.User, streak);
            additionalLootChance = this.treasureConfig.AdditionalLootChances.ClampChance(additionalLootChance);

            while (possibleLoot.Any() && rewards < this.treasureConfig.MaxTreasureQuantity && Game1.random.NextDouble() <= chance)
            {
                var treasure = possibleLoot.Choose(Game1.random);
                rewards += 1;
                yield return treasure.Value;

                if (!this.treasureConfig.AllowDuplicateLoot || !treasure.Value.AllowDuplicates)
                {
                    possibleLoot.Remove(treasure);
                }
                chance *= additionalLootChance;
            }
        }

        internal new void OnCaughtItem(CaughtItemEventArgs e) { base.OnCaughtItem(e); }
        internal new void OnOpeningChest(OpeningChestEventArgs e) { base.OnOpeningChest(e); }
    }
}
