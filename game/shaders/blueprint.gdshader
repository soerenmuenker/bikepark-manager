// Planned ways (blueprints): see-through vertex colors (alpha from COLOR.a; the finished part of a way under
// construction is opaque), pulled towards the camera like the way ribbons (way.gdshader) so they sit on the terrain.
shader_type spatial;
render_mode blend_mix, cull_disabled, shadows_disabled, unshaded, depth_draw_never;

uniform float depth_bias = 0.002;

void vertex() {
	POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	POSITION.z += depth_bias * POSITION.w;
}

void fragment() {
	ALBEDO = COLOR.rgb;
	ALPHA = COLOR.a;
}
