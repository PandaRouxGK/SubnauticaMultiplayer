using System;

namespace Coop.Model
{
    /// <summary>
    /// Sauvegarde **imposée** à l'instance, par la ligne de commande ou l'environnement.
    ///
    /// Pourquoi ce réglage existe : le harnais de vérification doit pouvoir lancer un client sur une
    /// **autre** sauvegarde que l'hôte pour éprouver le refus P0. Sans ce choix explicite, le menu
    /// charge la première sauvegarde venue et les deux instances parlent forcément de la même partie —
    /// le refus « deux sauvegardes différentes » n'était alors prouvé qu'hors jeu.
    ///
    /// Une valeur **explicitement invalide** ne retombe pas sur le comportement par défaut : charger
    /// silencieusement une autre partie ferait croire à une mesure alors que la mauvaise sauvegarde
    /// aurait été ouverte. L'appelant reçoit <c>null</c> et doit le dire.
    /// </summary>
    public static class SaveSlotPreference
    {
        public const string ArgumentName = "-coopsave";
        public const string EnvironmentVariableName = "COOP_SAVE_SLOT";

        /// <summary>Nom de sauvegarde demandé, ou <c>null</c> si aucune préférence exploitable.</summary>
        public static string Parse(string[] args)
        {
            if (args != null)
            {
                foreach (var arg in args)
                {
                    if (arg == null || !arg.StartsWith(ArgumentName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // Nom exact, comme `-coopmode` : `-coopsaveevil=x` appartient à un autre outil.
                    if (arg.Length <= ArgumentName.Length || arg[ArgumentName.Length] != '=')
                    {
                        continue;
                    }

                    var value = arg.Substring(ArgumentName.Length + 1).Trim();

                    // Une préférence explicite mais invalide est un **refus**, pas un repli : on ne
                    // charge pas une autre partie à la place de celle qui a été demandée.
                    return IsUsable(value) ? value : null;
                }
            }

            var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariableName);
            var trimmed = fromEnvironment?.Trim();
            return IsUsable(trimmed) ? trimmed : null;
        }

        public static string Current => Parse(Environment.GetCommandLineArgs());

        /// <summary>
        /// Un nom de sauvegarde lisible et sûr : lettres, chiffres, `-` et `_`, dans la borne du
        /// protocole. Subnautica nomme ses dossiers `slot0000`, `slot0001`, … ; refuser le reste évite
        /// qu'une valeur d'environnement fasse sortir du dossier `SavedGames` (séparateurs, `..`).
        /// </summary>
        public static bool IsUsable(string slot)
        {
            if (string.IsNullOrEmpty(slot) || slot.Length > Protocol.MaxSaveSlotLength)
            {
                return false;
            }

            foreach (var character in slot)
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
