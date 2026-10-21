using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// P4/réseau distant : une adresse saisie par un joueur doit être validée **avant** d'être utilisée,
    /// et une faute de frappe doit être nommée plutôt que remplacée par la boucle locale. Ces cas sont
    /// hors jeu : le panneau et `Connect()` ne font qu'appeler cette règle.
    /// </summary>
    public sealed class ServerAddressTests
    {
        private static ServerAddress Parse(string raw) =>
            ServerAddressPreference.TryParse(raw, Protocol.DefaultPort, out var address, out var error)
                ? address
                : throw new Xunit.Sdk.XunitException($"adresse refusée alors qu'elle devait passer : {error}");

        private static string Refusal(string raw)
        {
            Assert.False(
                ServerAddressPreference.TryParse(raw, Protocol.DefaultPort, out _, out var error),
                $"« {raw} » aurait dû être refusée");
            Assert.False(string.IsNullOrWhiteSpace(error));
            return error;
        }

        [Fact]
        public void Une_ipv4_avec_port_donne_l_hote_et_le_port()
        {
            var address = Parse("203.0.113.10:7800");
            Assert.Equal("203.0.113.10", address.Host);
            Assert.Equal((ushort)7800, address.Port);
            Assert.Equal("203.0.113.10:7800", address.ToString());
        }

        [Fact]
        public void Un_hote_seul_prend_le_port_par_defaut()
        {
            var address = Parse("203.0.113.10");
            Assert.Equal((ushort)Protocol.DefaultPort, address.Port);
        }

        [Fact]
        public void Les_espaces_autour_sont_ignores()
        {
            var address = Parse("   serveur.exemple.fr:7777   ");
            Assert.Equal("serveur.exemple.fr", address.Host);
            Assert.Equal((ushort)7777, address.Port);
        }

        [Fact]
        public void Un_nom_d_hote_avec_tirets_et_points_passe()
        {
            var address = Parse("mon-serveur.exemple.fr");
            Assert.Equal("mon-serveur.exemple.fr", address.Host);
        }

        [Fact]
        public void Une_adresse_vide_est_refusee_et_nommee()
        {
            Assert.Contains("vide", Refusal("   "));
        }

        [Fact]
        public void Un_port_hors_bornes_est_refuse_en_le_nommant()
        {
            Assert.Contains("port invalide", Refusal("203.0.113.10:0"));
            Assert.Contains("port invalide", Refusal("203.0.113.10:65536"));
            Assert.Contains("port invalide", Refusal("203.0.113.10:-1"));
            Assert.Contains("port invalide", Refusal("203.0.113.10:abc"));
        }

        [Fact]
        public void Un_port_manquant_apres_les_deux_points_est_refuse()
        {
            Assert.Contains("port manquant", Refusal("203.0.113.10:"));
        }

        [Fact]
        public void Une_ipv4_dont_un_octet_depasse_255_est_refusee()
        {
            Assert.Contains("octet hors 0..255", Refusal("999.999.1.1:7777"));
            Assert.Contains("4 octets", Refusal("203.0.113:7777"));
        }

        [Fact]
        public void Un_nom_d_hote_avec_espace_ou_underscore_est_refuse()
        {
            Assert.Contains("nom d'hôte invalide", Refusal("mon serveur.fr"));
            Assert.Contains("nom d'hôte invalide", Refusal("mon_serveur.fr"));
        }

        [Fact]
        public void Une_ipv6_est_refusee_en_le_nommant()
        {
            Assert.Contains("IPv6", Refusal("[::1]:7777"));
        }

        [Fact]
        public void Un_schema_d_url_est_refuse_en_le_nommant()
        {
            Assert.Contains("sans schéma", Refusal("udp://203.0.113.10:7777"));
        }

        [Fact]
        public void L_argument_exact_est_lu_et_un_prefixe_voisin_est_ignore()
        {
            Assert.Equal("203.0.113.10:7777", ServerAddressPreference.Raw(new[] { "-coopmode=client", "-coopserver=203.0.113.10:7777" }));
            Assert.Null(ServerAddressPreference.Raw(new[] { "-coopserverevil=203.0.113.10" }));
            Assert.Null(ServerAddressPreference.Raw(new[] { "-coopmode=client" }));
        }

        [Fact]
        public void L_argument_est_normalise_puis_valide_comme_une_saisie()
        {
            var raw = ServerAddressPreference.Raw(new[] { "-COOPSERVER=  serveur.exemple.fr  " });
            Assert.Equal("serveur.exemple.fr", raw);
            Assert.True(ServerAddressPreference.TryParse(raw, Protocol.DefaultPort, out var address, out _));
            Assert.Equal((ushort)Protocol.DefaultPort, address.Port);
        }
    }
}
