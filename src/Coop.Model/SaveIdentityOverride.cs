using System;

namespace Coop.Model
{
    /// <summary>
    /// Champs de l'empreinte de sauvegarde **imposés** à l'instance, pour la mesure en jeu.
    ///
    /// P0 exige que chaque divergence soit refusée **nommément** : slot, session, mode, `changeSet`.
    /// Le slot se mesure avec `-coopsave` (on charge une autre sauvegarde) ; les trois autres vivent
    /// dans le même fichier, donc on ne peut pas les faire diverger sans toucher à la sauvegarde.
    /// Ces deux arguments disent au client **quoi annoncer** dans son handshake, alors qu'il a chargé
    /// la même partie que l'hôte : c'est un mensonge de sonde, pas un réglage de jeu.
    ///
    /// Ils ne changent rien au monde chargé et ne sont lus par aucun autre chemin. Une valeur vide ou
    /// invalide est **ignorée** (aucun override) : le harnais doit nommer ce qu'il impose, pas hériter
    /// d'une valeur d'environnement douteuse.
    /// </summary>
    public static class SaveIdentityOverride
    {
        public const string SessionArgumentName = "-coopsession";
        public const string ModeArgumentName = "-coopgamemode";

        /// <summary>Identifiant de session annoncé, ou <c>null</c> (aucun override).</summary>
        public static string Session => Read(Environment.GetCommandLineArgs(), SessionArgumentName);

        /// <summary>Mode de jeu annoncé, ou <c>null</c> (aucun override).</summary>
        public static string Mode => Read(Environment.GetCommandLineArgs(), ModeArgumentName);

        /// <summary>
        /// Applique les overrides à une empreinte réelle. Le hash est **recalculé** :
        /// `SaveCompatibility.Explain` refuse d'abord une empreinte altérée, et un override qui
        /// laisserait l'ancien hash ferait dire « hash incohérent » au lieu du motif qu'on mesure.
        /// </summary>
        public static SaveIdentity Apply(SaveIdentity identity) => Apply(identity, Session, Mode);

        /// <summary>Même chose avec des valeurs explicites (tests, outils).</summary>
        public static SaveIdentity Apply(SaveIdentity identity, string session, string mode)
        {
            if (identity == null)
            {
                return null;
            }

            if (session == null && mode == null)
            {
                return identity;
            }

            return SaveIdentity.Create(
                identity.Slot,
                session ?? identity.SessionId,
                identity.ChangeSet,
                mode ?? identity.GameMode);
            // Fin de l'application : le hash est recalculé par `Create`, jamais recopié.
        }

        /// <summary>
        /// Lecture d'un argument `-coop…=valeur`, nom exact (`-coopsessionevil=` appartient à un
        /// autre outil) et caractères sûrs : lettres, chiffres, `-` et `_`.
        /// </summary>
        public static string Read(string[] args, string argumentName)
        {
            if (args == null || string.IsNullOrEmpty(argumentName))
            {
                return null;
            }

            foreach (var arg in args)
            {
                if (arg == null || !arg.StartsWith(argumentName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (arg.Length <= argumentName.Length || arg[argumentName.Length] != '=')
                {
                    continue;
                }

                var value = arg.Substring(argumentName.Length + 1).Trim();
                return IsUsable(value) ? value : null;
            }

            return null;
        }

        public static bool IsUsable(string value)
        {
            if (string.IsNullOrEmpty(value)
                || value.Length > Protocol.MaxSaveSessionLength
                || value.Length > Protocol.MaxGameModeLength * 4)
            {
                // La borne la plus large est celle de la session (64) ; le mode est vérifié à
                // l'écriture du message, où sa propre borne s'applique. Refuser les valeurs démesurées
                // ici évite de faire échouer un handshake sur une faute de frappe du harnais.
                return false;
            }

            foreach (var character in value)
            {
                var allowed = (character >= 'a' && character <= 'z')
                              || (character >= 'A' && character <= 'Z')
                              || (character >= '0' && character <= '9')
                              || character == '-'
                              || character == '_';
                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
