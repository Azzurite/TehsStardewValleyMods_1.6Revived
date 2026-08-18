using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Locations;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using TehPers.Core.Api.Gameplay;
using TehPers.Core.Api.Items;
using TehPers.FishingOverhaul.Api;
using TehPers.FishingOverhaul.Api.Content;
using TehPers.FishingOverhaul.Content;

namespace TehPers.FishingOverhaul.Services
{
    internal sealed partial class DefaultFishingSource
    {
        // Extending the list to include Jellies and other non-fish fishing items
        private static readonly HashSet<string> extendedTrashIds = new()
        {
            "152", // Seaweed
            "153", // Green Algae
            "157", // White Algae
            "167", // Joja Cola
            "168", // Trash
            "169", // Driftwood
            "170", // Broken Glasses
            "171", // Broken CD
            "172", // Soggy Newspaper
            "812", // Roe (Sometimes considered contextual trash)
            "856", // River Jelly (1.6)
            "857", // Sea Jelly (1.6)
            "858", // Cave Jelly (1.6)
            // Ridgeside Village 1.6
            "Rafseazz.RSVCP_Village_Hero_Sculpture",
            "Rafseazz.RSVCP_Sapphire_Pearl"
        };

        private FishingContent GetDefaultTrashData()
        {
            var fishData = Game1.content.Load<Dictionary<string, string>>("Data\\Fish");
            var locationData = Game1.content.Load<Dictionary<string, LocationData>>("Data\\Locations");

            var trashEntries = new List<TrashEntry>();
            var baseAvailabilities = new Dictionary<NamespacedKey, AvailabilityInfo>();

            // --- STEP 1: Parse Base Trash from Data/Fish ---
            foreach (var (rawKey, data) in fishData)
            {
                var parts = data.Split('/');
                if (parts.Length < 13)
                {
                    continue;
                }

                var cleanId = rawKey.StartsWith("(O)") ? rawKey[3..] : rawKey;

                // Only keep explicit trash/algae/jellies
                if (!DefaultFishingSource.extendedTrashIds.Contains(cleanId))
                {
                    continue;
                }

                var itemKey = GetFishKey(rawKey);

                if (!float.TryParse(parts[10], out var chance))
                {
                    if (!float.TryParse(parts[9], out chance))
                    {
                        chance = 0.1f;
                    }
                }

                if (!int.TryParse(parts[12], out var minLevel))
                {
                    if (!int.TryParse(parts[11], out minLevel))
                    {
                        minLevel = 0;
                    }
                }

                var baseInfo = new AvailabilityInfo(chance)
                {
                    MinFishingLevel = minLevel,
                    Seasons = ParseSeasons(parts[6]),
                    Weathers = ParseWeathers(parts[7]),
                };

                var times = parts[5].Split(' ');
                baseInfo = times.Length >= 2 && int.TryParse(times[0], out var start) && int.TryParse(times[1], out var end)
                    ? baseInfo with { StartTime = start, EndTime = end }
                    : baseInfo with { StartTime = 600, EndTime = 2600 };

                baseAvailabilities[itemKey] = baseInfo;
            }

            // --- STEP 2: Iterate Data/Locations for Trash & Jellies ---
            foreach (var (locName, locData) in locationData)
            {
                if (locData.Fish == null)
                {
                    continue;
                }

                foreach (var spawnData in locData.Fish)
                {
                    if (string.IsNullOrEmpty(spawnData.ItemId))
                    {
                        continue;
                    }

                    var cleanId = spawnData.ItemId.StartsWith("(O)") ? spawnData.ItemId[3..] : spawnData.ItemId;

                    if (!DefaultFishingSource.extendedTrashIds.Contains(cleanId))
                    {
                        continue;
                    }

                    var itemKey = GetFishKey(spawnData.ItemId);

                    var info = baseAvailabilities.TryGetValue(itemKey, out var baseAvail)
                        ? baseAvail
                        : new AvailabilityInfo(0.1f);

                    var locations = GetLocationNames(locName);
                    info = info with { IncludeLocations = locations };

                    var waterConstraint = locName switch
                    {
                        "Beach" => WaterTypes.PondOrOcean,
                        "Town" => WaterTypes.River,
                        "Forest" => WaterTypes.River | WaterTypes.Freshwater,
                        "Mountain" => WaterTypes.Freshwater,
                        "Desert" => WaterTypes.Freshwater,
                        _ => WaterTypes.All
                    };

                    if (waterConstraint != WaterTypes.All)
                    {
                        info = info with { WaterTypes = waterConstraint };
                    }

                    if (!string.IsNullOrEmpty(spawnData.Condition))
                    {
                        var tempFishInfo = new FishAvailabilityInfo(info.BaseChance)
                        {
                            StartTime = info.StartTime,
                            EndTime = info.EndTime,
                            Seasons = info.Seasons,
                            Weathers = info.Weathers,
                            MinFishingLevel = info.MinFishingLevel,
                            IncludeLocations = info.IncludeLocations,
                            When = info.When
                        };

                        tempFishInfo = ParseConditionString(spawnData.Condition, tempFishInfo, locName);

                        info = info with
                        {
                            StartTime = tempFishInfo.StartTime,
                            EndTime = tempFishInfo.EndTime,
                            Seasons = tempFishInfo.Seasons,
                            Weathers = tempFishInfo.Weathers,
                            MinFishingLevel = tempFishInfo.MinFishingLevel,
                            When = tempFishInfo.When
                        };
                    }

                    trashEntries.Add(new TrashEntry(itemKey, info));
                }
            }

            // --- STEP 3: Manual Registration for Standard Trash (Missing in 1.6 Data/Locations) ---
            var globalTrashLocations = ImmutableArray.Create(
                "Town", "Forest", "Beach", "Mountain", "Desert", "DesertFestival", "Woods", "Sewer", "BugLand", "WitchSwamp", "UndergroundMine",
                "Farm", "Custom_FrontierFarm", "FrontierFarm",
                "Custom_FerngillRepublicFrontier", "Custom_Ferngill_Frontier", "Ferngill_Frontier", "Custom_FerngillFrontier",
                "Custom_Ridgeside_RidgesideVillage"
            );

            // 168: Trash, 169: Driftwood, 170: Broken Glasses, 171: Broken CD, 172: Soggy Newspaper, 167: Joja Cola
            var standardTrashIds = new[] { "168", "169", "170", "171", "172", "167" };

            foreach (var id in standardTrashIds)
            {
                trashEntries.Add(new TrashEntry(
                    NamespacedKey.SdvObject(id),
                    new AvailabilityInfo(0.1d)
                    {
                        WaterTypes = WaterTypes.All,
                        IncludeLocations = globalTrashLocations
                    }
                ));
            }

            // --- STEP 4: Manual Registration for Jellies (1.6) ---

            // River Jelly
            trashEntries.Add(new TrashEntry(
                NamespacedKey.SdvObject("RiverJelly"),
                new AvailabilityInfo(0.05d)
                {
                    WaterTypes = WaterTypes.River | WaterTypes.PondOrOcean,
                    // ADDED: DesertFestival
                    IncludeLocations = ImmutableArray.Create("Town", "Mountain", "Forest", "Desert", "DesertFestival", "Woods", "Custom_FrontierFarm", "Custom_FerngillRepublicFrontier")
                }
            ));

            // Sea Jelly
            trashEntries.Add(new TrashEntry(
                NamespacedKey.SdvObject("SeaJelly"),
                new AvailabilityInfo(0.05d)
                {
                    WaterTypes = WaterTypes.PondOrOcean,
                    IncludeLocations = ImmutableArray.Create("Beach", "BeachNightMarket", "IslandWest", "IslandSouth", "IslandSouthEast", "Custom_FerngillRepublicFrontier")
                }
            ));

            // Cave Jelly
            trashEntries.Add(new TrashEntry(
                NamespacedKey.SdvObject("CaveJelly"),
                new AvailabilityInfo(0.05d)
                {
                    WaterTypes = WaterTypes.All,
                    IncludeLocations = ImmutableArray.Create("UndergroundMine")
                }
            ));

            // --- STEP 5: Ridgeside Village Specials ---

            // Village Hero Sculpture
            trashEntries.Add(new TrashEntry(
                NamespacedKey.SdvObject("Rafseazz.RSVCP_Village_Hero_Sculpture"),
                new AvailabilityInfo(1.0d)
                {
                    IncludeLocations = ImmutableArray.Create("Custom_Ridgeside_RidgesideVillage"),
                    PriorityTier = 20d,
                    FarmerPosition = new()
                    {
                        X = new() { GreaterThanEq = 145, LessThan = 146 },
                        Y = new() { GreaterThanEq = 69, LessThan = 70 }
                    },
                    When = new Dictionary<string, string?>
                    {
                        ["HasSeenEvent |contains=75160259"] = "true",
                        ["HasFlag |contains=RSV.HeroStatue"] = "false"
                    }.ToImmutableDictionary()
                }
            )
            {
                OnCatch = new CatchActions
                {
                    SetFlags = ImmutableArray.Create("RSV.HeroStatue")
                }
            });

            // Sapphire Pearl
            trashEntries.Add(new TrashEntry(
                NamespacedKey.SdvObject("Rafseazz.RSVCP_Sapphire_Pearl"),
                new AvailabilityInfo(1.0d)
                {
                    IncludeLocations = ImmutableArray.Create("Custom_Ridgeside_RidgesideVillage"),
                    PriorityTier = 20d,
                    Position = new()
                    {
                        new()
                        {
                            X = new() { GreaterThanEq = 60, LessThan = 61 },
                            Y = new() { GreaterThanEq = 55, LessThan = 56 }
                        }
                    },
                    When = new Dictionary<string, string?>
                    {
                        ["HasSeenEvent |contains=75160259"] = "true",
                        ["HasFlag |contains=RSV.Sapphire"] = "false"
                    }.ToImmutableDictionary()
                }
            )
            {
                OnCatch = new CatchActions
                {
                    SetFlags = ImmutableArray.Create("RSV.Sapphire")
                }
            });

            // --- STEP 6: Special One-Off Fishing Collectibles ---
            // These items need MaxFish=0 effects (defined in FishingEffects.cs) to block the
            // fish pool so only the trash pool is consulted. They live here so they bypass the
            // fish-chance gating entirely.

            // Iridium Krobus Statue (Forest, south of the sewer outlet)
            // Vanilla flag: caughtIridiumKrobus — set via mailReceived, checked via HasFlag GSQ.
            trashEntries.Add(new TrashEntry(
                NamespacedKey.SdvObject("IridiumKrobus"),
                new AvailabilityInfo(1.0d)
                {
                    IncludeLocations = ImmutableArray.Create("Forest"),
                    PriorityTier = 20d,
                    Position = new()
                    {
                        new()
                        {
                            Y = new() { GreaterThan = 108 }
                        }
                    },
                    When = new Dictionary<string, string?>
                    {
                        ["HasFlag |contains=caughtIridiumKrobus"] = "false",
                    }.ToImmutableDictionary()
                }
            )
            {
                OnCatch = new CatchActions
                {
                    SetFlags = ImmutableArray.Create("caughtIridiumKrobus")
                }
            });

            // Tide Pool Golden Walnut (IslandSouthEast / Pirate's Cove star)
            // Gated by the TidePoolGoldenWalnut CP token; the custom event handles the actual
            // nut-collection logic (including MP sync via fishWalnutEvent).
            trashEntries.Add(new GoldenWalnutEntry(
                new AvailabilityInfo(1.0d)
                {
                    IncludeLocations = ImmutableArray.Create("IslandSouthEast"),
                    PriorityTier = 20d,
                    Position = new()
                    {
                        new()
                        {
                            X = new() { GreaterThanEq = 18, LessThan = 20 },
                            Y = new() { GreaterThanEq = 20, LessThan = 22 },
                        }
                    },
                    When = new Dictionary<string, string?>
                    {
                        ["Hiztaar.FishingOverhaulRevived/TidePoolGoldenWalnut"] = "false",
                    }.ToImmutableDictionary()
                }
            )
            {
                OnCatch = new CatchActions
                {
                    CustomEvents = ImmutableArray.Create(
                        new NamespacedKey(this.manifest, "TidePoolGoldenWalnut")
                    )
                }
            });

            return new(this.manifest)
            {
                AddTrash = trashEntries.ToImmutableArray()
            };
        }
    }
}
