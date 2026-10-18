using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using Coop.Shared;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Conduite autoritaire (M4, protocole v9) : le client qui pilote envoie la position de **son**
    /// véhicule, et le serveur l'applique à son exemplaire du même véhicule puis la rediffuse.
    ///
    /// Pourquoi c'est le client qui parle : le véhicule est conduit par un joueur, avec les entrées du
    /// joueur et la physique locale du jeu. Faire simuler le Seamoth par le serveur demanderait de
    /// transporter les entrées (gaz, barre, tangage) et de rejouer le moteur du jeu côté serveur — hors de
    /// portée, et sans bénéfice visible. Ce que le serveur garde, c'est le **droit** : un seul conducteur
    /// par véhicule, une portée, et un refus opposable.
    ///
    /// Cadence : `Protocol.VehicleControlIntervalMs` (10 Hz), la même que la diffusion des véhicules —
    /// envoyer plus vite serait perdu à la rediffusion.
    ///
    /// Ce que cette boucle ne couvre pas : un client qui **contourne** ce code (patch désactivé) n'envoie
    /// rien, et son véhicule reste immobile chez les autres ; c'est visible côté serveur (aucun état) et
    /// côté client (`conduits localement` dans `RemoteVehicleManager`), mais ce n'est pas empêché.
    /// </summary>
    internal sealed class VehicleDriverBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Conduite");

        /// <summary>
        /// Véhicule imposé par une sonde : il emprunte **la même** boucle d'envoi, seule la source du
        /// véhicule change (`Player.main.currentMountedVehicle` → ce champ). Ce n'est pas un raccourci de
        /// test : c'est la seule façon d'exercer l'envoi sans piloter pour de vrai dans un run automatisé.
        /// </summary>
        internal static Vehicle ProbeTarget;

        private static int _sent;
        private static int _zeroKey;
        private static int _granted;
        private static int _refused;

        internal static int SentCount => _sent;
        internal static int ZeroKeyCount => _zeroKey;
        internal static int GrantedCount => _granted;
        internal static int RefusedCount => _refused;

        private float _nextSendAt;
        private long _lastKey;

        private void Update()
        {
            var network = ClientNetworkBehaviour.Current;
            if (network == null)
            {
                return;
            }

            var vehicle = ProbeTarget != null
                ? ProbeTarget
                : (Player.main != null ? Player.main.currentMountedVehicle : null);

            if (vehicle == null)
            {
                if (_lastKey != 0L)
                {
                    var released = _lastKey;
                    _lastKey = 0L;

                    // v12 : le volant revient **tout de suite**. Sans ce message, le véhicule restait
                    // attribué à ce joueur jusqu'à sa déconnexion, et aucun autre ne pouvait le reprendre.
                    var sent = network.SendVehicleRelease(released);
                    Log.LogInfo(
                        $"[conduite] véhicule quitté — l'envoi s'arrête, libération envoyée={sent} " +
                        $"(clé=0x{released:x16}).");
                }

                return;
            }

            if (Time.realtimeSinceStartup < _nextSendAt)
            {
                return;
            }

            _nextSendAt = Time.realtimeSinceStartup + Protocol.VehicleControlIntervalMs / 1000f;

            var key = VehicleIdentity.KeyOf(vehicle, out var mechanism);
            if (key == 0L)
            {
                _zeroKey++;
                return;
            }

            if (key != _lastKey)
            {
                // Changement direct d'un véhicule à un autre : l'ancien serait resté attribué.
                if (_lastKey != 0L)
                {
                    network.SendVehicleRelease(_lastKey);
                    Log.LogInfo($"[conduite] véhicule précédent libéré avant la prise en main : clé=0x{_lastKey:x16}.");
                }

                _lastKey = key;
                Log.LogInfo($"[conduite] prise en main : clé=0x{key:x16} ({mechanism}) — envoi à 10 Hz.");
            }

            var transform = vehicle.transform;
            if (transform == null)
            {
                return;
            }

            var body = vehicle.GetComponent<Rigidbody>();
            var speed = body != null && !body.isKinematic ? body.velocity : Vector3.zero;

            if (network.SendVehicleControl(key, transform.position, transform.rotation, speed))
            {
                _sent++;
            }
        }

        /// <summary>Décision reçue du serveur : c'est elle qui fait autorité, pas l'optimisme du client.</summary>
        internal static void OnControlResult(VehicleControlResultMessage result)
        {
            if (result == null)
            {
                return;
            }

            if (result.Granted)
            {
                _granted++;
                if (_granted == 1)
                {
                    Log.LogInfo(
                        $"[conduite] accordée sur 0x{result.VehicleKey:x16} — le serveur applique cet état " +
                        "et le rediffuse aux autres joueurs.");
                }

                return;
            }

            _refused++;
            if (_refused == 1)
            {
                Log.LogWarning(
                    $"[conduite] REFUSÉE sur 0x{result.VehicleKey:x16} — conduite par l'id " +
                    $"{result.DriverId.ToString(CultureInfo.InvariantCulture)}. Ce client n'est pas le " +
                    "conducteur : le serveur ignore ses états.");
            }
        }
    }
}
