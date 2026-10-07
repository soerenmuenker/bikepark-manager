// Trail features (berms, jumps, wood features): lit vertex colors (linear), pulled towards the camera a little more
// than the way ribbons (way.gdshader), so a jump built on a trail is drawn over the trail's ribbon.
shader_type spatial;
render_mode cull_disabled;

uniform float depth_bias = 0.0025;
uniform float roughness_value = 0.95;

void vertex() {
	POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	POSITION.z += depth_bias * POSITION.w;
}

void fragment() {
	ALBEDO = COLOR.rgb;
	ROUGHNESS = roughness_value;
}
