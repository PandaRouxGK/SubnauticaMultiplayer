using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Server
{
    /// <summary>
    /// Fabrique **côté serveur** l'entité qu'un client a demandée (v16, M4/M6).
    ///
    /// Pourquoi un composant et pas une méthode statique : le prefab d'un véhicule se charge de façon
    /// **asynchrone** (`CraftData.GetPrefabForTechTypeAsync`), et un `ServerNetwork` n'est pas un
    /// `MonoBehaviour` — il ne peut pas porter la coroutine. La fabrique reçoit donc les demandes dans une
    /// file et rend la main par un callback, une fois l'objet réellement dans le monde.
    ///
    /// Ce qu'elle garantit, et pourquoi :
    ///  - un identifiant **neuf et unique** est écrit sur l'objet (`UniqueIdentifier.Id`) : sans lui, la
    ///    clé d'un véhicule fabriqué à l'exécution repose sur sa position d'apparition, et un client qui
    ///    arrive une seconde plus tard en calcule une autre (limite connue du repli, `VehicleIdentity`) ;
    ///  - la clé est relue **après** la pose de l'identifiant et de la position, et une clé nulle est un
    ///    échec nommé — jamais une annonce d'entité impossible à apparier ;
    ///  - un échec détruit l'objet partiel : une entité que personne ne peut désigner resterait dans le
    ///    monde du serveur sans être annoncée.
    ///
    /// Ce qu'elle ne décide pas : **si** la demande est acceptable. La liste blanche, la portée, la
    /// cadence et le plafond vivent dans `ServerNetwork.Spawns.cs`, au même endroit que les autres
    /// décisions autoritaires.
    /// </summary>
    internal sealed class ServerEntityFactory : MonoBehaviour
    {
        // Préfixe explicite : `using UnityEngine` apporte aussi un `Logger`, la référence seule serait ambiguë.
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Fabrique");

        internal static ServerEntityFactory Current { get; private set; }

        private readonly Queue<Job> _jobs = new Queue<Job>();
        private bool _busy;

        private sealed class Job
        {
            public TechType Tech;
            public Vector3 Position;
            public Quaternion Rotation;
            public Action<long, string> Completed;
        }

        private void Awake()
        {
            Current = this;
        }

        private void OnDestroy()
        {
            if (Current == this)
            {
                Current = null;
            }
        }

        /// <summary>
        /// Met une fabrication en file. <paramref name="completed"/> est appelé avec la clé, ou avec
        /// `0` et un motif nommé. Le callback est invoqué sur le thread principal, depuis `Update`.
        /// </summary>
        internal void Request(TechType tech, Vector3 position, Quaternion rotation, Action<long, string> completed)
        {
            _jobs.Enqueue(new Job
            {
                Tech = tech,
                Position = position,
                Rotation = rotation,
                Completed = completed,
            });
        }

        private void Update()
        {
            if (_busy || _jobs.Count == 0)
            {
                return;
            }

            StartCoroutine(Spawn(_jobs.Dequeue()));
        }

        private IEnumerator Spawn(Job job)
        {
            _busy = true;

            string failure = null;
            CoroutineTask<GameObject> request = null;

            try
            {
                request = CraftData.GetPrefabForTechTypeAsync(job.Tech, false);
            }
            catch (Exception exception)
            {
                failure = $"prefab '{job.Tech}' illisible ({exception.GetType().Name})";
            }

            GameObject prefab = null;
            if (failure == null)
            {
                yield return request;
                prefab = request != null ? request.GetResult() : null;
                if (prefab == null)
                {
                    failure = $"prefab '{job.Tech}' introuvable";
                }
            }

            GameObject created = null;
            long key = 0L;
            string mechanism = VehicleIdentity.MechanismNone;

            if (failure == null)
            {
                try
                {
                    created = CraftData.InstantiateFromPrefab(prefab, job.Tech, false);
                }
                catch (Exception exception)
                {
                    failure = $"instanciation '{job.Tech}' : {exception.GetType().Name}";
                }

                if (created == null && failure == null)
                {
                    failure = $"instanciation '{job.Tech}' sans objet";
                }
            }

            if (created != null)
            {
                var identifier = created.GetComponentInChildren<UniqueIdentifier>();
                if (identifier == null)
                {
                    // Fabriqué à l'exécution : le prefab n'en porte pas toujours. En poser un rend la clé
                    // stable pour tous (le client écrit cet id sur sa copie à l'annonce).
                    identifier = created.AddComponent<UniqueIdentifier>();
                }

                if (identifier != null)
                {
                    identifier.Id = Guid.NewGuid().ToString("D");
                }

                created.transform.position = job.Position;
                created.transform.rotation = job.Rotation;

                var vehicle = created.GetComponent<Vehicle>();
                if (vehicle == null)
                {
                    failure = $"'{job.Tech}' n'est pas un Vehicle";
                }
                else
                {
                    key = VehicleIdentity.KeyOf(vehicle, out mechanism);
                    if (key == 0L)
                    {
                        failure = "entité sans clé partageable";
                    }
                }
            }

            if (failure != null && created != null)
            {
                // Une entité que personne ne peut désigner ne doit pas rester dans le monde du serveur.
                Destroy(created);
            }

            if (failure == null)
            {
                Log.LogInfo(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "[net] fabrication : '{0}' créé à ({1:F1}, {2:F1}, {3:F1}) — clé=0x{4:x16} ({5}), " +
                        "identifiant '{6}'.",
                        job.Tech,
                        job.Position.x,
                        job.Position.y,
                        job.Position.z,
                        key,
                        mechanism,
                        created != null && created.GetComponentInChildren<UniqueIdentifier>() != null
                            ? created.GetComponentInChildren<UniqueIdentifier>().Id
                            : "(aucun)"));
            }
            else
            {
                Log.LogWarning($"[net] fabrication REFUSÉE : '{job.Tech}' — {failure}.");
            }

            var completed = job.Completed;
            _busy = false;
            completed?.Invoke(failure == null ? key : 0L, failure);
        }
    }
}
