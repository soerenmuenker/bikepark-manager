using Bikepark.Sim.Core;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Weather;

public enum WeatherKind : byte
{
    Sunny = 0,
    Cloudy = 1,

    /// <summary>A shower of a few hours during the day.</summary>
    Showers = 2,

    /// <summary>Rain for a large part of the day.</summary>
    Rain = 3,
}

/// <summary>One day's weather: its kind and when it rains (minutes of the day, end exclusive; equal = no rain).</summary>
public sealed record DayWeather(WeatherKind Kind, int RainStartMinute = 0, int RainEndMinute = 0)
{
    public static readonly DayWeather Sunny = new(WeatherKind.Sunny);

    public bool HasRain => RainEndMinute > RainStartMinute;

    public bool IsRainingAt(int minuteOfDay) => minuteOfDay >= RainStartMinute && minuteOfDay < RainEndMinute;
}

/// <summary>Today's weather, tomorrow's forecast and how wet the ground is.</summary>
public sealed class WeatherState
{
    public DayWeather Today { get; set; } = DayWeather.Sunny;

    /// <summary>The forecast, which becomes <see cref="Today"/> at midnight (null until the first day starts).</summary>
    public DayWeather? Tomorrow { get; set; }

    /// <summary>Ground wetness 0..1000: rises while it rains, dries afterwards. Wet trails wear faster.</summary>
    public int WetnessPermille { get; set; }

    public long RainMinutes { get; set; }
    public int RainDays { get; set; }
}

/// <summary>
/// Weather tuning, loaded from the scenario and saved with the game. Each day's weather is rolled from the odds; all odds
/// 0 (the default) means always sunny with no randomness used.
/// </summary>
public sealed class WeatherRules
{
    public int SunnyPermille { get; set; }
    public int CloudyPermille { get; set; }
    public int ShowersPermille { get; set; }
    public int RainPermille { get; set; }

    public int ShowerMinHours { get; set; } = 1;
    public int ShowerMaxHours { get; set; } = 3;
    public int RainMinHours { get; set; } = 4;
    public int RainMaxHours { get; set; } = 10;

    /// <summary>Ground wetness gained per minute of rain, lost per dry minute (sunny / other days).</summary>
    public int WetPerRainMinute { get; set; } = 8;
    public int DryPerMinuteSunny { get; set; } = 2;
    public int DryPerMinuteCloudy { get; set; } = 1;

    /// <summary>Arrivals in permille of normal, by today's weather (people check the forecast).</summary>
    public int SunnyArrivalPermille { get; set; } = 1000;
    public int CloudyArrivalPermille { get; set; } = 1000;
    public int ShowersArrivalPermille { get; set; } = 1000;
    public int RainArrivalPermille { get; set; } = 1000;

    /// <summary>Mood lost per minute in the rain (not on the lift).</summary>
    public int RainMoodPerMinute { get; set; }

    public bool Enabled => SunnyPermille + CloudyPermille + ShowersPermille + RainPermille > 0;

    public int ArrivalPermille(WeatherKind kind) => kind switch
    {
        WeatherKind.Sunny => SunnyArrivalPermille,
        WeatherKind.Cloudy => CloudyArrivalPermille,
        WeatherKind.Showers => ShowersArrivalPermille,
        _ => RainArrivalPermille,
    };

    public List<string> Validate()
    {
        var errors = new List<string>();
        int[] odds = [SunnyPermille, CloudyPermille, ShowersPermille, RainPermille];
        if (odds.Any(o => o < 0) || Enabled && odds.Sum() != 1000)
            errors.Add("weatherRules: the odds must be >= 0 and add up to 1000 (or all be 0)");
        if (ShowerMinHours < 1 || ShowerMaxHours < ShowerMinHours || ShowerMaxHours > 12)
            errors.Add("weatherRules: shower hours must satisfy 1 <= min <= max <= 12");
        if (RainMinHours < 1 || RainMaxHours < RainMinHours || RainMaxHours > 18)
            errors.Add("weatherRules: rain hours must satisfy 1 <= min <= max <= 18");
        if (WetPerRainMinute is < 0 or > 1000 || DryPerMinuteSunny is < 0 or > 1000 || DryPerMinuteCloudy is < 0 or > 1000)
            errors.Add("weatherRules: wetting and drying must be within 0..1000 per minute");
        if (new[] { SunnyArrivalPermille, CloudyArrivalPermille, ShowersArrivalPermille, RainArrivalPermille }.Any(p => p is < 0 or > 5000))
            errors.Add("weatherRules: arrival permilles must be within 0..5000");
        if (RainMoodPerMinute is < 0 or > 100) errors.Add("weatherRules.rainMoodPerMinute must be within 0..100");
        return errors;
    }
}

public static class WeatherMath
{
    public static bool IsRaining(WorldState state, long tick) => state.Weather.Today.IsRainingAt(GameTime.MinuteOfDay(tick));

    /// <summary>Rolls a day's weather: the kind from the odds, then when it rains.</summary>
    public static DayWeather Roll(SimRandom rng, WeatherRules rules)
    {
        int roll = rng.NextInt(1000);
        WeatherKind kind = roll < rules.SunnyPermille ? WeatherKind.Sunny
            : roll < rules.SunnyPermille + rules.CloudyPermille ? WeatherKind.Cloudy
            : roll < rules.SunnyPermille + rules.CloudyPermille + rules.ShowersPermille ? WeatherKind.Showers
            : WeatherKind.Rain;
        switch (kind)
        {
            case WeatherKind.Showers:
            {
                // A daytime shower, 06:00-20:00.
                int length = rng.Range(rules.ShowerMinHours, rules.ShowerMaxHours + 1) * GameTime.MinutesPerHour;
                int start = rng.Range(6 * 60, 20 * 60 - length + 1);
                return new DayWeather(kind, start, start + length);
            }
            case WeatherKind.Rain:
            {
                // Mostly rain, 03:00-22:00.
                int length = rng.Range(rules.RainMinHours, rules.RainMaxHours + 1) * GameTime.MinutesPerHour;
                int start = rng.Range(3 * 60, Math.Max(3 * 60, 22 * 60 - length) + 1);
                return new DayWeather(kind, start, Math.Min(GameTime.MinutesPerDay, start + length));
            }
            default:
                return new DayWeather(kind);
        }
    }
}
