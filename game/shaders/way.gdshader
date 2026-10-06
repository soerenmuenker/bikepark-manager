// Ribbons for paths, trails and the build preview: vertex colors, pulled slightly towards the camera in depth so
// they stay visible on top of the terrain (including its coarser far-LOD mesh) without floating visibly.
shader_type spatial;
render_mode cull_disabled, shadows_disabled, unshaded;

uniform float depth_bias = 0.0015;
uniform float roughness_value = 0.95;

void vertex() {
	POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	POSITION.z += depth_bias * POSITION.w;
}

void fragment() {
	ALBEDO = COLOR.rgb;
	ROUGHNESS = roughness_value;
}
