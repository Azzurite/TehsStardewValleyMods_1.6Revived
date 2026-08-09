using System;
using TehPers.Core.Api.DI;
using TehPers.FishingOverhaul.Api;
using TehPers.FishingOverhaul.Api.Content;
using TehPers.FishingOverhaul.Api.Effects;
using StardewModdingAPI.Utilities;

namespace TehPers.FishingOverhaul.Services
{
    internal class FishingEffectManager
    {
        public ConditionsCalculator ConditionsCalculator { get; }
        public FishingEffectEntry Entry { get; }
        public IFishingEffect Effect { get; }

        // FIX: Split-screen co-op bug — this manager is a single shared instance across all
        // screens (see FishingApi.fishingEffectManagers), but UpdateEnabled() is called once per
        // screen per tick with that screen's own farmer. A plain bool let one screen's tick
        // overwrite another's Enabled state, causing Effect.Apply() to be called twice for the
        // same player in the same frame — which double-stacks chance-modifying calculators in
        // ModifyChanceEffectManager. PerScreen keeps Enabled correctly scoped per screen.
        private readonly PerScreen<bool> enabledPerScreen = new();

        public bool Enabled
        {
            get => this.enabledPerScreen.Value;
            private set => this.enabledPerScreen.Value = value;
        }

        public FishingEffectManager(
            IGlobalKernel kernel,
            ConditionsCalculator conditionsCalculator,
            FishingEffectEntry entry
        )
        {
            this.ConditionsCalculator = conditionsCalculator
                ?? throw new ArgumentNullException(nameof(conditionsCalculator));
            this.Entry = entry ?? throw new ArgumentNullException(nameof(entry));
            this.Effect = this.Entry.CreateEffect(kernel);
        }

        public bool? UpdateEnabled(FishingInfo fishingInfo)
        {
            switch (this.Enabled)
            {
                case false when this.ConditionsCalculator.IsAvailable(fishingInfo):
                    this.Enabled = true;
                    return true;
                case true when !this.ConditionsCalculator.IsAvailable(fishingInfo):
                    this.Enabled = false;
                    return false;
                default:
                    return null;
            }
        }
    }
}
