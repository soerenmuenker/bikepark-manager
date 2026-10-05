// Terrain material: blends simple per-layer colors from the Sim's layer data, plus debug overlays.
// Vertex data (see TerrainMeshBuilder.cs):
//   COLOR = (tree density, rock, roots, water) in 0..1
//   UV2.x = TerrainSurface id (Grass 0, Forest 1, Roots 2, Rock 3, Water 4)
//   UV2.y = slope grade (rise / run; 1.0 = 45°)
// The overlay palettes match the SimRunner map renderer (src/Bikepark.SimRunner/Terrain/TerrainMapRenderer.cs).
shader_type spatial;
render_mode cull_disabled;

uniform int overlay_mode = 0; // 0 natural, 1 slope, 2 surface
uniform float tree_line_m = 1080.0;

varying vec4 layers;
varying float slope_grade;
varying flat float surface_id;
varying vec3 world_pos;

vec3 srgb(vec3 c) {
	return pow(c, vec3(2.2));
}

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x),
	           mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

vec3 slope_color(float grade) {
	if (grade < 0.141) return vec3(0.18, 0.55, 0.20); // < 8°
	if (grade < 0.268) return vec3(0.55, 0.75, 0.25); // < 15°
	if (grade < 0.466) return vec3(0.95, 0.85, 0.25); // < 25°
	if (grade < 0.700) return vec3(0.95, 0.55, 0.15); // < 35°
	if (grade < 1.000) return vec3(0.85, 0.20, 0.15); // < 45°
	return vec3(0.50, 0.15, 0.55);
}

vec3 surface_color(float id) {
	int i = int(round(id));
	if (i == 1) return vec3(0.15, 0.42, 0.18); // Forest
	if (i == 2) return vec3(0.55, 0.38, 0.20); // Roots
	if (i == 3) return vec3(0.60, 0.60, 0.62); // Rock
	if (i == 4) return vec3(0.20, 0.45, 0.85); // Water
	return vec3(0.55, 0.75, 0.35);             // Grass
}

void vertex() {
	layers = COLOR;
	slope_grade = UV2.y;
	surface_id = UV2.x;
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float n = value_noise(world_pos.xz * 0.12) * 0.6 + value_noise(world_pos.xz * 0.8) * 0.4;

	vec3 c;
	if (overlay_mode == 1) {
		c = slope_color(slope_grade);
	} else if (overlay_mode == 2) {
		c = surface_color(surface_id);
	} else {
		vec3 grass = vec3(0.42, 0.56, 0.24);
		vec3 meadow = vec3(0.58, 0.60, 0.40);
		vec3 forest_floor = vec3(0.24, 0.30, 0.14);
		vec3 roots = vec3(0.40, 0.29, 0.17);
		vec3 rock = vec3(0.56, 0.55, 0.52);
		vec3 water = vec3(0.18, 0.32, 0.42);

		c = mix(grass, meadow, smoothstep(tree_line_m - 30.0, tree_line_m + 20.0, world_pos.y));
		c = mix(c, forest_floor, layers.r);
		c = mix(c, roots, layers.b * 0.8);
		c = mix(c, rock, smoothstep(0.25, 0.6, layers.g + (n - 0.5) * 0.25));
		c = mix(c, water, step(0.04, layers.a));
		c *= 0.85 + 0.3 * n;
	}

	ALBEDO = srgb(c);
	ROUGHNESS = 0.95;
}
