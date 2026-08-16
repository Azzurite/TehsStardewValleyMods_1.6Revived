using System;
using ContentPatcher;
using StardewModdingAPI;
using StardewValley;
using TehPers.Core.Api.Items;
using TehPers.FishingOverhaul.Api;
using TehPers.FishingOverhaul.Api.Content;

namespace TehPers.FishingOverhaul.Services
{
    internal class ChanceCalculator : ConditionsCalculator
    {
        private readonly AvailabilityInfo availabilityInfo;
        private readonly NamespacedKey? entryKey;
        private readonly IMonitor monitor;

        public ChanceCalculator(
            IMonitor monitor,
            IContentPatcherAPI contentPatcherApi,
            IManifest fishingManifest,
            IManifest owner,
            AvailabilityInfo availabilityInfo,
            NamespacedKey? entryKey = null
        )
            : base(monitor, contentPatcherApi, fishingManifest, owner, availabilityInfo)
        {
            this.availabilityInfo = availabilityInfo
                ?? throw new ArgumentNullException(nameof(availabilityInfo));
            this.entryKey = entryKey;
            this.monitor = monitor;
        }

        public double? GetWeightedChance(FishingInfo fishingInfo)
        {
            // --- DIAGNOSTIC LOGGING (Runs unconditionally at start) ---
            // This will tell us if the mod sees your bait at all.
            /*
            if (fishingInfo.Bait.HasValue)
            {
                if (fishingInfo.TargetedFish is { } tFish)
                {
                    this.monitor.LogOnce($"[TFO Debug] Bait Equipped. Type: {fishingInfo.Bait}. Target Fish Detected: {tFish}", LogLevel.Alert);
                }
                else
                {
                    this.monitor.LogOnce($"[TFO Debug] Bait Equipped. Type: {fishingInfo.Bait}. Target Fish: NULL (Not targeted bait or ID lookup failed)", LogLevel.Warn);
                }
            }
            else
            {
                // Only log this once to avoid massive spam, but let us know if bait is missing when it shouldn't be
                if (fishingInfo.User.CurrentTool is StardewValley.Tools.FishingRod)
                {
                    this.monitor.LogOnce($"[TFO Debug] No Bait Detected on Rod.", LogLevel.Trace);
                }
            } */

            // 1. Availability Check
            if (!this.IsAvailable(fishingInfo))
            {
                return null;
            }

            // 2. Base Chance
            var chance = this.availabilityInfo.GetChance(fishingInfo);

            // 3. Targeted Bait Multiplier
            if (this.entryKey.HasValue && fishingInfo.TargetedFish is { } target)
            {
                // Strict equality check
                var isMatch = target.Equals(this.entryKey.Value);

                // Loose equality check (Resolve IDs)
                if (!isMatch)
                {
                    var targetId = ItemRegistry.GetData(target.ToString())?.ItemId;
                    var entryId = ItemRegistry.GetData(this.entryKey.Value.ToString())?.ItemId;

                    if (targetId != null && targetId == entryId)
                    {
                        isMatch = true;
                    }
                }

                if (isMatch)
                {
                    // Log success if we are actually boosting a fish
                    // this.monitor.LogOnce($"[TFO Success] Targeted Bait Match for {this.entryKey}! boosting chance.", LogLevel.Info);
                    chance *= 10;
                }
            }

            return chance;
        }
    }
}
