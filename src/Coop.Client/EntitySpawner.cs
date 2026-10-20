using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Crée chez le client l'entité que le serveur vient de fabriquer (protocole v10, M6).
    ///
    /// Pourquoi ce composant : jusqu'ici, le serveur diffusait la position d'un véhicule que le client
    /// n'avait pas. Il en résultait une clé inconnue et, au mieux, un rattachement par proximité sur une
    /// entité qui n'existait pas. Un `spawn` côté serveur n'existe pas dans le monde du client, et aucun
    /// flux de positions ne peut le créer.
    ///
    /// Ce que le client fait de l'annonce, et pourquoi :
    ///
    /// 1. il instancie le prefab de l'espèce (`TechType`) — le nom d'espèce vient du message, le prefab de
    ///    la base de données du client ;
    /// 2. il **écrit l'identifiant du serveur** sur l'exemplaire créé (`UniqueIdentifier.Id`). Une entité
    ///    créée à l'exécution reçoit sinon un identifiant neuf, différent dans chaque instance : les clés
    ///    ne correspondraient jamais et le véhicule resterait immobile chez tout le monde sauf son
    ///    propriétaire ;
    /// 3. il le pose à la position annoncée, et le laisse au suivi normal (`RemoteVehicleManager`
    ///    l'apparie par sa clé comme n'importe quel autre).
    ///
    /// Ce qu'il ne fait pas, et qui est écrit : un échec d'instanciation est **compté et journalisé**, pas
    /// réessayé indéfiniment ; et une entité déjà présente (même clé) est ignorée plutôt que dupliquée.
    /// </summary>
    internal sealed class EntitySpawner : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Apparition");

        private static readonly Queue<EntitySpawnMessage> Pending = new Queue<EntitySpawnMessage>();
        private static readonly HashSet<long> Requested = new HashSet<long>();

        private static int _spawned;
        private static int _failed;
        private static int _skipped;
        private static int _badKey;
        private static long _lastSpawnedKey;
        private static string _lastSpawnedName = string.Empty;

        // — Contrat de contenu (v13, P1bis) ————————————————————————————————————————————
        // Une définition dont le hash ne se recalcule pas est refusée **avant** toute instanciation.
        // Le compteur existe pour que la sonde puisse échouer : un contrôle qui ne peut pas échouer
        // ne prouve rien (c'est la leçon de la preuve discriminante du §28).
        private static int _refusedDefinition;
        private static long _lastDeclaredHash;
        private static long _lastComputedHash;
        private static string _lastDefinitionSummary = string.Empty;
        private static EntitySpawnMessage _lastMessage;

        internal static int SpawnedCount => _spawned;
        internal static int FailedCount => _failed;
        internal static int SkippedCount => _skipped;
        internal static int BadKeyCount => _badKey;
        internal static long LastSpawnedKey => _lastSpawnedKey;
        internal static string LastSpawnedName => _lastSpawnedName;
        internal static int RefusedDefinitionCount => _refusedDefinition;
        internal static long LastDeclaredHash => _lastDeclaredHash;
        internal static long LastComputedHash => _lastComputedHash;
        internal static string LastDefinitionSummary => _lastDefinitionSummary;
        internal static EntitySpawnMessage LastMessage => _lastMessage;

        /// <summary>
        /// Vérifie une définition **par la même règle** que <see cref="Spawn"/>. Exposé pour que la
        /// sonde puisse falsifier une définition et constater le refus, au lieu de réimplémenter la
        /// règle (deux implémentations divergeraient, et la sonde validerait la mauvaise).
        /// </summary>
        internal static bool ProbeDefinitionContract(EntityDefinition definition, long declaredHash, out long computed)
            => EntityDefinition.Verify(definition, declaredHash, out computed);

        /// <summary>Reçoit une annonce du serveur (appelée par la boucle réseau, thread principal).</summary>
        internal static void Enqueue(EntitySpawnMessage msg)
        {
            if (msg == null || msg.Key == 0L)
            {
                return;
            }

            if (!Requested.Add(msg.Key))
            {
                // Annonce répétée (reconnexion, renvoi) : une seule création par clé.
                return;
            }

            Pending.Enqueue(msg);
        }

        private void Update()
        {
            // Rien n'est créé avant que le monde ne soit chargé. Une annonce arrive **dès la connexion**,
            // or le client se connecte au menu principal : l'objet fabriqué à ce moment-là vit dans la scène
            // du menu, que le chargement du monde décharge — mesuré à l'étape N, le second client créait
            // son Seamoth à 17:13:09 et ne le retrouvait plus trois secondes plus tard.
            if (!AutoLoadBehaviour.WorldLoaded)
            {
                return;
            }

            while (Pending.Count > 0)
            {
                StartCoroutine(Spawn(Pending.Dequeue()));
            }
        }

        private IEnumerator Spawn(EntitySpawnMessage msg)
        {
            // Course réelle : le serveur peut détruire l'entité avant que ce client n'ait chargé son monde.
            // L'annonce de disparition est alors arrivée la première ; recréer l'objet ici en ferait un
            // fantôme que plus rien ne retirerait.
            if (EntityDespawner.IsGone(msg.Key))
            {
                _skipped++;
                Log.LogInfo(
                    $"[spawn] entité 0x{msg.Key:x16} annoncée disparue avant sa création — aucune création " +
                    "(l'annonce de retrait l'a emporté).");
                yield break;
            }

            var duplicate = FindLocalVehicle(msg.Key);
            if (duplicate != null)
            {
                _skipped++;
                Log.LogInfo(
                    $"[spawn] entité 0x{msg.Key:x16} déjà présente localement ('{duplicate.gameObject.name}') — " +
                    "aucune création.");
                yield break;
            }

            // Le contrat de contenu : le client recalcule l'empreinte de la définition annoncée.
            // Un hash divergent n'est pas une raison de « réessayer » — c'est une définition qu'on
            // ne sait pas lire, et instancier quand même ferait entrer dans le monde une entité que
            // le serveur ne décrit pas. Refus nommé, compté, et avant toute allocation.
            var definition = msg.BuildDefinition();
            if (!EntityDefinition.Verify(definition, msg.DefinitionHash, out var computedHash))
            {
                _refusedDefinition++;
                _lastDeclaredHash = msg.DefinitionHash;
                _lastComputedHash = computedHash;
                _lastDefinitionSummary = definition.Canonical();
                Log.LogError(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "[spawn] définition REFUSÉE : hash annoncé=0x{0:x16} recalculé=0x{1:x16} pour " +
                        "'{2}' — entité non créée.",
                        msg.DefinitionHash,
                        computedHash,
                        msg.TechTypeName));
                yield break;
            }

            TechType tech;
            if (!Enum.TryParse(msg.TechTypeName, true, out tech) || tech == TechType.None)
            {
                _failed++;
                Log.LogError(
                    $"[spawn] espèce inconnue '{msg.TechTypeName}' (clé=0x{msg.Key:x16}) — entité non créée. " +
                    "Le client ne peut pas inventer un prefab.");
                yield break;
            }

            CoroutineTask<GameObject> request;
            try
            {
                request = CraftData.GetPrefabForTechTypeAsync(tech, false);
            }
            catch (Exception exception)
            {
                _failed++;
                Log.LogError($"[spawn] demande de prefab '{tech}' rejetée : {exception.GetType().Name} — {exception.Message}");
                yield break;
            }

            yield return request;

            var prefab = request.GetResult();
            if (prefab == null)
            {
                _failed++;
                Log.LogError($"[spawn] prefab introuvable pour '{tech}' — entité non créée.");
                yield break;
            }

            GameObject created;
            try
            {
                created = CraftData.InstantiateFromPrefab(prefab, tech, false);
            }
            catch (Exception exception)
            {
                _failed++;
                Log.LogError($"[spawn] instanciation de '{tech}' échouée : {exception.GetType().Name} — {exception.Message}");
                yield break;
            }

            if (created == null)
            {
                _failed++;
                Log.LogError($"[spawn] instanciation de '{tech}' sans objet — entité non créée.");
                yield break;
            }

            // L'identité du serveur, écrite sur l'exemplaire local : c'est elle qui rend les deux clés
            // égales, et donc le suivi possible.
            var identifier = created.GetComponentInChildren<UniqueIdentifier>();
            if (identifier != null && !string.IsNullOrEmpty(msg.UniqueId))
            {
                identifier.Id = msg.UniqueId;
            }

            created.transform.position = new Vector3(msg.X, msg.Y, msg.Z);
            created.transform.rotation = new Quaternion(msg.RotX, msg.RotY, msg.RotZ, msg.RotW);

            var vehicle = created.GetComponent<Vehicle>();
            var localKey = vehicle != null ? VehicleIdentity.KeyOf(vehicle, out _) : 0L;

            if (localKey != msg.Key)
            {
                _badKey++;
                Log.LogError(
                    $"[spawn] clé locale 0x{localKey:x16} ≠ clé annoncée 0x{msg.Key:x16} pour " +
                    $"'{created.name}' — le suivi ne pourra pas l'apparier (identifiant écrit : " +
                    $"{(identifier != null ? "oui" : "NON")}).");
            }

            _spawned++;
            _lastSpawnedKey = msg.Key;
            _lastSpawnedName = created.name;
            _lastMessage = msg;
            _lastDeclaredHash = msg.DefinitionHash;
            _lastComputedHash = computedHash;
            _lastDefinitionSummary = definition.Canonical();

            Log.LogInfo(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "[spawn] entité créée : clé=0x{0:x16} '{1}' ({2}) pos=({3:F1}, {4:F1}, {5:F1}) — " +
                    "identifiant du serveur '{6}' écrit ({7}), clé locale 0x{8:x16}, créées={9}",
                    msg.Key,
                    created.name,
                    tech,
                    msg.X,
                    msg.Y,
                    msg.Z,
                    msg.UniqueId,
                    identifier != null ? "oui" : "non",
                    localKey,
                    _spawned));
        }

        /// <summary>Véhicule local portant déjà cette clé, s'il existe.</summary>
        private static Vehicle FindLocalVehicle(long key)
        {
            var all = UnityEngine.Object.FindObjectsOfType<Vehicle>();
            foreach (var candidate in all)
            {
                if (candidate == null || candidate.gameObject == null || !candidate.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (VehicleIdentity.KeyOf(candidate, out _) == key)
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
