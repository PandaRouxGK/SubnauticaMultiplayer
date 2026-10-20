using System;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    public sealed class ModListHashTests
    {
        [Fact]
        public void Une_liste_vide_ou_nulle_donne_l_empreinte_de_la_chaine_vide()
        {
            // da39a3ee5e6b4b0d… est le préfixe SHA-1 de la chaîne vide, cité dans docs/protocol.md.
            Assert.Equal("da39a3ee5e6b4b0d", ModListHash.Compute(null));
            Assert.Equal("da39a3ee5e6b4b0d", ModListHash.Compute(Array.Empty<string>()));
        }

        [Fact]
        public void L_empreinte_des_identifiants_normaux_est_inchangee()
        {
            // Valeur calculée avec l'algorithme historique (tri ordinal + jointure par \n) :
            // l'échappement ne doit rien changer aux noms de DLL usuels.
            Assert.Equal("d8132ec29283184a", ModListHash.Compute(new[] { "mod.b", "mod.a" }));
        }

        [Fact]
        public void L_ordre_de_chargement_ne_change_pas_l_empreinte()
        {
            Assert.Equal(
                ModListHash.Compute(new[] { "mod.a", "mod.b" }),
                ModListHash.Compute(new[] { "mod.b", "mod.a" }));
        }

        [Fact]
        public void Un_doublon_change_l_empreinte()
        {
            Assert.NotEqual(
                ModListHash.Compute(new[] { "mod.a" }),
                ModListHash.Compute(new[] { "mod.a", "mod.a" }));
        }

        [Fact]
        public void Un_identifiant_avec_saut_de_ligne_ne_collisionne_plus_avec_deux_identifiants()
        {
            Assert.NotEqual(
                ModListHash.Compute(new[] { "a\nb" }),
                ModListHash.Compute(new[] { "a", "b" }));
        }

        [Fact]
        public void Un_antislash_et_un_saut_de_ligne_ne_collisionnent_pas()
        {
            Assert.NotEqual(
                ModListHash.Compute(new[] { "a\\nb" }),
                ModListHash.Compute(new[] { "a\nb" }));
        }

        [Fact]
        public void Un_retour_chariot_ne_collisionne_pas_avec_un_saut_de_ligne()
        {
            Assert.NotEqual(
                ModListHash.Compute(new[] { "a\rb" }),
                ModListHash.Compute(new[] { "a\nb" }));
        }

        [Fact]
        public void Un_element_nul_est_traite_comme_vide()
        {
            Assert.Equal(
                ModListHash.Compute(new[] { "mod.a", null }),
                ModListHash.Compute(new[] { "mod.a", string.Empty }));
        }

        [Fact]
        public void La_casse_change_l_empreinte()
        {
            Assert.NotEqual(
                ModListHash.Compute(new[] { "Mod.a" }),
                ModListHash.Compute(new[] { "mod.a" }));
        }

        [Fact]
        public void L_empreinte_est_seize_caracteres_hexadecimaux_minuscules()
        {
            var hash = ModListHash.Compute(new[] { "mod.a" });

            Assert.Equal(16, hash.Length);
            foreach (var character in hash)
            {
                Assert.True((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));
            }
        }
    }
}
