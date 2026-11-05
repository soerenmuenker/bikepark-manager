// Ribbons for paths, trails and the build preview: vertex colors, pulled slightly towards the camera in depth so
// they stay visible on top of the terrain (including its coarser far-LOD mesh) without floating visibly.
shader_type spatial;
render_mode cull_disabled, shadows_disabled, unshaded;

// The pull is in meters along the view ray (a few cm close up, a fixed share of the distance far away), never a
// constant in clip space: with reversed Z that pulled whole meters at mid zoom and drew roads over the riders on them.
uniform float pull_min = 0.05;
uniform float pull_per_meter = 0.004;
uniform float roughness_value = 0.95;

void vertex() {
	vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	float dist = length(view.xyz);
	view.xyz -= view.xyz / max(dist, 0.001) * (pull_min + pull_per_meter * dist) * UV.x;
	POSITION = PROJECTION_MATRIX * view;
	// UV.x is 0 over rock: the rock stands in front
}

void fragment() {
	ALBEDO = COLOR.rgb;
	ROUGHNESS = roughness_value;
}
