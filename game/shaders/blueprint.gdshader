// Planned ways (blueprints): see-through vertex colors (alpha from COLOR.a; the finished part of a way under
// construction is opaque), pulled towards the camera like the way ribbons (way.gdshader) so they sit on the terrain.
shader_type spatial;
render_mode blend_mix, cull_disabled, shadows_disabled, unshaded, depth_draw_never;

// The pull is in meters along the view ray (a few cm close up, a fixed share of the distance far away), never a
// constant in clip space: with reversed Z that pulled whole meters at mid zoom and drew roads over the riders on them.
uniform float pull_min = 0.07;
uniform float pull_per_meter = 0.005;

void vertex() {
	vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	float dist = length(view.xyz);
	view.xyz -= view.xyz / max(dist, 0.001) * (pull_min + pull_per_meter * dist) * UV.x;
	POSITION = PROJECTION_MATRIX * view;
	// UV.x is 0 over rock: the rock stands in front
}

void fragment() {
	ALBEDO = COLOR.rgb;
	ALPHA = COLOR.a;
}
