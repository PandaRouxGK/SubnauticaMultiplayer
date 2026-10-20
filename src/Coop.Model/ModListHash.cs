using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Coop.Model
{
    /// <summary>
    /// Empreinte déterministe de la liste des mods chargés. Deux instances qui n'ont pas le même
    /// contenu ne doivent pas se connecter : sinon le client interprète mal ce que le serveur envoie.
    /// </summary>
    public static class ModListHash
    {
        /// <summary>
        /// Calcule l'empreinte d'un ensemble d'identifiants de mods.
        /// Le tri est <see cref="StringComparer.Ordinal"/> : l'ordre de chargement ne doit pas
        /// changer le résultat, sinon deux joueurs identiques auraient des empreintes différentes.
        /// </summary>
        public static string Compute(IEnumerable<string> modIds)
        {
            var ordered = new List<string>(modIds ?? Array.Empty<string>());
            ordered.Sort(StringComparer.Ordinal);

            // Sans échappement, `["a\nb"]` et `["a", "b"]` produisent la même chaîne jointe : deux
            // listes de mods différentes auraient donc la même empreinte. On trie les identifiants
            // **bruts** (le hash des listes normales ne change pas), puis on échappe avant la jointure.
            for (var i = 0; i < ordered.Count; i++)
            {
                ordered[i] = Escape(ordered[i]);
            }

            var joined = string.Join("\n", ordered);
            using (var sha1 = SHA1.Create())
            {
                var digest = sha1.ComputeHash(Encoding.UTF8.GetBytes(joined));
                var builder = new StringBuilder(16);
                for (var i = 0; i < 8; i++)
                {
                    builder.Append(digest[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        /// <summary>
        /// Rend un identifiant injectif vis-à-vis du séparateur `\n` : l'antislash, le saut de ligne
        /// et le retour chariot sont échappés. Les identifiants usuels (noms de DLL) traversent
        /// inchangés, donc leurs empreintes restent identiques.
        /// </summary>
        internal static string Escape(string modId)
        {
            var value = modId ?? string.Empty;
            if (value.IndexOf('\\') < 0 && value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length + 8);
            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    default:
                        builder.Append(character);
                        break;
                }
            }

            return builder.ToString();
        }
    }
}
