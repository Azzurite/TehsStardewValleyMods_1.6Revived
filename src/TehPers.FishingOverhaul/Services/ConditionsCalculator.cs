using ContentPatcher;
using StardewModdingAPI;
using StardewValley;
using System;
using System.Collections.Generic;
using System.Linq;
using TehPers.FishingOverhaul.Api;
using TehPers.FishingOverhaul.Api.Content;

namespace TehPers.FishingOverhaul.Services
{
    internal class ConditionsCalculator
    {
        private const string contentPatcherId = "Pathoschild.ContentPatcher";

        private readonly AvailabilityConditions conditions;
        private readonly IManagedConditions? managedConditions;

        // Liste pour stocker les requêtes natives Stardew 1.6 (ex: CATCHING_FRENZY_FISH)
        private readonly List<string> nativeQueries = new();

        public ConditionsCalculator(
            IMonitor monitor,
            IContentPatcherAPI contentPatcherApi,
            IManifest fishingManifest,
            IManifest owner,
            AvailabilityConditions conditions
        )
        {
            _ = monitor ?? throw new ArgumentNullException(nameof(monitor));
            _ = contentPatcherApi ?? throw new ArgumentNullException(nameof(contentPatcherApi));
            _ = owner ?? throw new ArgumentNullException(nameof(owner));
            this.conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));

            // LOGIQUE DE SÉPARATION :
            // 1. Conditions CP (Saison, Météo, etc.) -> Envoyées à Content Patcher
            // 2. Requêtes Natives (Query: ...) -> Gardées pour GameStateQuery.CheckConditions

            // CORRECTION CS8620/CS8604 : Le dictionnaire doit accepter des valeurs nulles (string?)
            var cpConditions = new Dictionary<string, string?>();

            foreach (var condition in conditions.When)
            {
                // Si la condition commence par "Query:", c'est une chaîne native Stardew 1.6.
                if (condition.Key.StartsWith("Query:", StringComparison.OrdinalIgnoreCase))
                {
                    // CORRECTION IDE0057 : Utilisation de la syntaxe de plage [6..] au lieu de Substring(6)
                    var query = condition.Key[6..].Trim();
                    this.nativeQueries.Add(query);
                }
                else
                {
                    // C'est un token standard, on l'envoie à CP
                    cpConditions.Add(condition.Key, condition.Value);
                }
            }

            // Vérifier s'il y a des conditions CP à traiter
            if (!cpConditions.Any())
            {
                return;
            }

            // Récupération de la version de CP
            var version =
                owner.Dependencies.FirstOrDefault(
                        dependency => string.Equals(
                            dependency.UniqueID,
                            ConditionsCalculator.contentPatcherId,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    ?.MinimumVersion
                ?? fishingManifest.Dependencies.FirstOrDefault(
                        dependency => string.Equals(
                            dependency.UniqueID,
                            ConditionsCalculator.contentPatcherId,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    ?.MinimumVersion
                ?? throw new ArgumentException(
                    "TFO does not depend on Content Patcher",
                    nameof(fishingManifest)
                );

            // Parse conditions (On ne passe que les conditions CP "safe")
            this.managedConditions = contentPatcherApi.ParseConditions(
                owner,
                cpConditions,
                version,
                new[] { fishingManifest.UniqueID }
            );

            // Vérification de la validité
            if (!this.managedConditions.IsValid)
            {
                var conditionKeys = string.Join(", ", cpConditions.Keys);
                monitor.Log(
                    $"Failed to parse conditions for one of {owner.UniqueID}'s entries: {this.managedConditions.ValidationError}" +
                    $" (affected When keys: [{conditionKeys}])" +
                    $" -- If the condition uses GameStateQuery syntax (e.g. DAY_OF_MONTH, PLAYER_HAS_FLAG), prefix the key with 'Query: ' in the content.json.",
                    LogLevel.Error
                );
            }
        }

        /// <summary>
        /// Calculates whether the entry is available.
        /// </summary>
        /// <param name="fishingInfo">The fishing info to calculate the availability for.</param>
        /// <returns>Whether the entry is available.</returns>
        public bool IsAvailable(FishingInfo fishingInfo)
        {
            // 1. Vérifier les conditions de base TFO (Heure, Niveau, etc.)
            if (!this.conditions.IsAvailable(fishingInfo))
            {
                return false;
            }

            // 2. Vérifier les conditions Content Patcher
            if (this.managedConditions is { } managedConditions)
            {
                managedConditions.UpdateContext();
                if (!managedConditions.IsMatch)
                {
                    return false;
                }
            }

            // 3. Vérifier les requêtes natives Stardew 1.6 (Nouvelle logique)
            // Cela gère CATCHING_FRENZY_FISH et d'autres flags natifs
            foreach (var query in this.nativeQueries)
            {
                // Contexte : Le lieu actuel et le joueur sont requis pour les requêtes de pêche.
                if (!GameStateQuery.CheckConditions(query, fishingInfo.User.currentLocation, fishingInfo.User))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
