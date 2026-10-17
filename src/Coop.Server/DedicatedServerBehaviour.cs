using System.Collections;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Coop.Server
{
    /// <summary>
    /// Serveur **dédié** (`-batchmode`) : il n'affiche aucune fenêtre, ne diffuse aucun personnage joueur et
    /// ne peut pas être piloté.
    ///
    /// Ce qui a été essayé et **rejeté**, avec sa mesure : désactiver le GameObject du joueur
    /// (`player.gameObject.SetActive(false)`). Le joueur disparaît bien de `FindObjectOfType`, mais une
    /// trentaine de systèmes du jeu continuent de passer par la référence statique `Player.main` (non nulle)
    /// et lèvent des `NullReferenceException` à chaque frame — mesuré sur une instance `-batchmode` :
    /// **318 395 erreurs en 28 s** (`uGUI_Pings.UpdatePings`, `DisableSkyBox.Update`,
    /// `EcoRegionManager.UpdateCameraRegion`, …), journal de 81 Mo, et le serveur ne répond plus assez vite
    /// pour que le contrôle du launcher aboutisse. Un serveur qui écrit 11 000 erreurs par seconde n'est pas
    /// un serveur : c'est un jeu cassé qui garde un port ouvert.
    ///
    /// Ce qui est donc fait, et pourquoi ça suffit à la demande : le joueur local **reste simulé** (Subnautica
    /// l'exige — le charger, sauvegarder et faire tourner le monde passent par lui), mais il n'est
    ///  - **ni affiché** (aucune fenêtre : `-batchmode`), 
    ///  - **ni piloté** (aucune entrée : pas de fenêtre, pas de focus),
    ///  - **ni diffusé** (`ServerNetwork` ne construit plus l'état id=9999 en mode dédié).
    ///
    /// Ce composant ne fait donc qu'une chose : **constater et dire** cet état, pour qu'on ne confonde pas
    /// « aucun client ne voit d'avatar » avec « le joueur n'existe pas dans la sauvegarde ». Il relève aussi
    /// la position du joueur, utile quand un client se demande d'où viennent les créatures.
    /// </summary>
    public class DedicatedServerBehaviour : MonoBehaviour
    {
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("Coop Dédié");

        private const float WorldTimeoutSeconds = 240f;

        private IEnumerator Start()
        {
            var deadline = Time.realtimeSinceStartup + WorldTimeoutSeconds;
            while (SceneManager.GetActiveScene().name != "Main" && Time.realtimeSinceStartup < deadline)
            {
                yield return new WaitForSecondsRealtime(2f);
            }

            if (SceneManager.GetActiveScene().name != "Main")
            {
                Log.LogError(
                    $"[dédié] verdict=False — le monde n'est pas chargé après {WorldTimeoutSeconds:F0}s : " +
                    "le serveur n'a rien à simuler.");
                yield break;
            }

            // Le chargement de la sauvegarde crée d'abord un joueur à l'origine de la scène, puis le place à
            // sa position sauvegardée **~10 s plus tard** (mesuré). Attendre un délai fixe évite de décrire
            // la position provisoire comme si c'était la bonne.
            const float PlayerSettleSeconds = 15f;
            yield return new WaitForSecondsRealtime(PlayerSettleSeconds);

            var player = FindObjectOfType<Player>();
            if (player == null || player.transform == null)
            {
                Log.LogWarning(
                    "[dédié] aucun composant Player détecté après le chargement : le serveur n'a aucun " +
                    "personnage à exposer (rien à diffuser, rien à piloter).");
                yield break;
            }

            var position = player.transform.position;
            Log.LogWarning(
                $"[dédié] serveur sans personnage exposé — joueur local à {position.ToString("F1")}, " +
                "fenêtre=aucune (batchmode), état diffusé=aucun (id 9999 désactivé), entrées=aucune. " +
                "Le joueur reste simulé : Subnautica en a besoin pour charger, sauvegarder et faire vivre le monde.");
        }
    }
}
