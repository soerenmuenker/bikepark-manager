using Bikepark.Game.Terrain;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Land;
using Bikepark.Sim.State;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>
/// Land menu: the scenario's parcels, what each costs and needs (park level), and Buy. Clicking a parcel's name moves
/// the camera there. In a sandbox without parcels all land is the park's.
/// </summary>
public partial class LandPanel : HudPanel
{
    private VBoxContainer _list = null!;
    private Label _summary = null!;
    private readonly List<(Parcel Parcel, Label Status, Button Buy)> _rows = [];
    private int _parcelCount = -1;

    public override string Title => "Land";

    protected override void Build()
    {
        _summary = UiTheme.Label("", 13, UiTheme.TextDim);
        Body.AddChild(_summary);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(620, 420), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_list);
        Body.AddChild(scroll);
        Body.AddChild(UiTheme.Label("Everything you build (paths, trails, lift stations, parking, felling) must be on your land; a lift's cable may cross other land.",
            11, UiTheme.TextDim));
    }

    private void Rebuild(WorldState state)
    {
        foreach (var child in _list.GetChildren()) child.QueueFree();
        _rows.Clear();
        _parcelCount = state.Parcels.Count;
        if (state.Parcels.Count == 0)
        {
            _list.AddChild(UiTheme.Label("Sandbox: all land on the map is yours.", 13, UiTheme.TextDim));
            return;
        }
        foreach (var parcel in state.Parcels)
        {
            var row = new PanelContainer();
            var box = UiTheme.Box(UiTheme.Card, 8, 10, 6);
            box.ShadowSize = 0;
            row.AddThemeStyleboxOverride("panel", box);
            var h = Row(10);
            row.AddChild(h);
            var texts = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            texts.AddThemeConstantOverride("separation", 0);
            var name = UiTheme.Button(parcel.Name, () => FocusOn(parcel), "Show it on the map");
            name.Flat = true;
            name.Alignment = HorizontalAlignment.Left;
            name.AddThemeFontOverride("font", UiTheme.Bold);
            texts.AddChild(name);
            var desc = UiTheme.Label(parcel.Description, 11, UiTheme.TextDim);
            desc.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            desc.CustomMinimumSize = new Vector2(380, 0);
            texts.AddChild(desc);
            var status = UiTheme.Label("", 12);
            texts.AddChild(status);
            h.AddChild(texts);
            string id = parcel.Id;
            var buy = UiTheme.Button($"Buy\n{UiTheme.Money(parcel.PriceCents)}", () => Ctx.Host.Enqueue(new BuyParcelCommand(id)));
            buy.CustomMinimumSize = new Vector2(110, 44);
            buy.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            h.AddChild(buy);
            _list.AddChild(row);
            _rows.Add((parcel, status, buy));
        }
    }

    public override void Refresh()
    {
        var state = Ctx.Sim.State;
        if (_parcelCount != state.Parcels.Count || _rows.Any(r => !state.Parcels.Contains(r.Parcel))) Rebuild(state);
        int level = Ctx.Kpi.Level;
        int owned = state.OwnedParcelIds.Count;
        _summary.Text = state.Parcels.Count == 0 ? "" : $"You own {owned} of {state.Parcels.Count} parcels · park level {level} · {UiTheme.Money(state.Finance.MoneyCents)} available";
        foreach (var (parcel, status, buy) in _rows)
        {
            long hectares10 = LandMath.AreaSquareMeters(parcel.Outline) / 1000; // tenths of a hectare
            string size = $"{hectares10 / 10}.{hectares10 % 10} ha";
            if (LandMath.IsOwned(state, parcel))
            {
                status.Text = $"{size} · yours";
                status.AddThemeColorOverride("font_color", LandView.OwnedColor);
                buy.Visible = false;
                continue;
            }
            string? why = LandMath.CannotBuy(state, parcel, level);
            status.Text = $"{size} · " + (level < parcel.RequiredLevel ? $"needs park level {parcel.RequiredLevel} (you are level {level})"
                : why is null ? "for sale" : "not enough money yet");
            status.AddThemeColorOverride("font_color", level < parcel.RequiredLevel ? UiTheme.TextDim : why is null ? LandView.ForSaleColor : UiTheme.Warn);
            buy.Visible = true;
            buy.Disabled = why is not null;
            buy.TooltipText = why ?? $"Buy {parcel.Name} for {UiTheme.Money(parcel.PriceCents)}";
        }
    }

    private void FocusOn(Parcel parcel)
    {
        var center = LandView.Centroid(parcel);
        Ctx.Camera.Follow = null;
        Ctx.Camera.FocusOn(new Vector2(center.X, center.Y));
    }
}

/// <summary>
/// Parcel borders on the terrain (pure view): yours in teal, for sale (level reached) in yellow, locked in grey; names
/// float over each parcel while the Land menu is open. Hidden in sandboxes without parcels.
/// </summary>
public partial class LandView : Node3D
{
    public static readonly Color OwnedColor = new(0.25f, 0.85f, 0.85f);
    public static readonly Color ForSaleColor = new(1.0f, 0.82f, 0.25f);
    public static readonly Color LockedColor = new(0.65f, 0.65f, 0.70f);

    private const float StepMeters = 4f;
    private const float WidthMeters = 3f;

    private readonly SimHost _host;
    private readonly TerrainView _terrain;
    private readonly Func<int> _level;
    private MeshInstance3D _borders = null!;
    private Node3D _labels = null!;
    private string _signature = "";

    /// <summary>Show the parcel names (while the Land menu is open).</summary>
    public bool ShowLabels { get; set; }

    public LandView() : this(null!, null!, () => 0) { }

    public LandView(SimHost host, TerrainView terrain, Func<int> level)
    {
        _host = host;
        _terrain = terrain;
        _level = level;
        Name = "Land";
    }

    public override void _Ready()
    {
        _borders = new MeshInstance3D
        {
            Name = "Borders",
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        AddChild(_borders);
        _labels = new Node3D { Name = "Labels" };
        AddChild(_labels);
    }

    public override void _Process(double delta)
    {
        if (_host is null || _terrain.Grid is null) return;
        var state = _host.Sim.State;
        int level = _level();
        string signature = $"{state.Parcels.Count}|{string.Join(',', state.OwnedParcelIds)}|{level}|{_terrain.Grid.GetHashCode()}";
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild(state, level);
        }
        _labels.Visible = ShowLabels;
    }

    private void Rebuild(WorldState state, int level)
    {
        foreach (var child in _labels.GetChildren()) child.QueueFree();
        if (state.Parcels.Count == 0)
        {
            _borders.Mesh = null;
            return;
        }
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        // Locked first, owned last: where borders meet, the more important colour ends up on top.
        foreach (var parcel in state.Parcels.OrderBy(p => LandMath.IsOwned(state, p) ? 2 : level >= p.RequiredLevel ? 1 : 0))
        {
            bool owned = LandMath.IsOwned(state, parcel);
            var color = owned ? OwnedColor : level >= parcel.RequiredLevel ? ForSaleColor : LockedColor;
            float lift = owned ? 1.6f : 1.3f;
            var outline = parcel.Outline;
            for (int i = 0; i < outline.Count; i++)
            {
                var a = new Vector2(outline[i].X / 100f, outline[i].Z / 100f);
                var b = new Vector2(outline[(i + 1) % outline.Count].X / 100f, outline[(i + 1) % outline.Count].Z / 100f);
                Ribbon(st, a, b, new Color(color, owned ? 0.95f : 0.75f), lift);
            }
            var center = Centroid(parcel);
            var label = new Label3D
            {
                Text = owned ? parcel.Name : $"{parcel.Name}\nlevel {parcel.RequiredLevel} · {UiTheme.Money(parcel.PriceCents)}",
                Position = new Vector3(center.X, _terrain.HeightAt(center.X, center.Y) + 25f, center.Y),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                FontSize = 64,
                PixelSize = 0.3f,
                Modulate = color,
                OutlineSize = 12,
                OutlineModulate = new Color(0, 0, 0, 0.8f),
            };
            _labels.AddChild(label);
        }
        _borders.Mesh = st.Commit();
    }

    /// <summary>A flat band along the ground from a to b (metres), drawn a little above the terrain.</summary>
    private void Ribbon(SurfaceTool st, Vector2 a, Vector2 b, Color color, float lift)
    {
        var dir = b - a;
        float length = dir.Length();
        if (length < 0.01f) return;
        var side = new Vector2(-dir.Y, dir.X) / length * (WidthMeters / 2);
        int steps = Math.Max(1, (int)(length / StepMeters));
        Vector3 At(Vector2 p) => new(p.X, _terrain.HeightAt(Math.Clamp(p.X, 0, 999.9f), Math.Clamp(p.Y, 0, 999.9f)) + lift, p.Y);
        st.SetColor(color);
        for (int i = 0; i < steps; i++)
        {
            var p0 = a + dir * (i / (float)steps);
            var p1 = a + dir * ((i + 1) / (float)steps);
            Vector3 l0 = At(p0 + side), r0 = At(p0 - side), l1 = At(p1 + side), r1 = At(p1 - side);
            st.AddVertex(l0); st.AddVertex(r0); st.AddVertex(l1);
            st.AddVertex(l1); st.AddVertex(r0); st.AddVertex(r1);
        }
    }

    /// <summary>The average of a parcel's corners (metres, x and z).</summary>
    public static Vector2 Centroid(Parcel parcel) =>
        new((float)parcel.Outline.Average(p => p.X) / 100f, (float)parcel.Outline.Average(p => p.Z) / 100f);
}
