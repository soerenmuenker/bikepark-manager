using Bikepark.Sim.Core;
using Bikepark.Sim.Weather;
using Godot;

namespace Bikepark.Game.World;

/// <summary>
/// Time-of-day lighting. From the game clock (smoothed between ticks) it moves the <c>Sun</c> along an arc from east
/// (sunrise ~06:00) over south to west (sunset ~21:00) and blends a keyframe table for its colour and energy, the sky,
/// the fog and the ambient light: warm low light in the morning and evening, white at noon, faint blue moonlight at
/// night. Clouds and rain (from the sim's weather) dim the sun and grey the sky. Pure view.
/// </summary>
public partial class DayLight : Node
{
    [Export] public NodePath SimHostPath { get; set; } = "../SimHost";
    [Export] public NodePath SunPath { get; set; } = "../Sun";
    [Export] public NodePath EnvironmentPath { get; set; } = "../WorldEnvironment";

    private const float SunriseMinute = 6 * 60;
    private const float SunsetMinute = 21 * 60;
    private const float MaxElevationDegrees = 55f;
    private const float MoonElevationDegrees = 35f;
    private const float MoonAzimuthDegrees = 200f;

    /// <summary>Light energy below which shadows are switched off (low sun and moonlight).</summary>
    private const float ShadowMinEnergy = 0.2f;

    private readonly record struct Key(float Minute, float Energy, Color Light, Color SkyTop, Color Horizon, float Ambient);

    private static readonly Color NightLight = new(0.55f, 0.65f, 1.0f);
    private static readonly Color NightTop = new(0.02f, 0.03f, 0.08f);
    private static readonly Color NightHorizon = new(0.07f, 0.09f, 0.16f);
    private static readonly Color DayTop = new(0.35f, 0.55f, 0.85f);
    private static readonly Color DayHorizon = new(0.68f, 0.76f, 0.86f);

    private static readonly Key[] Keys =
    [
        new(0, 0.06f, NightLight, NightTop, NightHorizon, 0.25f),
        new(5 * 60, 0.06f, NightLight, NightTop, NightHorizon, 0.25f),
        new(6 * 60, 0.15f, new(1.0f, 0.55f, 0.35f), new(0.20f, 0.28f, 0.50f), new(0.95f, 0.60f, 0.45f), 0.45f),
        new(7 * 60 + 30, 0.8f, new(1.0f, 0.85f, 0.70f), new(0.32f, 0.50f, 0.80f), new(0.82f, 0.78f, 0.78f), 0.8f),
        new(10 * 60, 1.1f, new(1.0f, 0.97f, 0.92f), DayTop, DayHorizon, 1.0f),
        new(17 * 60, 1.05f, new(1.0f, 0.95f, 0.88f), DayTop, DayHorizon, 1.0f),
        new(19 * 60 + 30, 0.75f, new(1.0f, 0.75f, 0.52f), new(0.30f, 0.42f, 0.70f), new(0.90f, 0.70f, 0.58f), 0.75f),
        new(21 * 60, 0.12f, new(1.0f, 0.45f, 0.30f), new(0.12f, 0.15f, 0.32f), new(0.70f, 0.40f, 0.35f), 0.4f),
        new(22 * 60, 0.06f, NightLight, NightTop, NightHorizon, 0.25f),
        new(24 * 60, 0.06f, NightLight, NightTop, NightHorizon, 0.25f),
    ];

    private SimHost _host = null!;
    private DirectionalLight3D _sun = null!;
    private Godot.Environment _environment = null!;
    private ProceduralSkyMaterial? _sky;
    private float _overcast;
    private float _baseFogDensity;

    public override void _Ready()
    {
        _host = GetNode<SimHost>(SimHostPath);
        _sun = GetNode<DirectionalLight3D>(SunPath);
        _environment = GetNode<WorldEnvironment>(EnvironmentPath).Environment;
        _sky = _environment.Sky?.SkyMaterial as ProceduralSkyMaterial;
        _baseFogDensity = _environment.FogDensity;
    }

    /// <summary>How overcast it is now, 0..1: rain is darkest, then rain days, cloudy days, shower days.</summary>
    private static float OvercastTarget(Bikepark.Sim.State.WorldState state)
    {
        if (WeatherMath.IsRaining(state, state.Tick)) return 1f;
        return state.Weather.Today.Kind switch
        {
            WeatherKind.Rain => 0.75f,
            WeatherKind.Cloudy => 0.55f,
            WeatherKind.Showers => 0.35f,
            _ => 0f,
        };
    }

    public override void _Process(double delta)
    {
        var state = _host.Sim.State;
        float minute = GameTime.MinuteOfDay(state.Tick) + Math.Clamp(_host.InterpolationAlpha, 0f, 0.999f);
        _overcast = Mathf.MoveToward(_overcast, OvercastTarget(state), (float)delta * 0.4f); // clouds roll in over a few seconds
        Apply(minute);
    }

    private void Apply(float minute)
    {
        int i = 1;
        while (i < Keys.Length - 1 && Keys[i].Minute < minute) i++;
        var (a, b) = (Keys[i - 1], Keys[i]);
        float t = b.Minute > a.Minute ? Math.Clamp((minute - a.Minute) / (b.Minute - a.Minute), 0f, 1f) : 0f;
        t = t * t * (3f - 2f * t); // smoothstep: no kinks at the keys

        float energy = Mathf.Lerp(a.Energy, b.Energy, t) * (1f - 0.6f * _overcast);
        _sun.LightEnergy = energy;
        _sun.LightColor = Grey(a.Light.Lerp(b.Light, t), 0.6f * _overcast);
        _sun.ShadowEnabled = energy >= ShadowMinEnergy;
        _sun.Basis = Basis.LookingAt(-LightSourceDirection(minute), Vector3.Up);

        var top = Grey(a.SkyTop.Lerp(b.SkyTop, t), 0.8f * _overcast).Darkened(0.25f * _overcast);
        var horizon = Grey(a.Horizon.Lerp(b.Horizon, t), 0.7f * _overcast).Darkened(0.15f * _overcast);
        _environment.FogDensity = _baseFogDensity * (1f + 2f * _overcast * _overcast);
        if (_sky is not null)
        {
            _sky.SkyTopColor = top;
            _sky.SkyHorizonColor = horizon;
            _sky.GroundHorizonColor = horizon;
            _sky.GroundBottomColor = horizon.Darkened(0.5f);
        }
        _environment.AmbientLightEnergy = Mathf.Lerp(a.Ambient, b.Ambient, t);
        // Fog covers the whole valley: half as saturated as the horizon, or dawn and dusk paint everything.
        float grey = horizon.Luminance;
        _environment.FogLightColor = horizon.Lerp(new Color(grey, grey, grey), 0.5f);
    }

    /// <summary>The colour moved towards a grey of the same brightness.</summary>
    private static Color Grey(Color c, float amount)
    {
        float l = c.Luminance;
        return c.Lerp(new Color(l, l, l), Math.Clamp(amount, 0f, 1f));
    }

    /// <summary>Unit vector towards the sun (by day) or the moon (by night). North is -Z, east +X.</summary>
    private static Vector3 LightSourceDirection(float minute)
    {
        float elevation, azimuth;
        if (minute is > SunriseMinute and < SunsetMinute)
        {
            float day = (minute - SunriseMinute) / (SunsetMinute - SunriseMinute);
            elevation = MaxElevationDegrees * MathF.Sin(MathF.PI * day);
            azimuth = 70f + 220f * day; // east-north-east → south → west-north-west
            elevation = Math.Max(elevation, 4f); // keep low light skimming the slopes, not from below
        }
        else
        {
            elevation = MoonElevationDegrees;
            azimuth = MoonAzimuthDegrees;
        }
        float e = Mathf.DegToRad(elevation), az = Mathf.DegToRad(azimuth);
        return new Vector3(MathF.Cos(e) * MathF.Sin(az), MathF.Sin(e), -MathF.Cos(e) * MathF.Cos(az)).Normalized();
    }
}
