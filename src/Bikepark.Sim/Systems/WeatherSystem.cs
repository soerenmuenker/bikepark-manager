using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Weather;

namespace Bikepark.Sim.Systems;

/// <summary>
/// The weather. At midnight tomorrow's forecast becomes today's weather and a new forecast is rolled (only when the
/// scenario has weather odds; otherwise it stays sunny and no randomness is used). Every minute the ground gets wetter
/// in the rain and dries otherwise. Runs first, so the other systems see this minute's weather.
/// </summary>
internal sealed class WeatherSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        var rules = state.WeatherRules;
        var weather = state.Weather;
        int minute = GameTime.MinuteOfDay(ctx.Tick);

        if (minute == 0 && rules.Enabled)
        {
            weather.Today = weather.Tomorrow ?? WeatherMath.Roll(ctx.Rng, rules);
            weather.Tomorrow = WeatherMath.Roll(ctx.Rng, rules);
            if (weather.Today.HasRain) weather.RainDays++;
            ctx.Publish(new WeatherForecast(ctx.Tick, weather.Today, weather.Tomorrow));
        }

        var today = weather.Today;
        if (today.HasRain && minute == today.RainStartMinute) ctx.Publish(new RainStarted(ctx.Tick));
        if (today.HasRain && minute == today.RainEndMinute) ctx.Publish(new RainStopped(ctx.Tick));

        if (today.IsRainingAt(minute))
        {
            weather.RainMinutes++;
            weather.WetnessPermille = Math.Min(1000, weather.WetnessPermille + rules.WetPerRainMinute);
        }
        else if (weather.WetnessPermille > 0)
        {
            int dry = today.Kind == WeatherKind.Sunny ? rules.DryPerMinuteSunny : rules.DryPerMinuteCloudy;
            weather.WetnessPermille = Math.Max(0, weather.WetnessPermille - dry);
        }
    }
}
