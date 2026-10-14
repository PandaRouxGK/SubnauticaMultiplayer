using System.Collections.Generic;
using BepInEx.Bootstrap;

namespace Coop.Shared
{
    /// <summary>
    /// Empreinte de la liste des mods chargés, calculée depuis BepInEx.
    ///
    /// Les plugins du projet lui-même (<c>coop.subnautica.*</c>) sont exclus : le serveur et le
    /// client n'ont pas le même plugin chargé, donc les inclure ferait échouer tous les handshakes.
    /// Ce qu'on veut comparer, c'est le contenu tiers (mods de gameplay, assets), pas notre propre code.
    ///
    /// Ce fichier est compilé dans plusieurs projets via un &lt;Compile Include&gt; partagé :
    /// il n'existe qu'une seule implémentation, sans projet supplémentaire.
    /// </summary>
    internal static class BepInExModList
    {
        private const string OwnPluginPrefix = "coop.subnautica";

        public static string ComputeHash()
        {
            var ids = new List<string>();

            foreach (var plugin in Chainloader.PluginInfos.Values)
            {
                var guid = plugin.Metadata.GUID;
                if (guid.StartsWith(OwnPluginPrefix))
                {
                    continue;
                }

                ids.Add(guid + ":" + plugin.Metadata.Version);
            }

            return Model.ModListHash.Compute(ids);
        }

        public static int ThirdPartyPluginCount()
        {
            var count = 0;
            foreach (var plugin in Chainloader.PluginInfos.Values)
            {
                if (!plugin.Metadata.GUID.StartsWith(OwnPluginPrefix))
                {
                    count++;
                }
            }

            return count;
        }
    }
}
