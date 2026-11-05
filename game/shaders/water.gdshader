// Lakes, rivers and brooks: no simulation, just moving value noise over a flat colour. Opaque and without any screen
// reading, so it costs about what a ribbon costs. UV is (along the flow in meters, across) for streams, world meters for lakes.
shader_type spatial;
render_mode cull_disabled, shadows_disabled, unshaded;

uniform vec3 deep_color = vec3(0.015, 0.075, 0.115);
uniform vec3 light_color = vec3(0.07, 0.24, 0.33);
uniform float flow_speed = 0.0;
uniform float ripple_scale = 0.35;
uniform float depth_bias = 0.0004;

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

void vertex() {
	POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	POSITION.z += depth_bias * POSITION.w;
}

void fragment() {
	vec2 p = UV * ripple_scale;
	float a = noise(p + vec2(-TIME * flow_speed, TIME * 0.05));
	float b = noise(p * 2.3 + vec2(-TIME * flow_speed * 1.6, -TIME * 0.07));
	float glint = smoothstep(0.62, 0.8, a * 0.6 + b * 0.4);
	ALBEDO = mix(deep_color, light_color, a * 0.55 + glint * 0.3);
}
