using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Coop.Model
{
    /// <summary>
    /// Format de **conservation** de l'inventaire d'un joueur sur le disque du serveur (v21).
    ///
    /// Pourquoi un fichier texte lisible plutôt qu'un binaire : un registre de vingt lignes doit pouvoir
    /// être relu par un humain qui cherche pourquoi un craft est refusé. Le format est donc une en-tête
    /// versionnée et des lignes `Nom\tQuantité`, triées, sans doublon.
    ///
    /// **Ce que ce stockage garantit** : un contenu relu est **exactement** celui qui a été écrit (mêmes
    /// bornes que l'instantané), et un fichier corrompu est **refusé** avec un motif — jamais interprété
    /// partiellement. Un fichier absent n'est pas une erreur : c'est le premier passage d'un joueur.
    ///
    /// **Ce qu'il ne garantit pas** : l'écriture atomique au sens strict (Windows peut laisser un
    /// `.tmp`), et la vérité du contenu — l'adoption initiale reste une déclaration du client, faute de
    /// pouvoir lire sa sauvegarde. Le serveur la conserve ensuite et fait foi pendant la session.
    /// </summary>
    public static class InventoryStore
    {
        public const string Header = "coop-inventory 1";

        public const string Extension = ".inv";

        /// <summary>Contenu trié, prêt pour l'écriture.</summary>
        public static string Serialize(InventoryEntry[] entries)
        {
            var sorted = entries ?? Array.Empty<InventoryEntry>();
            Array.Sort(sorted, (left, right) => string.CompareOrdinal(left.TechTypeName, right.TechTypeName));

            var builder = new StringBuilder();
            builder.Append(Header).Append('\n');

            foreach (var entry in sorted)
            {
                builder.Append(entry.TechTypeName)
                    .Append('\t')
                    .Append(entry.Count.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>
        /// Relit un contenu. Un fichier vide, une en-tête inconnue, une ligne mal formée, un doublon ou un
        /// dépassement de borne rendent `false` et un motif — jamais une liste partielle.
        /// </summary>
        public static bool TryParse(string text, out InventoryEntry[] entries, out string error)
        {
            entries = Array.Empty<InventoryEntry>();
            error = null;

            if (text == null)
            {
                error = "contenu absent";
                return false;
            }

            var lines = text.Replace("\r\n", "\n").Split('\n');

            if (lines.Length == 0 || !string.Equals(lines[0].Trim(), Header, StringComparison.Ordinal))
            {
                error = $"en-tête absente ou inconnue (attendu « {Header} »)";
                return false;
            }

            var parsed = new List<InventoryEntry>();
            string previous = null;

            for (var index = 1; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.Length == 0)
                {
                    continue;
                }

                var tab = line.IndexOf('\t');
                if (tab <= 0 || tab == line.Length - 1)
                {
                    error = $"ligne {index + 1} mal formée";
                    return false;
                }

                var name = line.Substring(0, tab);
                var countText = line.Substring(tab + 1);

                if (name.Length > Protocol.MaxTechTypeNameLength)
                {
                    error = $"ligne {index + 1} : nom trop long ({name.Length})";
                    return false;
                }

                if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                    count <= 0 ||
                    count > Protocol.MaxInventoryStack)
                {
                    error = $"ligne {index + 1} : quantité hors bornes ou illisible";
                    return false;
                }

                if (previous != null && string.CompareOrdinal(previous, name) >= 0)
                {
                    error = $"ligne {index + 1} : objets non triés ou dupliqués ('{previous}' puis '{name}')";
                    return false;
                }

                previous = name;
                parsed.Add(new InventoryEntry(name, count));
            }

            if (parsed.Count > Protocol.MaxInventoryEntries)
            {
                error = $"trop d'objets ({parsed.Count} > {Protocol.MaxInventoryEntries})";
                return false;
            }

            entries = parsed.ToArray();
            return true;
        }

        /// <summary>
        /// Lit le fichier s'il existe. <paramref name="missing"/> à `true` = premier passage, ce qui n'est
        /// **pas** une erreur : l'adoption est alors demandée au client.
        /// </summary>
        public static bool TryLoad(string path, out InventoryEntry[] entries, out string error, out bool missing)
        {
            entries = Array.Empty<InventoryEntry>();
            error = null;
            missing = false;

            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    missing = true;
                    return false;
                }

                return TryParse(File.ReadAllText(path), out entries, out error);
            }
            catch (Exception exception)
            {
                error = $"{exception.GetType().Name} : {exception.Message}";
                return false;
            }
        }

        /// <summary>
        /// Écrit le contenu via un fichier temporaire, puis le renomme. Le dossier est créé au besoin.
        /// </summary>
        public static bool Save(string path, InventoryEntry[] entries, out string error)
        {
            error = null;

            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temporary = path + ".tmp";
                File.WriteAllText(temporary, Serialize(entries), new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }

                return true;
            }
            catch (Exception exception)
            {
                error = $"{exception.GetType().Name} : {exception.Message}";
                return false;
            }
        }

        /// <summary>Chemin d'un registre : `&lt;racine&gt;/inventories/&lt;partie&gt;/&lt;pseudo&gt;.inv`.</summary>
        public static string PathFor(string root, string saveSlot, string username)
        {
            return Path.Combine(
                root ?? ".",
                "inventories",
                SanitizeForFile(saveSlot, "sans-partie"),
                SanitizeForFile(username, "sans-pseudo") + Extension);
        }

        /// <summary>
        /// Réduit une valeur venue du réseau à un nom de fichier sûr. Un pseudo est normalisé côté réseau,
        /// mais un nom de partie ou un pseudo tiers ne doit jamais pouvoir désigner `..\..\config`.
        /// </summary>
        public static string SanitizeForFile(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            var builder = new StringBuilder();
            foreach (var character in value)
            {
                var safe = (character >= 'a' && character <= 'z')
                           || (character >= 'A' && character <= 'Z')
                           || (character >= '0' && character <= '9')
                           || character == '-'
                           || character == '_'
                           || character == '.';

                builder.Append(safe ? character : '_');

                if (builder.Length >= 64)
                {
                    break;
                }
            }

            var result = builder.ToString();
            return result.Length == 0 || result == "." || result == ".." ? fallback : result;
        }
    }
}
