namespace Bikepark.Sim.Core;

/// <summary>
/// Simulation time. One tick is one game minute; tick 0 is midnight of day 0.
/// </summary>
public static class GameTime
{
    public const int MinutesPerHour = 60;
    public const int MinutesPerDay = 24 * MinutesPerHour;

    public static long Day(long tick) => tick / MinutesPerDay;

    public static int MinuteOfDay(long tick) => (int)(tick % MinutesPerDay);

    public static bool IsLastMinuteOfDay(long tick) => MinuteOfDay(tick) == MinutesPerDay - 1;

    public static long TicksForDays(long days) => days * MinutesPerDay;

    public static string Format(long tick)
    {
        int minute = MinuteOfDay(tick);
        return $"Day {Day(tick) + 1} {minute / MinutesPerHour:00}:{minute % MinutesPerHour:00}";
    }
}
