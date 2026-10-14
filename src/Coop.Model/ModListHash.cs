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
    }
}
