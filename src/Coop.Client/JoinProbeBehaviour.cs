using System;
using System.Collections;
using System.Globalization;
using BepInEx.Logging;
using Coop.Model;
using UnityEngine;

namespace Coop.Client
{
    /// <summary>
    /// Sonde de connexion (`-coopdebug=join`) : elle **remplit les vrais champs** du panneau et appelle
    /// le **même** `Submit()` que le bouton.
    ///
    /// Pourquoi une sonde plutôt qu'une frappe simulée : simuler des événements clavier dans uGUI dépend
    /// du focus, de la disposition et du clavier du joueur — un test qui échoue pour ces raisons ne dit
    /// rien du mod. Ici, ce qui est mesuré est ce qui compte : une saisie invalide est **refusée sans
    /// connexion**, une saisie valide **écrit la configuration et connecte**, et la même règle sert les
    /// deux (aucune duplication du parseur).
    ///
    /// L'adresse à saisir vient de `-coopjoin=IP[:port]` : elle est distincte de `-coopserver` pour que le
    /// panneau soit réellement sollicité (avec `-coopserver`, l'adresse est déjà connue au démarrage).
    /// </summary>
    internal sealed class JoinProbeBehaviour : MonoBehaviour
    {
        // Préfixe explicite : `using UnityEngine` apporte aussi un `Logger`, la référence seule serait ambiguë.
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Join Probe");

        private const string JoinArgument = "-coopjoin";
        private const float TimeoutSeconds = 45f;

        /// <summary>Adresse volontairement invalide : un octet d'IPv4 hors 0..255.</summary>
        private const string InvalidAddress = "999.999.1.1";

        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + TimeoutSeconds;

            while (JoinPanelBehaviour.Current == null
                   || !JoinPanelBehaviour.Current.Ready
                   || ClientNetworkBehaviour.Current == null)
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    Fail("le panneau de connexion n'a jamais été construit (menu absent ?)");
                    yield break;
                }

                yield return new WaitForSeconds(0.5f);
            }

            var panel = JoinPanelBehaviour.Current;
            var client = ClientNetworkBehaviour.Current;

            // 1. Saisie invalide : refusée, et **surtout** aucune connexion tentée vers un repli.
            panel.AddressField.text = InvalidAddress;
            panel.PortField.text = Protocol.DefaultPort.ToString(CultureInfo.InvariantCulture);
            panel.Submit();

            var refused = !string.IsNullOrEmpty(panel.LastError) && !client.HasEndpoint;
            Log.LogInfo(
                $"[sonde] join : adresse invalide « {InvalidAddress} » refusée={refused} " +
                $"(raison=« {panel.LastError} »), endpoint vide={!client.HasEndpoint}");

            // 2. Saisie valide, par le même chemin : elle doit écrire la configuration et connecter.
            var requested = JoinValue();
            if (string.IsNullOrEmpty(requested))
            {
                Fail($"aucune adresse dans {JoinArgument}=IP[:port] — la saisie valide n'a pas pu être éprouvée");
                yield break;
            }

            if (!ServerAddressPreference.TryParse(requested, Protocol.DefaultPort, out var address, out var error))
            {
                Fail($"adresse de test illisible : « {requested} » — {error}");
                yield break;
            }

            panel.AddressField.text = address.Host;
            panel.PortField.text = address.Port.ToString(CultureInfo.InvariantCulture);
            panel.Submit();

            var acceptedDeadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!client.Accepted && Time.realtimeSinceStartup < acceptedDeadline)
            {
                yield return new WaitForSeconds(0.5f);
            }

            var accepted = client.Accepted;
            var stored = CoopClientPlugin.HostEntry != null ? CoopClientPlugin.HostEntry.Value : "(inconnu)";
            var verdict = refused && accepted;

            Log.LogInfo(
                $"[sonde] join : verdict={verdict} — adresse invalide refusée={refused}, " +
                $"adresse saisie={address}, connexion acceptée={accepted} " +
                $"(id={client.AssignedPlayerIdForProbe}), configuration enregistrée='{stored}'");

            if (!verdict)
            {
                Log.LogError(
                    "[sonde] join : verdict=False — " +
                    (refused ? "l'adresse valide n'a pas abouti à une session acceptée." : "une adresse invalide a été acceptée."));
            }
        }

        private static void Fail(string reason)
        {
            Log.LogError($"[sonde] join : verdict=False — {reason}");
        }

        /// <summary>`-coopjoin=IP[:port]` — nom exact, comme les autres arguments du projet.</summary>
        private static string JoinValue()
        {
            foreach (var argument in Environment.GetCommandLineArgs())
            {
                if (argument == null || !argument.StartsWith(JoinArgument, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (argument.Length <= JoinArgument.Length || argument[JoinArgument.Length] != '=')
                {
                    continue;
                }

                return argument.Substring(JoinArgument.Length + 1).Trim();
            }

            return null;
        }
    }
}
