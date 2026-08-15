using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Tools;
using TehPers.Core.Api.Gameplay;
using TehPers.Core.Api.Items;

namespace TehPers.FishingOverhaul.Api
{
    /// <summary>
    /// Information about a <see cref="Farmer"/> that is fishing.
    ///
    /// It is recommended to instead call <see cref="IFishingApi.CreateDefaultFishingInfo"/> to
    /// create a new instance of this rather than call the constructor directly.
    /// </summary>
    /// <param name="User">The <see cref="Farmer"/> that is fishing.</param>
    public record FishingInfo(Farmer User)
    {
        private static readonly Func<FishingRod, int> getBobberDepth;

        static FishingInfo()
        {
            var rod = Expression.Parameter(typeof(FishingRod), "rod");
            var bobberDepthField = Expression.Field(rod, "clearWaterDistance");
            var getBobberDepthExpr =
                Expression.Lambda<Func<FishingRod, int>>(bobberDepthField, rod);
            FishingInfo.getBobberDepth = getBobberDepthExpr.Compile();
        }

        /// <summary>
        /// The times of day being fished at.
        /// </summary>
        public ImmutableArray<int> Times { get; init; } = ImmutableArray.Create(Game1.timeOfDay);

        /// <summary>
        /// The seasons being fished in.
        /// </summary>
        public Seasons Seasons { get; init; } = User.currentLocation.GetSeason() switch
        {
            Season.Spring => Seasons.Spring,
            Season.Summer => Seasons.Summer,
            Season.Fall => Seasons.Fall,
            Season.Winter => Seasons.Winter,
            _ => Seasons.All,
        };

        /// <summary>
        /// The weathers being fished in.
        /// </summary>
        public Weathers Weathers { get; init; } = Game1.isRaining switch
        {
            true => Weathers.Rainy,
            false => Weathers.Sunny,
        };

        /// <summary>
        /// The water types being fished in.
        /// </summary>
        public WaterTypes WaterTypes { get; init; } = GetWaterType(User);

        /// <summary>
        /// (Optional) Flag to set when fish is caught
        /// </summary>
        public string? SetFlagOnCatch { get; init; } = null;

        /// <summary>
        /// The fishing level of the <see cref="Farmer"/> that is fishing.
        /// </summary>
        public int FishingLevel { get; init; } = User.FishingLevel;

        /// <summary>
        /// The bobber depth.
        /// </summary>
        public int BobberDepth { get; init; } =
            User.CurrentTool is FishingRod { isFishing: true } rod
                ? FishingInfo.getBobberDepth(rod)
                : 4;

        /// <summary>
        /// The names of the locations being fished in.
        /// </summary>
        public ImmutableArray<string> Locations { get; init; } = FishingInfo
            .GetDefaultLocationNames(User.currentLocation)
            .ToImmutableArray();

        /// <summary>
        /// The fishing rod's bobber position.
        /// </summary>
        public Vector2 BobberPosition { get; init; } =
            User.CurrentTool is FishingRod { isFishing: true, bobber: { Value: var bobberPos } }
                ? bobberPos / 64f
                : User.getStandingPosition() / 64f;

        /// <summary>
        /// The bait used for fishing.
        /// </summary>
        public NamespacedKey? Bait { get; } = User.CurrentTool is FishingRod { } rod && rod.GetBait() is { } baitItem
            ? NamespacedKey.SdvObject(baitItem.ParentSheetIndex)
            : null;

        /// <summary>
        /// The specific fish targeted by the bait (if any).
        /// Used for 1.6 Targeted Bait mechanics.
        /// </summary>
        public NamespacedKey? TargetedFish { get; } = User.CurrentTool is FishingRod { } rod
            && rod.GetBait() is { } bait
            && bait.preservedParentSheetIndex.Value is { } targetId
            ? NamespacedKey.SdvObject(targetId)
            : null;

        /// <summary>
        /// The bobber/tackle used for fishing.
        /// </summary>
        public NamespacedKey? Bobber { get; } = User.CurrentTool is FishingRod { } rod && rod.GetTackle().FirstOrDefault() is { } tackleItem
            ? NamespacedKey.SdvObject(tackleItem.ParentSheetIndex)
            : null;

        private static WaterTypes GetWaterType(Farmer farmer)
        {
            // 1.6 Fix: Convert Point (ints) to Vector2 (floats) for API compatibility
            var tileLocation = new Vector2(farmer.Tile.X, farmer.Tile.Y);

            if (farmer.currentLocation.TryGetFishAreaForTile(tileLocation, out var id, out _))
            {
                return id switch
                {
                    "0" => WaterTypes.River,
                    "1" => WaterTypes.PondOrOcean,
                    "2" => WaterTypes.Freshwater,
                    "River" => WaterTypes.River,
                    "Ocean" => WaterTypes.PondOrOcean,
                    "Lake" => WaterTypes.Freshwater,
                    _ => WaterTypes.All,
                };
            }
            return WaterTypes.All;
        }

        private static NamespacedKey? ConvertAttachmentIndex(int index)
        {
            return index switch
            {
                < 0 => null,
                _ => NamespacedKey.SdvObject(index),
            };
        }

        /// <summary>
        /// Gets the location names associated with a <see cref="GameLocation"/>.
        /// </summary>
        public static IEnumerable<string> GetDefaultLocationNames(GameLocation location)
        {
            return location switch
            {
                MineShaft { Name: { } name, mineLevel: var mineLevel } => new[]
                {
                    name, "UndergroundMine", $"UndergroundMine/{mineLevel}"
                },
                Farm { Name: { } name } => Game1.whichFarm switch
                {
                    0 => new[] { name, $"{name}/Standard" },
                    1 => new[] { name, $"{name}/Riverland" },
                    2 => new[] { name, $"{name}/Forest" },
                    3 => new[] { name, $"{name}/Hills" },
                    4 => new[] { name, $"{name}/Wilderness" },
                    5 => new[] { name, $"{name}/FourCorners" },
                    6 => new[] { name, $"{name}/Beach" },
                    7 => new[] { name, $"{name}/Meadows" },
                    _ => new[] { name },
                },
                IslandLocation { Name: { } name } => new[] { name, "Island" },
                _ => new[] { location.Name },
            };
        }
    }
}
