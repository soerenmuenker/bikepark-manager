// Trail features (berms, jumps, wood features): lit vertex colors (linear), pulled towards the camera a little more
// than the way ribbons (way.gdshader), so a jump built on a trail is drawn over the trail's ribbon.
shader_type spatial;
render_mode cull_disabled;

// The pull is in meters along the view ray (a few cm close up, a fixed share of the distance far away), never a
// constant in clip space: with reversed Z that pulled whole meters at mid zoom and drew roads over the riders on them.
uniform float pull_min = 0.08;
uniform float pull_per_meter = 0.006;
uniform float roughness_value = 0.95;

void vertex() {
	vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	float dist = length(view.xyz);
	view.xyz -= view.xyz / max(dist, 0.001) * (pull_min + pull_per_meter * dist);
	POSITION = PROJECTION_MATRIX * view;
}

void fragment() {
	ALBEDO = COLOR.rgb;
	ROUGHNESS = roughness_value;
}
