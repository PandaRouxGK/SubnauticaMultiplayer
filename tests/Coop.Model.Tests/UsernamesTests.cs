using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class UsernamesTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\r\n")]
        public void Un_pseudo_vide_ou_blanc_devient_le_repli(string username)
        {
            Assert.Equal(Usernames.Fallback, Usernames.Sanitize(username));
        }

        [Fact]
        public void Les_espaces_de_bord_sont_retires()
        {
            Assert.Equal("killi", Usernames.Sanitize("  killi \t"));
        }

        [Fact]
        public void Les_caracteres_de_controle_sont_retires_pour_ne_pas_casser_un_journal()
        {
            // Sans ce retrait, le journal affichait « handshake ACCEPTÉ : 'a » puis une ligne « b'#2 ».
            Assert.Equal("ab", Usernames.Sanitize("a\nb"));
            Assert.Equal("killi", Usernames.Sanitize("kil\u0000li\u007f"));
        }

        [Fact]
        public void Un_pseudo_entierement_de_controle_devient_le_repli()
        {
            Assert.Equal(Usernames.Fallback, Usernames.Sanitize("\n\r\t"));
        }

        [Fact]
        public void Un_pseudo_trop_long_est_tronque_a_la_borne_du_protocole()
        {
            var username = new string('x', Protocol.MaxUsernameLength + 12);
            var sanitized = Usernames.Sanitize(username);

            Assert.Equal(Protocol.MaxUsernameLength, sanitized.Length);
            Assert.Equal(new string('x', Protocol.MaxUsernameLength), sanitized);
        }

        [Fact]
        public void La_troncature_ne_coupe_jamais_une_paire_de_surrogates()
        {
            // 31 caractères ASCII + un emoji (2 unités UTF-16) : la coupe à 32 tomberait au milieu
            // de la paire, et l'écriture stricte du handshake refuserait un surrogate isolé.
            var username = new string('x', Protocol.MaxUsernameLength - 1) + "\U0001F600";
            var sanitized = Usernames.Sanitize(username);

            Assert.True(sanitized.Length <= Protocol.MaxUsernameLength);
            Assert.Equal(new string('x', Protocol.MaxUsernameLength - 1), sanitized);

            // Le résultat doit rester encodable : c'est la propriété qui compte, pas la longueur exacte.
            var writer = new PacketWriter();
            writer.WriteString(sanitized);
            Assert.Equal(2 + sanitized.Length, writer.Length); // ASCII uniquement après la coupe
        }

        [Fact]
        public void Un_emoji_entier_passe_intact()
        {
            Assert.Equal("Élodie😀", Usernames.Sanitize("Élodie😀"));
        }
    }
}
