using System;
using System.Text;

namespace Coop.Model
{
    /// <summary>
    /// Normalisation du pseudo d'un joueur, partagée par le client et le serveur.
    ///
    /// Pourquoi ce n'est pas une simple `Trim` côté client : le pseudo voyage dans le handshake, dans
    /// `PlayerJoined`/`PlayerLeft`, dans chaque journal et dans le nom d'objet du clone d'avatar. Un
    /// pseudo contenant un saut de ligne casse une ligne de journal (« handshake ACCEPTÉ : 'a » puis
    /// « b'#2 »), et un pseudo trop long faisait **refuser** le handshake au lieu de se connecter.
    ///
    /// Règles, dans cet ordre :
    /// 1. `null`, vide ou blanc → <see cref="Fallback"/> ;
    /// 2. `Trim`, puis retrait des caractères de contrôle (U+0000–U+001F, U+007F) ;
    /// 3. si tout a été retiré → <see cref="Fallback"/> ;
    /// 4. troncature à <see cref="Protocol.MaxUsernameLength"/>, sans couper une paire de surrogates :
    ///    la moitié d'un emoji n'est pas encodable en UTF-8 et l'écriture stricte la refuserait.
    ///
    /// Le nom n'est **pas** une identité : le serveur attribue `PlayerId` et force ce champ, comme il
    /// force la couleur. Changer un pseudo pour le rendre affichable ne change donc aucun droit.
    /// </summary>
    public static class Usernames
    {
        /// <summary>Nom utilisé quand l'utilisateur n'en fournit aucun d'exploitable.</summary>
        public const string Fallback = "joueur";

        public static string Sanitize(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return Fallback;
            }

            var trimmed = username.Trim();
            var cleaned = RemoveControlCharacters(trimmed);
            if (cleaned.Length == 0)
            {
                return Fallback;
            }

            return Truncate(cleaned, Protocol.MaxUsernameLength);
        }

        /// <summary>
        /// Coupe à <paramref name="maxLength"/> caractères sans séparer une paire de surrogates.
        /// Une coupe au milieu d'un emoji laisserait un surrogate isolé, que <see cref="PacketWriter"/>
        /// refuse désormais à l'écriture : le handshake échouerait pour une raison incompréhensible.
        /// </summary>
        public static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            var cut = maxLength < 0 ? 0 : maxLength;
            if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
            {
                cut--;
            }

            return value.Substring(0, cut);
        }

        private static string RemoveControlCharacters(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                if (!char.IsControl(character))
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }
    }
}
