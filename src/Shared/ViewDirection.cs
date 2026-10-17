using UnityEngine;

namespace Coop.Shared
{
    /// <summary>
    /// Direction **du regard** d'une instance, réduite au lacet (rotation autour de Y).
    ///
    /// Pourquoi cette fonction existe, et pourquoi elle ne lit pas la racine du joueur : dans Subnautica,
    /// `Player.mainObject` (la racine du joueur) **ne porte pas le lacet de la vue** — c'est la caméra
    /// (`MainCameraControl`) qui le porte, et `Player/body` compense ce lacet dans son repère local.
    /// Envoyer `Player.mainObject.transform.rotation` envoyait donc une orientation quasi fixe, et comme
    /// l'avatar distant était orienté par `networkRootRotation * body.localRotation` **du joueur local**,
    /// tous les corps distants prenaient le lacet de la caméra de celui qui regarde (constaté en jeu).
    ///
    /// La caméra est la seule source qui dit la vérité pour les deux instances : le client (joueur à la
    /// première personne) comme l'hôte fenêtré. Le y est retiré parce qu'un corps ne se penche pas : le
    /// tangage reste un paramètre d'animation (`view_pitch`).
    /// </summary>
    public static class ViewDirection
    {
        /// <summary>
        /// Rotation (lacet seul, `Vector3.up` pour l'axe) vers laquelle l'instance regarde.
        /// <paramref name="fallback"/> sert quand aucune caméra n'est disponible (menu, chargement) :
        /// on préfère l'orientation de la racine du joueur à une identité arbitraire.
        /// </summary>
        public static Quaternion Yaw(Transform fallback)
        {
            var camera = MainCameraControl.main != null ? MainCameraControl.main.transform : null;
            var forward = camera != null ? camera.forward : Vector3.zero;

            // Une caméra peut être présente mais pas encore orientée (juste après le chargement) :
            // dans ce cas `forward` est horizontalement nul et on retombe sur la racine.
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f && fallback != null)
            {
                forward = fallback.forward;
                forward.y = 0f;
            }

            return forward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;
        }

        /// <summary>Lacet en degrés de la rotation donnée, dans [-180, 180].</summary>
        public static float YawDegrees(Quaternion rotation)
        {
            var forward = rotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                return 0f;
            }

            return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        }
    }
}
