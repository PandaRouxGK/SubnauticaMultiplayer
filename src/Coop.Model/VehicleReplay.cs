using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Coop.Model
{
    /// <summary>
    /// Enregistrement et **rejeu hors du jeu** d'une suite d'états de véhicule (P3, M4).
    ///
    /// Pourquoi ce fichier existe : jusqu'ici, une régression d'interpolation ne se voyait qu'à l'étape J,
    /// après 150 secondes de jeu. Le flux réel est donc écrit tel qu'il a été reçu — tick, intervalle,
    /// position — puis rejoué par la **même** règle de durée que le client
    /// (<see cref="VehicleInterpolation"/>), en millisecondes et sans Unity.
    ///
    /// Format : une ligne par état, `tick;intervalle;x;y;z`, en culture invariante. L'intervalle est celui
    /// **mesuré** entre deux annonces consécutives côté client : c'est lui qui décide de la durée du
    /// segment, et c'est donc lui qu'il faut conserver.
    /// </summary>
    public static class VehicleReplay
    {
        /// <summary>Un état reçu : son tick, l'intervalle mesuré avant lui (s), et sa position.</summary>
        public struct Frame
        {
            public long Tick;
            public float GapSeconds;
            public float X;
            public float Y;
            public float Z;
        }

        /// <summary>Résultat d'un rejeu : ce que la mesure en jeu aurait relevé, image par image.</summary>
        public struct Stats
        {
            public int States;
            public int Frames;
            public float MaxStepMeters;
            public float MeanStepMeters;
            public float MaxDistanceMeters;

            public override string ToString() => string.Format(
                CultureInfo.InvariantCulture,
                "états={0} images={1} pas max={2:F2} m moy={3:F3} m plus grand trajet={4:F1} m",
                States,
                Frames,
                MaxStepMeters,
                MeanStepMeters,
                MaxDistanceMeters);
        }

        /// <summary>Formate un état pour l'enregistrement. Ne lève pas : une valeur non finie est écrite telle quelle.</summary>
        public static string Format(long tick, float gapSeconds, float x, float y, float z)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0};{1:R};{2:R};{3:R};{4:R}",
                tick,
                gapSeconds,
                x,
                y,
                z);
        }

        /// <summary>Relit une ligne. Rend <c>false</c> plutôt que de lever : un fichier de mesure n'est pas un protocole.</summary>
        public static bool TryParse(string line, out Frame frame)
        {
            frame = default(Frame);
            if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
            {
                return false;
            }

            var parts = line.Split(';');
            if (parts.Length != 5)
            {
                return false;
            }

            if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tick))
            {
                return false;
            }

            if (!TryFloat(parts[1], out var gap) ||
                !TryFloat(parts[2], out var x) ||
                !TryFloat(parts[3], out var y) ||
                !TryFloat(parts[4], out var z))
            {
                return false;
            }

            frame = new Frame { Tick = tick, GapSeconds = gap, X = x, Y = y, Z = z };
            return true;
        }

        /// <summary>
        /// Rejoue une suite d'états à la cadence d'image donnée, avec la **règle réelle** : chaque état
        /// ouvre un segment dont la durée vient de <see cref="VehicleInterpolation.SegmentDuration"/>, et la
        /// copie avance linéairement dans ce segment.
        ///
        /// La première position est appliquée d'un coup (comme à l'adoption d'un véhicule) : ce n'est pas un
        /// pas d'interpolation, et la compter écraserait toutes les autres mesures.
        /// </summary>
        public static Stats Simulate(IEnumerable<Frame> frames, float frameSeconds)
        {
            var stats = new Stats();
            if (frames == null || frameSeconds <= 0f)
            {
                return stats;
            }

            var havePrevious = false;
            float px = 0f, py = 0f, pz = 0f;
            double sum = 0;
            var steps = 0;

            foreach (var frame in frames)
            {
                stats.States++;

                if (!havePrevious)
                {
                    px = frame.X;
                    py = frame.Y;
                    pz = frame.Z;
                    havePrevious = true;
                    continue;
                }

                var dx = frame.X - px;
                var dy = frame.Y - py;
                var dz = frame.Z - pz;
                var distance = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

                if (distance > stats.MaxDistanceMeters)
                {
                    stats.MaxDistanceMeters = distance;
                }

                var duration = VehicleInterpolation.SegmentDuration(frame.GapSeconds, distance);

                var sliceCount = Math.Max(1, (int)Math.Ceiling(duration / frameSeconds));
                var step = distance / sliceCount;

                for (var i = 0; i < sliceCount; i++)
                {
                    stats.Frames++;
                    sum += step;
                    steps++;
                    if (step > stats.MaxStepMeters)
                    {
                        stats.MaxStepMeters = step;
                    }
                }

                px = frame.X;
                py = frame.Y;
                pz = frame.Z;
            }

            stats.MeanStepMeters = steps > 0 ? (float)(sum / steps) : 0f;
            return stats;
        }

        /// <summary>Relit un enregistrement complet. Les lignes illisibles sont ignorées et comptées par l'appelant.</summary>
        public static List<Frame> Parse(string text, out int ignored)
        {
            var frames = new List<Frame>();
            ignored = 0;
            if (string.IsNullOrEmpty(text))
            {
                return frames;
            }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#')
                {
                    // Un commentaire n'est pas une erreur : le fichier commence par une ligne qui dit
                    // de quel véhicule il s'agit, et la compter comme illisible ferait échouer la
                    // relecture sur son propre en-tête.
                    continue;
                }

                if (TryParse(line, out var frame))
                {
                    frames.Add(frame);
                }
                else
                {
                    ignored++;
                }
            }

            return frames;
        }

        private static bool TryFloat(string raw, out float value)
        {
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
