using Godot;

namespace Bikepark.Game.Ui;

public enum UiIcon
{
    None,
    Build,
    Trails,
    Riders,
    Lift,
    Finance,
    Map,
    Pause,
    Play1,
    Play2,
    Play3,
    Play4,
    Skip,
    Menu,
    Person,
    Mood,
    Queue,
    Path,
    Trail,
    Parking,
    Demo,
    Close,
    Follow,
    ZoomIn,
    ZoomOut,
    Berm,
    Rollers,
    Table,
    Double,
    WallRide,
    Kicker,
    Drop,
}

/// <summary>Line icons drawn with canvas primitives (no image assets), scaled to any square.</summary>
internal static class UiIcons
{
    /// <param name="mood">For <see cref="UiIcon.Mood"/>: 0..1000, picks the mouth.</param>
    public static void Draw(CanvasItem ci, UiIcon icon, Rect2 rect, Color color, int mood = 700)
    {
        float s = Math.Min(rect.Size.X, rect.Size.Y);
        var o = rect.Position + (rect.Size - new Vector2(s, s)) / 2;
        Vector2 P(float x, float y) => o + new Vector2(x * s, y * s);
        float w = Math.Max(1.5f, s * 0.075f);

        void Line(float x0, float y0, float x1, float y1) => ci.DrawLine(P(x0, y0), P(x1, y1), color, w, true);
        void Circle(float x, float y, float r) => ci.DrawArc(P(x, y), r * s, 0, Mathf.Tau, 32, color, w, true);
        void Disc(float x, float y, float r, Color c) => ci.DrawCircle(P(x, y), r * s, c);
        void Poly(params float[] xy)
        {
            var points = new Vector2[xy.Length / 2];
            for (int i = 0; i < points.Length; i++) points[i] = P(xy[2 * i], xy[2 * i + 1]);
            ci.DrawColoredPolygon(points, color);
        }
        void Polyline(params float[] xy)
        {
            var points = new Vector2[xy.Length / 2];
            for (int i = 0; i < points.Length; i++) points[i] = P(xy[2 * i], xy[2 * i + 1]);
            ci.DrawPolyline(points, color, w, true);
        }
        void Triangles(int n)
        {
            float width = 0.62f / n;
            for (int i = 0; i < n; i++)
            {
                float x = 0.2f + i * width;
                Poly(x, 0.28f, x + width * 0.95f, 0.5f, x, 0.72f);
            }
        }

        switch (icon)
        {
            case UiIcon.Build: // hammer
                Line(0.28f, 0.80f, 0.62f, 0.40f);
                Poly(0.42f, 0.30f, 0.62f, 0.14f, 0.86f, 0.38f, 0.78f, 0.46f, 0.62f, 0.32f, 0.52f, 0.42f);
                break;
            case UiIcon.Trails: // winding line down a hill with a flag at the top
                Polyline(0.22f, 0.84f, 0.50f, 0.78f, 0.36f, 0.62f, 0.66f, 0.54f, 0.48f, 0.38f, 0.70f, 0.30f);
                Line(0.70f, 0.30f, 0.70f, 0.12f);
                Poly(0.70f, 0.12f, 0.86f, 0.17f, 0.70f, 0.22f);
                break;
            case UiIcon.Riders: // bike
                Circle(0.27f, 0.66f, 0.15f);
                Circle(0.73f, 0.66f, 0.15f);
                Polyline(0.27f, 0.66f, 0.42f, 0.42f, 0.62f, 0.42f, 0.73f, 0.66f);
                Polyline(0.42f, 0.42f, 0.50f, 0.66f, 0.62f, 0.42f);
                Line(0.60f, 0.34f, 0.70f, 0.34f);
                Disc(0.50f, 0.22f, 0.07f, color);
                break;
            case UiIcon.Lift or UiIcon.Queue: // rope with a cabin
                Line(0.10f, 0.30f, 0.90f, 0.14f);
                Line(0.50f, 0.22f, 0.50f, 0.38f);
                ci.DrawRect(new Rect2(P(0.32f, 0.38f), new Vector2(0.36f, 0.36f) * s), color, false, w);
                Line(0.32f, 0.52f, 0.68f, 0.52f);
                break;
            case UiIcon.Finance: // coin with a euro sign
                Circle(0.5f, 0.5f, 0.34f);
                ci.DrawString(UiTheme.Bold, P(0.5f, 0.5f) + new Vector2(-s * 0.2f, s * 0.15f), "€", HorizontalAlignment.Center, s * 0.4f, (int)(s * 0.42f), color);
                break;
            case UiIcon.Map: // stacked layers
                for (int i = 0; i < 3; i++)
                {
                    float y = 0.30f + i * 0.17f;
                    Polyline(0.5f, y - 0.14f, 0.86f, y, 0.5f, y + 0.14f, 0.14f, y, 0.5f, y - 0.14f);
                }
                break;
            case UiIcon.Pause:
                ci.DrawRect(new Rect2(P(0.30f, 0.26f), new Vector2(0.13f, 0.48f) * s), color);
                ci.DrawRect(new Rect2(P(0.57f, 0.26f), new Vector2(0.13f, 0.48f) * s), color);
                break;
            case UiIcon.Play1: Triangles(1); break;
            case UiIcon.Play2: Triangles(2); break;
            case UiIcon.Play3: Triangles(3); break;
            case UiIcon.Play4: Triangles(4); break;
            case UiIcon.Skip:
                Poly(0.22f, 0.28f, 0.58f, 0.5f, 0.22f, 0.72f);
                ci.DrawRect(new Rect2(P(0.64f, 0.28f), new Vector2(0.12f, 0.44f) * s), color);
                break;
            case UiIcon.Menu:
                Line(0.24f, 0.32f, 0.76f, 0.32f);
                Line(0.24f, 0.50f, 0.76f, 0.50f);
                Line(0.24f, 0.68f, 0.76f, 0.68f);
                break;
            case UiIcon.Person:
                Disc(0.5f, 0.30f, 0.14f, color);
                Poly(0.24f, 0.86f, 0.28f, 0.58f, 0.40f, 0.50f, 0.60f, 0.50f, 0.72f, 0.58f, 0.76f, 0.86f);
                break;
            case UiIcon.Mood:
                Disc(0.5f, 0.5f, 0.38f, color);
                var ink = new Color(0.07f, 0.11f, 0.16f);
                Disc(0.37f, 0.40f, 0.05f, ink);
                Disc(0.63f, 0.40f, 0.05f, ink);
                float curve = mood >= 650 ? 0.10f : mood >= 450 ? 0f : -0.10f;
                var mouth = new[] { P(0.32f, 0.60f), P(0.5f, 0.60f + curve), P(0.68f, 0.60f) };
                ci.DrawPolyline(mouth, ink, w, true);
                break;
            case UiIcon.Path: // two parallel wiggly edges
                Polyline(0.20f, 0.85f, 0.30f, 0.55f, 0.55f, 0.40f, 0.62f, 0.12f);
                Polyline(0.40f, 0.88f, 0.48f, 0.62f, 0.73f, 0.47f, 0.80f, 0.14f);
                break;
            case UiIcon.Trail:
                Polyline(0.20f, 0.18f, 0.62f, 0.30f, 0.30f, 0.50f, 0.70f, 0.64f, 0.40f, 0.84f);
                break;
            case UiIcon.Parking:
                ci.DrawRect(new Rect2(P(0.18f, 0.18f), new Vector2(0.64f, 0.64f) * s), color, false, w);
                ci.DrawString(UiTheme.Bold, P(0.30f, 0.71f), "P", HorizontalAlignment.Center, s * 0.4f, (int)(s * 0.5f), color);
                break;
            case UiIcon.Demo: // sparkle
                Poly(0.5f, 0.12f, 0.58f, 0.42f, 0.88f, 0.5f, 0.58f, 0.58f, 0.5f, 0.88f, 0.42f, 0.58f, 0.12f, 0.5f, 0.42f, 0.42f);
                break;
            case UiIcon.Close:
                Line(0.28f, 0.28f, 0.72f, 0.72f);
                Line(0.72f, 0.28f, 0.28f, 0.72f);
                break;
            case UiIcon.ZoomIn or UiIcon.ZoomOut: // magnifier with + / −
                Circle(0.42f, 0.42f, 0.24f);
                Line(0.60f, 0.60f, 0.82f, 0.82f);
                Line(0.31f, 0.42f, 0.53f, 0.42f);
                if (icon == UiIcon.ZoomIn) Line(0.42f, 0.31f, 0.42f, 0.53f);
                break;
            case UiIcon.Berm: // banked turn seen from above: a curved wall around a bend
                ci.DrawArc(P(0.5f, 0.62f), 0.30f * s, Mathf.Pi, Mathf.Tau, 24, color, w * 2.2f, true);
                ci.DrawArc(P(0.5f, 0.62f), 0.14f * s, Mathf.Pi, Mathf.Tau, 16, color, w, true);
                Line(0.14f, 0.86f, 0.86f, 0.86f);
                break;
            case UiIcon.Rollers: // three bumps
                Polyline(0.08f, 0.70f, 0.18f, 0.48f, 0.30f, 0.70f, 0.40f, 0.48f, 0.52f, 0.70f, 0.62f, 0.48f, 0.74f, 0.70f, 0.84f, 0.48f, 0.92f, 0.70f);
                Line(0.06f, 0.80f, 0.94f, 0.80f);
                break;
            case UiIcon.Table: // trapezoid
                Poly(0.08f, 0.78f, 0.34f, 0.42f, 0.66f, 0.42f, 0.92f, 0.78f);
                break;
            case UiIcon.Double: // takeoff, gap, landing
                Poly(0.06f, 0.78f, 0.36f, 0.40f, 0.40f, 0.78f);
                Poly(0.58f, 0.78f, 0.62f, 0.40f, 0.94f, 0.78f);
                break;
            case UiIcon.WallRide: // planks standing up on a curve
                for (int i = 0; i < 5; i++)
                {
                    float x = 0.16f + i * 0.17f;
                    float top = 0.20f + Math.Abs(i - 2) * 0.06f;
                    ci.DrawRect(new Rect2(P(x, top), new Vector2(0.12f, 0.80f - top) * s), color);
                }
                break;
            case UiIcon.Kicker: // curved ramp with a rider's arc
                Poly(0.10f, 0.82f, 0.50f, 0.74f, 0.70f, 0.56f, 0.70f, 0.82f);
                ci.DrawArc(P(0.86f, 0.56f), 0.18f * s, -Mathf.Pi * 0.95f, -Mathf.Pi * 0.35f, 12, color, w, true);
                break;
            case UiIcon.Drop: // raised deck and a step down
                Poly(0.08f, 0.82f, 0.30f, 0.38f, 0.62f, 0.38f, 0.62f, 0.82f);
                Line(0.62f, 0.82f, 0.94f, 0.82f);
                Polyline(0.70f, 0.30f, 0.80f, 0.40f, 0.84f, 0.62f);
                break;
            case UiIcon.Follow: // target
                Circle(0.5f, 0.5f, 0.30f);
                Disc(0.5f, 0.5f, 0.10f, color);
                Line(0.5f, 0.06f, 0.5f, 0.2f);
                Line(0.5f, 0.8f, 0.5f, 0.94f);
                Line(0.06f, 0.5f, 0.2f, 0.5f);
                Line(0.8f, 0.5f, 0.94f, 0.5f);
                break;
        }
    }
}

/// <summary>A control that only draws an icon (for stat chips and cards).</summary>
public partial class IconView : Control
{
    private UiIcon _icon;
    private Color _color = UiTheme.Text;
    private int _mood = 700;

    public IconView() { }

    public IconView(UiIcon icon, float size, Color? color = null)
    {
        _icon = icon;
        _color = color ?? UiTheme.Text;
        CustomMinimumSize = new Vector2(size, size);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void Set(UiIcon icon, Color color, int mood = 700)
    {
        if (icon == _icon && color == _color && mood == _mood) return;
        _icon = icon;
        _color = color;
        _mood = mood;
        QueueRedraw();
    }

    public override void _Draw() => UiIcons.Draw(this, _icon, new Rect2(Vector2.Zero, Size), _color, _mood);
}

/// <summary>
/// A round icon button with an optional caption below (the bar's categories) or a small round toggle (speed). Shows
/// an accent ring when <see cref="Active"/>.
/// </summary>
public partial class RoundButton : Control
{
    private readonly UiIcon _icon;
    private readonly float _diameter;
    private readonly string _caption;
    private bool _hover;
    private bool _active;

    public RoundButton() { _caption = ""; }

    public RoundButton(UiIcon icon, float diameter, string caption, string tooltip, Action onPressed)
    {
        _icon = icon;
        _diameter = diameter;
        _caption = caption;
        TooltipText = tooltip;
        Pressed += onPressed;
        CustomMinimumSize = new Vector2(Math.Max(diameter, caption.Length == 0 ? 0 : 64), diameter + (caption.Length == 0 ? 0 : 18));
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        MouseEntered += () => { _hover = true; QueueRedraw(); };
        MouseExited += () => { _hover = false; QueueRedraw(); };
    }

    public event Action? Pressed;

    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            QueueRedraw();
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
        {
            Pressed?.Invoke();
            AcceptEvent();
        }
    }

    public override void _Draw()
    {
        var center = new Vector2(Size.X / 2, _diameter / 2);
        float r = _diameter / 2;
        var fill = _active ? UiTheme.AccentDark : _hover ? UiTheme.CardHover : UiTheme.Card;
        DrawCircle(center, r, fill);
        DrawArc(center, r - 1, 0, Mathf.Tau, 48, _active ? UiTheme.Accent : _hover ? new Color(1, 1, 1, 0.35f) : new Color(1, 1, 1, 0.12f), _active ? 2.5f : 1.5f, true);
        float icon = _diameter * 0.58f;
        UiIcons.Draw(this, _icon, new Rect2(center - new Vector2(icon, icon) / 2, new Vector2(icon, icon)), _active ? Colors.White : UiTheme.Text);
        if (_caption.Length > 0)
        {
            var color = _active ? UiTheme.Accent : _hover ? UiTheme.Text : UiTheme.TextDim;
            DrawString(UiTheme.Bold, new Vector2(0, _diameter + 15), _caption, HorizontalAlignment.Center, Size.X, 12, color);
        }
    }
}
