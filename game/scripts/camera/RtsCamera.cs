using Bikepark.Game.Terrain;
using Godot;

namespace Bikepark.Game.Camera;

/// <summary>
/// RTS-style camera orbiting a focus point on the terrain.
/// <list type="bullet">
///   <item>Pan: WASD / arrow keys, screen edges, or middle-mouse drag.</item>
///   <item>Zoom: mouse wheel (or +/-).</item>
///   <item>Orbit: Q / E, or right-mouse drag (horizontal = rotate, vertical = tilt).</item>
/// </list>
/// The focus stays on the map and the camera never goes below the terrain.
/// </summary>
public partial class RtsCamera : Node3D
{
    [Export] public NodePath TerrainPath { get; set; } = "../TerrainView";
    [Export] public float MinDistance { get; set; } = 15f;
    [Export] public float MaxDistance { get; set; } = 1100f;
    [Export] public float StartDistance { get; set; } = 650f;

    /// <summary>Keyboard/edge pan speed as a fraction of the current zoom distance per second.</summary>
    [Export] public float PanSpeed { get; set; } = 0.9f;

    [Export] public float OrbitSpeed { get; set; } = 1.6f;
    [Export] public float MouseOrbitSensitivity { get; set; } = 0.006f;
    [Export] public float ZoomStep { get; set; } = 1.15f;
    [Export] public bool EdgePanEnabled { get; set; } = true;
    [Export] public float EdgePanMarginPx { get; set; } = 8f;
    [Export] public float Smoothing { get; set; } = 12f;
    [Export] public float MinPitchDegrees { get; set; } = 15f;
    [Export] public float MaxPitchDegrees { get; set; } = 85f;
    [Export] public float GroundClearance { get; set; } = 2f;

    private TerrainView _terrain = null!;
    private Vector2 _focus, _targetFocus;
    private float _yaw, _targetYaw;
    private float _pitch, _targetPitch;
    private float _distance, _targetDistance;
    private bool _dragPan, _dragOrbit;

    public Camera3D Camera { get; private set; } = null!;

    public override void _Ready()
    {
        _terrain = GetNode<TerrainView>(TerrainPath);
        Camera = new Camera3D { Name = "Camera", Near = 0.3f, Far = 6000f, Fov = 55f };
        AddChild(Camera);
        Camera.MakeCurrent();

        // Start south-west of the map center, looking north-east at the mountain.
        float half = (_terrain.Grid?.SizeMeters ?? 1000) / 2f;
        _focus = _targetFocus = new Vector2(half, half);
        _yaw = _targetYaw = Mathf.DegToRad(-35f);
        _pitch = _targetPitch = Mathf.DegToRad(40f);
        _distance = _targetDistance = StartDistance;
        UpdateTransform();
    }

    /// <summary>Moves the focus to a world position (meters).</summary>
    public void FocusOn(Vector2 xz) => _targetFocus = ClampToMap(xz);

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                Zoom(1f / ZoomStep);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                Zoom(ZoomStep);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } button:
                _dragPan = button.Pressed;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } button:
                _dragOrbit = button.Pressed;
                break;
            case InputEventMouseMotion motion when _dragPan:
                // Drag the ground: one viewport height ≈ the visible ground depth at this distance.
                float metersPerPixel = _targetDistance * 1.2f / GetViewport().GetVisibleRect().Size.Y;
                Pan(new Vector2(-motion.Relative.X, -motion.Relative.Y) * metersPerPixel);
                break;
            case InputEventMouseMotion motion when _dragOrbit:
                _targetYaw -= motion.Relative.X * MouseOrbitSensitivity;
                _targetPitch = ClampPitch(_targetPitch + motion.Relative.Y * MouseOrbitSensitivity);
                break;
            case InputEventKey { Pressed: true, PhysicalKeycode: Key.Equal or Key.KpAdd }:
                Zoom(1f / ZoomStep);
                break;
            case InputEventKey { Pressed: true, PhysicalKeycode: Key.Minus or Key.KpSubtract }:
                Zoom(ZoomStep);
                break;
            default:
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        var input = Vector2.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsPhysicalKeyPressed(Key.Up)) input.Y -= 1;
        if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsPhysicalKeyPressed(Key.Down)) input.Y += 1;
        if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsPhysicalKeyPressed(Key.Left)) input.X -= 1;
        if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsPhysicalKeyPressed(Key.Right)) input.X += 1;
        input += EdgePanInput();
        if (input != Vector2.Zero)
            Pan(input.Normalized() * PanSpeed * _targetDistance * dt);

        if (Input.IsPhysicalKeyPressed(Key.Q)) _targetYaw += OrbitSpeed * dt;
        if (Input.IsPhysicalKeyPressed(Key.E)) _targetYaw -= OrbitSpeed * dt;

        float t = 1f - MathF.Exp(-Smoothing * dt);
        _focus = _focus.Lerp(_targetFocus, t);
        _yaw = Mathf.Lerp(_yaw, _targetYaw, t);
        _pitch = Mathf.Lerp(_pitch, _targetPitch, t);
        _distance = Mathf.Lerp(_distance, _targetDistance, t);
        UpdateTransform();
    }

    private void UpdateTransform()
    {
        var focus = new Vector3(_focus.X, _terrain.HeightAt(_focus.X, _focus.Y), _focus.Y);
        var offset = new Vector3(
            MathF.Sin(_yaw) * MathF.Cos(_pitch),
            MathF.Sin(_pitch),
            MathF.Cos(_yaw) * MathF.Cos(_pitch)) * _distance;
        var position = focus + offset;
        position.Y = MathF.Max(position.Y, _terrain.HeightAt(position.X, position.Z) + GroundClearance);

        Camera.GlobalPosition = position;
        Camera.LookAt(focus, Vector3.Up);
    }

    /// <summary>Pans in screen-relative directions: X = right, Y = towards the viewer.</summary>
    private void Pan(Vector2 screenDelta)
    {
        var forward = new Vector2(-MathF.Sin(_targetYaw), -MathF.Cos(_targetYaw));
        var right = new Vector2(MathF.Cos(_targetYaw), -MathF.Sin(_targetYaw));
        _targetFocus = ClampToMap(_targetFocus + right * screenDelta.X - forward * screenDelta.Y);
    }

    private void Zoom(float factor) =>
        _targetDistance = Mathf.Clamp(_targetDistance * factor, MinDistance, MaxDistance);

    private Vector2 EdgePanInput()
    {
        if (!EdgePanEnabled || _dragPan || _dragOrbit || !DisplayServer.WindowIsFocused())
            return Vector2.Zero;

        var rect = GetViewport().GetVisibleRect();
        var mouse = GetViewport().GetMousePosition();
        if (!rect.HasPoint(mouse))
            return Vector2.Zero;

        var input = Vector2.Zero;
        if (mouse.X <= EdgePanMarginPx) input.X -= 1;
        if (mouse.X >= rect.Size.X - EdgePanMarginPx) input.X += 1;
        if (mouse.Y <= EdgePanMarginPx) input.Y -= 1;
        if (mouse.Y >= rect.Size.Y - EdgePanMarginPx) input.Y += 1;
        return input;
    }

    private float ClampPitch(float pitch) =>
        Mathf.Clamp(pitch, Mathf.DegToRad(MinPitchDegrees), Mathf.DegToRad(MaxPitchDegrees));

    private Vector2 ClampToMap(Vector2 p)
    {
        float size = _terrain.Grid?.SizeMeters ?? 1000;
        return new Vector2(Mathf.Clamp(p.X, 0, size), Mathf.Clamp(p.Y, 0, size));
    }
}
