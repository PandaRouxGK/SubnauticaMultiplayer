using System;
using System.IO;
using Coop.Model;
using Xunit;

namespace Coop.Model.Tests
{
    /// <summary>
    /// v21 : le registre survit désormais à la session. Le fichier doit être **exactement** relisible, et
    /// un contenu ambigu refusé en entier — un inventaire partiellement relu serait pire qu'un inventaire
    /// perdu, parce qu'il ferait refuser un craft légitime sans que personne ne sache pourquoi.
    /// </summary>
    public sealed class InventoryStoreTests
    {
        [Fact]
        public void Un_contenu_fait_un_aller_retour_exact_et_trie()
        {
            var entries = new[]
            {
                new InventoryEntry("Titanium", 40),
                new InventoryEntry("Copper", 2),
            };

            var text = InventoryStore.Serialize(entries);
            Assert.StartsWith(InventoryStore.Header, text, StringComparison.Ordinal);

            Assert.True(InventoryStore.TryParse(text, out var parsed, out var error), error);
            Assert.Equal(2, parsed.Length);
            Assert.Equal("Copper", parsed[0].TechTypeName);
            Assert.Equal(2, parsed[0].Count);
            Assert.Equal("Titanium", parsed[1].TechTypeName);
            Assert.Equal(40, parsed[1].Count);
        }

        [Fact]
        public void Un_contenu_vide_est_relu_vide()
        {
            Assert.True(
                InventoryStore.TryParse(InventoryStore.Serialize(Array.Empty<InventoryEntry>()), out var parsed, out var error),
                error);

            Assert.Empty(parsed);
        }

        [Fact]
        public void Une_en_tete_absente_est_refusee()
        {
            Assert.False(InventoryStore.TryParse("Titanium\t2\n", out _, out var error));
            Assert.Contains("en-tête", error);
        }

        [Fact]
        public void Une_ligne_mal_formee_est_refusee_sans_rien_relire()
        {
            Assert.False(InventoryStore.TryParse(InventoryStore.Header + "\nTitanium 2\n", out _, out var error));
            Assert.Contains("mal formée", error);
        }

        [Fact]
        public void Un_doublon_ou_un_desordre_est_refuse()
        {
            Assert.False(InventoryStore.TryParse(
                InventoryStore.Header + "\nTitanium\t1\nTitanium\t2\n",
                out _,
                out var duplicate));

            Assert.Contains("triés ou dupliqués", duplicate);

            Assert.False(InventoryStore.TryParse(
                InventoryStore.Header + "\nZinc\t1\nCopper\t1\n",
                out _,
                out var unordered));

            Assert.Contains("triés ou dupliqués", unordered);
        }

        [Fact]
        public void Une_quantite_hors_bornes_est_refusee()
        {
            Assert.False(InventoryStore.TryParse(
                InventoryStore.Header + "\nTitanium\t0\n",
                out _,
                out var zero));

            Assert.Contains("hors bornes", zero);

            Assert.False(InventoryStore.TryParse(
                InventoryStore.Header + "\nTitanium\t" + (Protocol.MaxInventoryStack + 1) + "\n",
                out _,
                out var tooBig));

            Assert.Contains("hors bornes", tooBig);
        }

        [Fact]
        public void Un_fichier_absent_n_est_pas_une_erreur()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent.inv");

            Assert.False(InventoryStore.TryLoad(path, out var entries, out var error, out var missing));
            Assert.True(missing);
            Assert.Null(error);
            Assert.Empty(entries);
        }

        [Fact]
        public void Un_fichier_ecrit_est_relu_au_meme_chemin()
        {
            var directory = Path.Combine(Path.GetTempPath(), "coop-store-" + Guid.NewGuid().ToString("N"));

            try
            {
                var path = InventoryStore.PathFor(directory, "slot0000", "killi");
                var entries = new[]
                {
                    new InventoryEntry("Copper", 3),
                    new InventoryEntry("Titanium", 12),
                };

                Assert.True(InventoryStore.Save(path, entries, out var saveError), saveError);
                Assert.True(File.Exists(path));

                Assert.True(InventoryStore.TryLoad(path, out var loaded, out var loadError, out var missing), loadError);
                Assert.False(missing);
                Assert.Equal(2, loaded.Length);
                Assert.Equal("Titanium", loaded[1].TechTypeName);
                Assert.Equal(12, loaded[1].Count);
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        [Fact]
        public void Un_pseudo_ne_peut_pas_sortir_du_dossier()
        {
            var path = InventoryStore.PathFor("racine", "..\\..\\config", "../../pseudo");
            var normalized = path.Replace('\\', '/');
            var segments = normalized.Split('/');

            // Aucun séparateur ne doit venir d'une valeur reçue : le dossier attendu est le seul existant.
            Assert.Equal(4, segments.Length);
            Assert.Equal("racine", segments[0]);
            Assert.Equal("inventories", segments[1]);
            Assert.NotEqual(".", segments[2]);
            Assert.NotEqual("..", segments[2]);
            Assert.NotEqual(".", segments[3]);
            Assert.NotEqual("..", segments[3]);
            Assert.EndsWith(InventoryStore.Extension, normalized);
        }
    }
}
