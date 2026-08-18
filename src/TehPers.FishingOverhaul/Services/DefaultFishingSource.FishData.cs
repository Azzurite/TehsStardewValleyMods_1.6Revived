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

namespace TehPers.FishingOverhaul.Services
{
    internal sealed partial class DefaultFishingSource
    {
        private static readonly HashSet<string> legendaryFishIds = new()
        {
            "159", "160", "163", "682", "775"
        };

        private static readonly HashSet<string> legendaryFamilyIds = new()
        {
            "898", "899", "900", "901", "902"
        };

        private static readonly HashSet<string> trashFishIds = new()
        {
            "152", "153", "157"
        };

        private static readonly HashSet<string> manualOverrideIds = new()
        {
            "158", "161", "162", "164", "165", "798", "799", "800"
        };

        private FishingContent GetDefaultFishData()
        {
            var fishData = Game1.content.Load<Dictionary<string, string>>("Data\\Fish");
            var locationData = Game1.content.Load<Dictionary<string, LocationData>>("Data\\Locations");

            var fishEntries = new List<FishEntry>();
            var fishTraits = new Dictionary<NamespacedKey, FishTraits>();
            var baseAvailabilities = new Dictionary<NamespacedKey, FishAvailabilityInfo>();

            // --- STEP 1: Parsing Data/Fish ---
            // Require at least 12 fields (indices 0–11) for the old format.
            // Vanilla SDV 1.6 fish typically have 12 fields; newer/modded entries may have 13+.
            // FIX: Old guard was < 13, which silently skipped every 12-field entry (all vanilla
            // fish in the old format), losing their difficulty/behavior/size traits entirely.
            foreach (var (rawKey, data) in fishData)
            {
                var parts = data.Split('/');
                if (parts.Length < 12)
                {
                    continue;
                }

                var cleanId = rawKey.StartsWith("(O)") ? rawKey[3..] : rawKey;
                if (trashFishIds.Contains(cleanId))
                {
                    continue;
                }

                var fishKey = GetFishKey(rawKey);
                var qualifiedId = rawKey.StartsWith("(O)") ? rawKey : "(O)" + rawKey;
                var tempItem = ItemRegistry.Create(qualifiedId);

                var isFamilyLegendary = (tempItem != null && tempItem.HasContextTag("fish_legendary_family")) || legendaryFamilyIds.Contains(cleanId);
                var isVanillaLegendary = !isFamilyLegendary && ((tempItem != null && tempItem.HasContextTag("fish_legendary")) || legendaryFishIds.Contains(cleanId));
                var isLegendary = isVanillaLegendary || isFamilyLegendary;

                if (int.TryParse(parts[1], out var difficulty) &&
                    int.TryParse(parts[3], out var minSize) &&
                    int.TryParse(parts[4], out var maxSize))
                {
                    var behavior = parts[2].ToLowerInvariant() switch
                    {
                        "mixed" => DartBehavior.Mixed,
                        "dart" => DartBehavior.Dart,
                        "smooth" => DartBehavior.Smooth,
                        "sinker" => DartBehavior.Sink,
                        "sink" => DartBehavior.Sink,
                        "floater" => DartBehavior.Floater,
                        "float" => DartBehavior.Floater,
                        _ => DartBehavior.Mixed
                    };

                    fishTraits[fishKey] = new FishTraits(difficulty, behavior, minSize, maxSize)
                    {
                        IsLegendary = isLegendary
                    };
                }

                // Chance: prefer index 10 (depth multiplier column), fall back to 9 (spawn multiplier).
                // For 12-field entries parts[10] and parts[9] are both present (indices 0-11).
                var chance = 0.5f;
                if (parts.Length > 10 && float.TryParse(parts[10], out var c10))
                {
                    chance = c10;
                }
                else if (parts.Length > 9 && float.TryParse(parts[9], out var c9))
                {
                    chance = c9;
                }

                // MinFishingLevel: index 12 (new format) or 11 (old 12-field format).
                var minLevel = 0;
                if (parts.Length > 12 && int.TryParse(parts[12], out var lv12))
                {
                    minLevel = lv12;
                }
                else if (parts.Length > 11 && int.TryParse(parts[11], out var lv11))
                {
                    minLevel = lv11;
                }

                var baseInfo = new FishAvailabilityInfo(chance)
                {
                    MinFishingLevel = minLevel,
                    Seasons = ParseSeasons(parts[6]),
                    Weathers = ParseWeathers(parts[7]),
                };

                // Time windows: Data/Fish uses space-separated pairs ("600 2000" or "600 1100 1700 2000").
                // FIX: Old code only read the FIRST pair. Crimsonfish (and others) may have multiple
                // windows (e.g. morning + evening). We now use the widest span across all pairs so
                // the fish can appear during any of its intended time windows.
                var times = parts[5].Split(' ');
                var timeStart = 2600;
                var timeEnd = 600;
                for (var ti = 0; ti + 1 < times.Length; ti += 2)
                {
                    if (int.TryParse(times[ti], out var ts) && int.TryParse(times[ti + 1], out var te))
                    {
                        if (ts < timeStart)
                        {
                            timeStart = ts;
                        }
                        if (te > timeEnd)
                        {
                            timeEnd = te;
                        }
                    }
                }
                baseInfo = timeStart < timeEnd
                    ? baseInfo with { StartTime = timeStart, EndTime = timeEnd }
                    : baseInfo with { StartTime = 600, EndTime = 2600 };

                baseAvailabilities[fishKey] = baseInfo;
            }

            // --- STEP 2: Iterate Data/Locations ---
            // Track items that are in Data/Locations but NOT in Data/Fish (e.g. Golden Walnut).
            // These get synthetic easy traits and must not get a frenzy entry.
            var specialNonFishKeys = new HashSet<NamespacedKey>();

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

                    // FIX: Skip SDV 1.6 item queries (e.g. "LOCATION_FISH Desert BOBBER_X BOBBER_Y WATER_DEPTH").
                    // These are runtime-evaluated expressions, not literal item IDs. ItemRegistry.Create()
                    // may return a non-null placeholder for them rather than null, causing them to leak
                    // into the fish pool as "Unknown" HUD entries. All valid item IDs are either type-
                    // qualified "(X)id" strings or bare alphanumeric/underscore IDs — none contain spaces.
                    if (spawnData.ItemId.Contains(' '))
                    {
                        continue;
                    }

                    var cleanId = spawnData.ItemId.StartsWith("(O)") ? spawnData.ItemId[3..] : spawnData.ItemId;
                    if (trashFishIds.Contains(cleanId) || manualOverrideIds.Contains(cleanId))
                    {
                        continue;
                    }

                    var fishKey = GetFishKey(spawnData.ItemId);
                    if (!fishTraits.ContainsKey(fishKey))
                    {
                        // Item is not in Data/Fish (e.g. Golden Walnut (O)73, furniture (F)2427).
                        // In SDV 1.6, SpawnFishData.ItemId can be any qualified item type.
                        // Try creating the item directly; fall back to (O) prefix for legacy
                        // unqualified numeric IDs that vanilla assumes are objects.
                        var testItem = ItemRegistry.Create(spawnData.ItemId)
                            ?? ItemRegistry.Create("(O)" + spawnData.ItemId);
                        if (testItem == null)
                        {
                            continue; // Not a valid item — skip
                        }
                        // Assign synthetic easy traits (difficulty 0 = fish bar never moves)
                        fishTraits[fishKey] = new FishTraits(0, DartBehavior.Smooth, 1, 1);
                        specialNonFishKeys.Add(fishKey);
                    }

                    // For context tag checks and GSQ conditions, derive a fully-qualified ID.
                    // If the ID already has a type prefix (e.g. (F), (W), (BC)), use it as-is.
                    // Otherwise assume Object and prepend (O) for legacy unqualified numeric IDs.
                    var qualifiedId = spawnData.ItemId.StartsWith("(")
                        ? spawnData.ItemId
                        : "(O)" + spawnData.ItemId;
                    var tempItem = ItemRegistry.Create(qualifiedId);

                    var isFamilyLegendary = (tempItem != null && tempItem.HasContextTag("fish_legendary_family")) || legendaryFamilyIds.Contains(cleanId);
                    var isVanillaLegendary = !isFamilyLegendary && ((tempItem != null && tempItem.HasContextTag("fish_legendary")) || legendaryFishIds.Contains(cleanId));
                    var isLegendary = isVanillaLegendary || isFamilyLegendary;

                    var info = baseAvailabilities.TryGetValue(fishKey, out var baseAvail)
                        ? baseAvail
                        : new FishAvailabilityInfo(0.5f) { StartTime = 600, EndTime = 2600 };

                    var locations = GetLocationNames(locName, isLegendary);
                    info = info with { IncludeLocations = locations };

                    // --- STRICT WATER TYPE ENFORCEMENT ---
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
                        info = ParseConditionString(spawnData.Condition, info, locName);
                    }

                    // FIX: SDV 1.6 SpawnFishData has structured Season and MinFishingLevel fields
                    // that are the *authoritative* source in vanilla data — the Condition string
                    // may not repeat them. If set, these override whatever ParseConditionString
                    // extracted (or the Data/Fish fallback) so the constraints are always correct.
                    if (spawnData.Season is { } spawnSeason)
                    {
                        var structuredSeason = spawnSeason switch
                        {
                            Season.Spring => Seasons.Spring,
                            Season.Summer => Seasons.Summer,
                            Season.Fall => Seasons.Fall,
                            Season.Winter => Seasons.Winter,
                            _ => Seasons.None,
                        };
                        if (structuredSeason != Seasons.None)
                        {
                            info = info with { Seasons = structuredSeason };
                        }
                    }

                    if (spawnData.MinFishingLevel > 0)
                    {
                        // Use the larger of the two sources so a Data/Fish constraint is never
                        // silently lowered by a Data/Locations entry that omits the field.
                        info = info with { MinFishingLevel = Math.Max(info.MinFishingLevel, spawnData.MinFishingLevel) };
                    }

                    // Preserve the vanilla SetFlagOnCatch so the flag is set after the minigame
                    // (e.g., goldenWalnutSpot for the IslandWest tide-pool Golden Walnut).
                    if (!string.IsNullOrEmpty(spawnData.SetFlagOnCatch))
                    {
                        info = info with { SetFlagOnCatch = spawnData.SetFlagOnCatch };
                    }

                    if (spawnData.PlayerPosition is { } pRect)
                    {
                        var xMax = pRect.X + pRect.Width;
                        var yMax = pRect.Y + pRect.Height;
                        info = info with
                        {
                            When = info.When
                                .Add($"Query: PLAYER_TILE_X Current {pRect.X} {xMax}", "true")
                                .Add($"Query: PLAYER_TILE_Y Current {pRect.Y} {yMax}", "true")
                        };
                    }

                    if (spawnData.BobberPosition is { } bRect)
                    {
                        info = info with
                        {
                            When = info.When.Add($"Query: BOBBER_IN_RECT {bRect.X} {bRect.Y} {bRect.Width} {bRect.Height}", "true")
                        };
                    }

                    if (spawnData.CuriosityLureBuff is { } buff && buff != -1) {
                        info = info with { CuriosityLureBuff = buff };
                    }

                    if (isVanillaLegendary)
                    {
                        // Strip ALL When conditions. Season/location/time/water-type constraints are
                        // already encoded in the FishAvailabilityInfo fields, so the When dict only
                        // needs to carry the recatch gate. This prevents mod-added conditions (e.g.
                        // SVE event-gate flags, year requirements) from silently blocking the fish.
                        info = info with
                        {
                            When = ImmutableDictionary<string, string?>.Empty
                                .Add($"Query: LEGENDARY_IS_RECHARGEABLE Current {qualifiedId}", "true")
                        };
                    }
                    else if (isFamilyLegendary)
                    {
                        info = info with
                        {
                            When = info.When.Add("Query: PLAYER_HAS_SPECIAL_ORDER_RULE Current LEGENDARY_FAMILY", "true")
                        };
                    }

                    fishEntries.Add(new FishEntry(fishKey, info));

                    // Special non-fish items (e.g. Golden Walnut) must not get a frenzy entry:
                    // they're one-off collectibles, not regular fish that should appear during frenzies.
                    if (!isVanillaLegendary && !isFamilyLegendary && !specialNonFishKeys.Contains(fishKey))
                    {
                        var frenzyInfo = info with
                        {
                            BaseChance = 5.0f,
                            Seasons = Seasons.Spring | Seasons.Summer | Seasons.Fall | Seasons.Winter,
                            Weathers = Weathers.All,
                            StartTime = 600,
                            EndTime = 2600,
                            MinFishingLevel = 0,
                            When = info.When.Add($"Query: CATCHING_FRENZY_FISH {qualifiedId}", "true")
                        };
                        fishEntries.Add(new FishEntry(fishKey, frenzyInfo));
                    }
                }
            }

            // --- STEP 3: Manual Injections ---
            if (fishTraits.ContainsKey(NamespacedKey.SdvObject(158)))
            {
                var locs = Enumerable.Range(20, 40).Select(i => $"UndergroundMine/{i}").ToImmutableArray();
                var baseInfo = baseAvailabilities.GetValueOrDefault(NamespacedKey.SdvObject(158)) ?? new FishAvailabilityInfo(0.05f);
                fishEntries.Add(new FishEntry(NamespacedKey.SdvObject(158), baseInfo with { IncludeLocations = locs }));
            }

            if (fishTraits.ContainsKey(NamespacedKey.SdvObject(161)))
            {
                var locs = Enumerable.Range(60, 40).Select(i => $"UndergroundMine/{i}").ToImmutableArray();
                var baseInfo = baseAvailabilities.GetValueOrDefault(NamespacedKey.SdvObject(161)) ?? new FishAvailabilityInfo(0.05f);
                fishEntries.Add(new FishEntry(NamespacedKey.SdvObject(161), baseInfo with { IncludeLocations = locs }));
            }

            if (fishTraits.ContainsKey(NamespacedKey.SdvObject(162)))
            {
                var locs = Enumerable.Range(100, 21).Select(i => $"UndergroundMine/{i}").Concat(new[] { "Caldera", "VolcanoDungeon" }).ToImmutableArray();
                var baseInfo = baseAvailabilities.GetValueOrDefault(NamespacedKey.SdvObject(162)) ?? new FishAvailabilityInfo(0.02f);
                fishEntries.Add(new FishEntry(NamespacedKey.SdvObject(162), baseInfo with { IncludeLocations = locs }));
            }

            // FIX: Desert Festival Fishing (Sandfish ID=164 & Scorpion Carp ID=165)
            // These fish are in manualOverrideIds so they skip the Data/Locations loop above.
            // We create TWO entries per fish:
            //   1. "Desert" with original seasons — normal desert fishing year-round
            //   2. "Desert" + "DesertFestival" with Spring forced — covers the festival (Spring 15-17)
            //      regardless of whether the festival runs on the base "Desert" map or a separate
            //      "DesertFestival" map (the game's internal location name is ambiguous at compile time).
            //      Scorpion Carp's default seasons are Summer/Fall, so the Spring override is required
            //      for it to appear at all during Willy's Day 2 challenge.
            foreach (var id in new[] { 164, 165 })
            {
                if (fishTraits.ContainsKey(NamespacedKey.SdvObject(id)))
                {
                    var baseInfo = baseAvailabilities.GetValueOrDefault(NamespacedKey.SdvObject(id)) ?? new FishAvailabilityInfo(0.1f);

                    // Normal Desert entry — keeps original seasons, times, water types
                    fishEntries.Add(new FishEntry(
                        NamespacedKey.SdvObject(id),
                        baseInfo with { IncludeLocations = ImmutableArray.Create("Desert") }
                    ));

                    // Festival entry — Seasons.All because the DesertFestival map reports Season=Summer
                    // at runtime (location-level override), even though the calendar date is Spring 15-17.
                    // The "DesertFestival" location name is the real guard; season filtering is redundant here.
                    fishEntries.Add(new FishEntry(
                        NamespacedKey.SdvObject(id),
                        baseInfo with
                        {
                            IncludeLocations = ImmutableArray.Create("Desert", "DesertFestival"),
                            Seasons = Seasons.All,
                            StartTime = 600,
                            EndTime = 2600,
                            WaterTypes = WaterTypes.All,
                        }
                    ));
                }
            }

            // FIX: Golden Bobber — Willy's Desert Festival Day 3 quest reward.
            // Vanilla injects the bobber via Desert.getFish() when the "GoldenBobber" special order
            // rule is active. TFO bypasses getFish() entirely, so we replicate the mechanism here:
            // add the bobber to the fish pool with PriorityTier=100 so it is always the next catch
            // when the quest is active at DesertFestival. Once the player delivers the bobber to Willy
            // the special order completes and the rule clears, restoring normal fishing.
            {
                var bobberKey = NamespacedKey.SdvObject("GoldenBobber");
                if (!fishTraits.ContainsKey(bobberKey))
                {
                    // Trivially easy minigame — difficulty 0, smooth dart, size 1.
                    fishTraits[bobberKey] = new FishTraits(0, DartBehavior.Smooth, 1, 1);
                    specialNonFishKeys.Add(bobberKey); // not a real fish — no frenzy entry
                }

                fishEntries.Add(new FishEntry(
                    bobberKey,
                    new FishAvailabilityInfo(1.0f)
                    {
                        Seasons = Seasons.All,
                        WaterTypes = WaterTypes.All,
                        IncludeLocations = ImmutableArray.Create("DesertFestival"),
                        PriorityTier = 100d,
                        When = new Dictionary<string, string?>
                        {
                            ["Query: PLAYER_HAS_SPECIAL_ORDER_RULE Current GoldenBobber"] = "true",
                        }.ToImmutableDictionary()
                    }
                ));
            }

            foreach (var id in new[] { 798, 799, 800, 154, 155, 149 })
            {
                if (fishTraits.ContainsKey(NamespacedKey.SdvObject(id)))
                {
                    var baseInfo = baseAvailabilities.GetValueOrDefault(NamespacedKey.SdvObject(id)) ?? new FishAvailabilityInfo(0.1f);
                    fishEntries.Add(new FishEntry(NamespacedKey.SdvObject(id), baseInfo with { IncludeLocations = ImmutableArray.Create("Submarine") }));
                }
            }

            // FIX: Random Golden Walnuts — Ginger Island fishing (up to 5 total)
            // Vanilla Data/Locations entries for island golden walnuts may have conditions that
            // ParseConditionString cannot correctly translate (e.g. limitedNutDrops checks).
            // We inject a canonical entry here with:
            //   • All island fishing locations
            //   • A CP token gate so walnuts stop appearing once all 5 have been collected
            //   • The RandomGoldenWalnut custom event to properly increment the team counter
            //     (handles multiplayer sync via RequestLimitedNutDrops)
            {
                var walnutKey = NamespacedKey.SdvObject(73);
                if (!fishTraits.ContainsKey(walnutKey))
                {
                    fishTraits[walnutKey] = new FishTraits(0, DartBehavior.Smooth, 1, 1);
                    specialNonFishKeys.Add(walnutKey);
                }

                var islandWalnutLocations = ImmutableArray.Create(
                    "IslandNorth", "IslandSouth", "IslandWest", "IslandSouthEast", "IslandEast"
                );

                fishEntries.Add(new FishEntry(
                    walnutKey,
                    new FishAvailabilityInfo(0.1f)
                    {
                        StartTime = 600,
                        EndTime = 2600,
                        Seasons = Seasons.All,
                        WaterTypes = WaterTypes.All,
                        IncludeLocations = islandWalnutLocations,
                        When = new Dictionary<string, string?>
                        {
                            // Only available while fewer than 5 island fishing walnuts have been collected.
                            // RandomGoldenWalnuts token returns the current limitedNutDrops["IslandFishing"] count.
                            ["Hiztaar.FishingOverhaulRevived/RandomGoldenWalnuts"] = "{{Range: 0, 4}}",
                        }.ToImmutableDictionary()
                    }
                )
                {
                    OnCatch = new CatchActions
                    {
                        CustomEvents = ImmutableArray.Create(
                            new NamespacedKey(this.manifest, "RandomGoldenWalnut")
                        )
                    }
                });
            }

            return new(this.manifest)
            {
                AddFish = fishEntries.ToImmutableArray(),
                SetFishTraits = fishTraits.ToImmutableDictionary()
            };
        }

        private static NamespacedKey GetFishKey(string rawId)
        {
            // SDV 1.6 SpawnFishData.ItemId can use any qualified item type prefix.
            // Route to the correct NamespacedKey factory so HUD and factory lookups work.
            if (rawId.StartsWith("(F)"))
            {
                return NamespacedKey.SdvFurniture(rawId[3..]);
            }
            if (rawId.StartsWith("(W)"))
            {
                return NamespacedKey.SdvWeapon(rawId[3..]);
            }
            if (rawId.StartsWith("(BC)"))
            {
                return NamespacedKey.SdvBigCraftable(rawId[4..]);
            }
            if (rawId.StartsWith("(H)"))
            {
                return NamespacedKey.SdvHat(rawId[3..]);
            }
            if (rawId.StartsWith("(B)"))
            {
                return NamespacedKey.SdvBoots(rawId[3..]);
            }

            // Object — with or without (O) prefix (legacy / unqualified IDs)
            var cleanId = rawId.StartsWith("(O)") ? rawId[3..] : rawId;
            return int.TryParse(cleanId, out var intId)
                ? NamespacedKey.SdvObject(intId)
                : NamespacedKey.SdvObject(cleanId);
        }

        private static ImmutableArray<string> GetLocationNames(string locationName, bool isLegendary = false)
        {
            if (isLegendary)
            {
                return ImmutableArray.Create(locationName);
            }

            var frontierFarmOnly = new[] { "Custom_FrontierFarm", "FrontierFarm" };
            var ferngillMulti = new[] { "Custom_FerngillRepublicFrontier", "Custom_Ferngill_Frontier", "Ferngill_Frontier", "Custom_FerngillFrontier" };
            var standardFarm = new[] { "Farm" };
            var desertFest = new[] { "DesertFestival" };

            return locationName switch
            {
                "Beach" => ImmutableArray.Create("Beach", "BeachNightMarket", "Farm/Beach")
                    .AddRange(ferngillMulti),

                "Forest" => ImmutableArray.Create("Forest", "Farm/Riverland", "Farm/Forest", "Farm/Hills", "Farm/FourCorners")
                    .AddRange(frontierFarmOnly)
                    .AddRange(ferngillMulti)
                    .AddRange(standardFarm),

                "Town" => ImmutableArray.Create("Town", "Farm/Riverland", "Farm/Standard")
                    .AddRange(frontierFarmOnly)
                    .AddRange(ferngillMulti)
                    .AddRange(standardFarm),

                "Mountain" => ImmutableArray.Create("Mountain", "Farm/Mountain", "Farm/FourCorners", "Farm/Wilderness"),
                "UndergroundMine" => ImmutableArray.Create("UndergroundMine"),

                // ADDED: DesertFestival inherits any fish configured globally for the Desert
                "Desert" => ImmutableArray.Create("Desert").AddRange(desertFest),

                _ => ImmutableArray.Create(locationName)
            };
        }

        private static Seasons ParseSeasons(string data)
        {
            var seasons = Seasons.None;
            var parts = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                if (Enum.TryParse<Seasons>(p, true, out var s))
                {
                    seasons |= s;
                }
                else if (p.Equals("spring", StringComparison.OrdinalIgnoreCase))
                {
                    seasons |= Seasons.Spring;
                }
                else if (p.Equals("summer", StringComparison.OrdinalIgnoreCase))
                {
                    seasons |= Seasons.Summer;
                }
                else if (p.Equals("fall", StringComparison.OrdinalIgnoreCase) || p.Equals("autumn", StringComparison.OrdinalIgnoreCase))
                {
                    seasons |= Seasons.Fall;
                }
                else if (p.Equals("winter", StringComparison.OrdinalIgnoreCase))
                {
                    seasons |= Seasons.Winter;
                }
            }
            return seasons == Seasons.None ? Seasons.All : seasons;
        }

        private static Weathers ParseWeathers(string data)
        {
            var w = Weathers.None;
            if (data.Contains("sunny", StringComparison.OrdinalIgnoreCase))
            {
                w |= Weathers.Sunny;
            }
            if (data.Contains("rainy", StringComparison.OrdinalIgnoreCase))
            {
                w |= Weathers.Rainy;
            }
            if (data.Contains("both", StringComparison.OrdinalIgnoreCase))
            {
                w = Weathers.All;
            }
            return w == Weathers.None ? Weathers.All : w;
        }

        private static FishAvailabilityInfo ParseConditionString(string condition, FishAvailabilityInfo baseInfo, string locationName)
        {
            var conditions = condition.Split(new[] { '/', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var newSeasons = Seasons.None;
            var newWeather = Weathers.None;
            var newLocations = new List<string>();
            var unparsedConditions = new Dictionary<string, string?>();
            int? newStart = null, newEnd = null, newLevel = null;

            foreach (var cond in conditions)
            {
                var parts = cond.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }
                var key = parts[0].ToUpperInvariant();
                switch (key)
                {
                    case "SEASON":
                    case "LOCATION_SEASON":
                        for (var i = 1; i < parts.Length; i++)
                        {
                            if (Enum.TryParse<Seasons>(parts[i], true, out var s))
                            {
                                newSeasons |= s;
                            }
                        }
                        break;
                    case "WEATHER":
                        // FIX: accept both short ("sun", "rain") and long ("sunny", "rainy") forms
                        // as well as "storm", "snow", "wind" which all map to rainy in TFO.
                        for (var i = 1; i < parts.Length; i++)
                        {
                            var token = parts[i].ToLowerInvariant();
                            if (token is "rain" or "rainy" or "storm" or "stormy" or "snow" or "snowy" or "wind" or "windy")
                            {
                                newWeather |= Weathers.Rainy;
                            }
                            else if (token is "sun" or "sunny")
                            {
                                newWeather |= Weathers.Sunny;
                            }
                            else if (token is "both" or "all")
                            {
                                newWeather = Weathers.All;
                            }
                        }
                        break;
                    case "TIME":
                        if (parts.Length >= 3 && int.TryParse(parts[1], out var sTime) && int.TryParse(parts[2], out var eTime))
                        {
                            newStart = sTime;
                            newEnd = eTime;
                        }
                        break;
                    case "FISHING_LEVEL":
                        if (parts.Length >= 2 && int.TryParse(parts[1], out var lvl))
                        {
                            newLevel = lvl;
                        }
                        break;
                    case "MINE_LEVEL":
                        if (parts.Length >= 2 && int.TryParse(parts[1], out var startLevel))
                        {
                            var endLevel = startLevel;
                            if (parts.Length >= 3 && int.TryParse(parts[2], out var parsedEnd))
                            {
                                endLevel = parsedEnd;
                            }
                            for (var i = startLevel; i <= endLevel; i++)
                            {
                                newLocations.Add($"{locationName}/{i}");
                            }
                        }
                        break;
                    default:
                        if (!cond.Contains("!PLAYER_HAS_CAUGHT_FISH"))
                        {
                            unparsedConditions[$"Query: {cond.Trim()}"] = "true";
                        }
                        break;
                }
            }
            return baseInfo with { Seasons = newSeasons != Seasons.None ? newSeasons : baseInfo.Seasons, Weathers = newWeather != Weathers.None ? newWeather : baseInfo.Weathers, StartTime = newStart ?? baseInfo.StartTime, EndTime = newEnd ?? baseInfo.EndTime, MinFishingLevel = newLevel ?? baseInfo.MinFishingLevel, IncludeLocations = newLocations.Any() ? newLocations.ToImmutableArray() : baseInfo.IncludeLocations, When = unparsedConditions.ToImmutableDictionary() };
        }
    }
}
