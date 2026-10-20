using BepInEx.Logging;
using Coop.Model;

namespace Coop.Shared
{
    /// <summary>
    /// Empreinte de la partie chargée par **cette** instance, publiée par `AutoLoadBehaviour` (P0).
    ///
    /// Pourquoi un point unique plutôt qu'un calcul dans chaque plugin : le client doit envoyer
    /// l'empreinte dans son handshake, et le serveur doit comparer. Les deux la lisent au même
    /// endroit — le menu du jeu — au même moment (juste avant `Load()`), donc les deux parlent de
    /// la même chose. Un calcul séparé dans chaque plugin aurait été deux occasions de diverger.
    ///
    /// L'empreinte vaut `null` tant que le menu n'a pas résolu la sauvegarde. Ce n'est pas une
    /// erreur : c'est un état nommé, que le client attend et que le serveur refuse explicitement.
    /// </summary>
    public static class SaveIdentitySource
    {
        private static readonly ManualLogSource Log = Logger.CreateLogSource("Coop Sauvegarde");

        /// <summary>Empreinte résolue, ou <c>null</c> tant que le menu n'a pas répondu.</summary>
        public static SaveIdentity Current { get; private set; }

        /// <summary>Rôle de l'instance au moment de la publication — journal seulement.</summary>
        public static LaunchMode Role { get; private set; }

        public static void Publish(SaveIdentity identity)
        {
            if (identity == null)
            {
                return;
            }

            Current = identity;
            Role = LaunchModeParser.Current;

            if (identity.IsUnknown)
            {
                Log.LogWarning("[monde] empreinte de sauvegarde publiée mais vide — elle sera refusée au handshake.");
                return;
            }

            Log.LogInfo($"[monde] empreinte de sauvegarde ({Role}) : {identity.Describe()}");
        }

        /// <summary>Efface l'empreinte (changement de partie, test).</summary>
        public static void Reset()
        {
            Current = null;
        }
    }
}
