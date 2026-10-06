namespace Bikepark.Sim.Trails;

/// <summary>
/// The game's gradient score: -10..+10, linear in angle (1 point = 9°). 0 is flat, +10 a vertical wall, -10 a
/// vertical drop; negative means downhill in the direction of travel. Stored in tenths (-100..100) as integers.
/// <para>Rough guide: 1.0 = 9° (16 %), 2.0 = 18° (32 %), 3.0 = 27° (51 %), 5.0 = 45° (100 %), 8.0 = 72° (308 %).</para>
/// </summary>
public static class Gradient
{
    public const int MaxTenths = 100;

    /// <summary>tan(k × 0.9°) × 1000 for k = 0..99: the grade (permille) at each tenth of the score.</summary>
    private static readonly int[] PermilleAtTenth =
    [
        0, 16, 31, 47, 63, 79, 95, 110, 126, 142,
        158, 175, 191, 207, 224, 240, 257, 274, 291, 308,
        325, 342, 360, 378, 396, 414, 433, 452, 471, 490,
        510, 529, 550, 570, 591, 613, 635, 657, 680, 703,
        727, 751, 776, 801, 827, 854, 882, 910, 939, 969,
        1000, 1032, 1065, 1099, 1134, 1171, 1209, 1248, 1289, 1332,
        1376, 1423, 1471, 1522, 1576, 1632, 1691, 1753, 1819, 1889,
        1963, 2041, 2125, 2215, 2311, 2414, 2526, 2646, 2778, 2921,
        3078, 3251, 3442, 3655, 3895, 4165, 4474, 4829, 5242, 5730,
        6314, 7026, 7916, 9058, 10579, 12706, 15895, 21205, 31821, 63657,
    ];

    /// <summary>Score in tenths (-100..100) for a signed grade in permille (rise / run × 1000), rounded to the nearest tenth.</summary>
    public static int FromPermille(int gradePermille)
    {
        int g = Math.Abs(gradePermille);
        int lo = 0, hi = PermilleAtTenth.Length - 1;
        if (g >= PermilleAtTenth[hi])
        {
            // Between 89.1° and vertical.
            int tenths = g >= 2 * PermilleAtTenth[hi] ? MaxTenths : hi;
            return Math.Sign(gradePermille) * tenths;
        }
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (PermilleAtTenth[mid] <= g) lo = mid; else hi = mid - 1;
        }
        // lo is the last tenth at or below g; round to the nearer neighbour.
        int result = lo + 1 < PermilleAtTenth.Length && g - PermilleAtTenth[lo] > PermilleAtTenth[lo + 1] - g ? lo + 1 : lo;
        return Math.Sign(gradePermille) * result;
    }

    /// <summary>Grade in permille for a score in tenths (the steepest grade that still has this score's angle).</summary>
    public static int ToPermille(int tenths)
    {
        int t = Math.Clamp(Math.Abs(tenths), 0, MaxTenths - 1);
        return Math.Sign(tenths) * PermilleAtTenth[t];
    }

    /// <summary>"-2.3", "+1.0", "0.0".</summary>
    public static string Format(int tenths) =>
        $"{(tenths > 0 ? "+" : tenths < 0 ? "-" : "")}{Math.Abs(tenths) / 10}.{Math.Abs(tenths) % 10}";
}
