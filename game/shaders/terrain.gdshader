// Terrain material: blends simple per-layer colors from the Sim's layer data, plus debug overlays.
// Vertex data (see TerrainMeshBuilder.cs):
//   COLOR = (tree density, rock, roots, water) in 0..1
//   UV2.x = TerrainSurface id (Grass 0, Forest 1, Roots 2, Rock 3, Water 4)
//   UV2.y = slope grade (rise / run; 1.0 = 45°)
//   UV.x = 1 in a stone field (RockFormation.DenseStones): a pale scree patch, so a field reads apart from boulders
// The overlay palettes match the SimRunner map renderer (src/Bikepark.SimRunner/Terrain/TerrainMapRenderer.cs).
shader_type spatial;
render_mode cull_disabled;

uniform int overlay_mode = 0; // 0 natural, 1 slope, 2 surface
uniform float tree_line_m = 1080.0;
uniform bool desert = false; // TerrainBiome.Desert: sand, hardpan and red sandstone
uniform sampler2D own_mask : filter_linear, repeat_disable; // R: 1 on land the park owns (TerrainView.UpdateOwnership)
uniform vec2 map_size_m = vec2(1000.0);
uniform float land_dim = 0.0; // 1: grey out land the park doesn't own (play view)

varying vec4 layers;
varying float slope_grade;
varying flat float surface_id;
varying vec3 world_pos;
varying float scree;

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

// Cellular pebbles: (distance to the nearest pebble centre, gap to the next one, the pebble's own random value).
vec3 pebbles(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	float d1 = 8.0, d2 = 8.0, id = 0.0;
	for (int y = -1; y <= 1; y++) {
		for (int x = -1; x <= 1; x++) {
			vec2 g = vec2(float(x), float(y));
			vec2 o = vec2(hash(i + g), hash(i + g + vec2(17.3, 5.1)));
			float d = length(g + o - f);
			if (d < d1) { d2 = d1; d1 = d; id = hash(i + g + vec2(3.7, 9.2)); }
			else if (d < d2) { d2 = d; }
		}
	}
	return vec3(d1, d2 - d1, id);
}

// Scree: pebbles of two sizes in light and dark shades, gritty dark gaps between them.
vec3 scree_color(vec2 xz, vec3 light, vec3 dark, vec3 grit) {
	// Warp the cells a little so they never line up like paving.
	vec2 p = xz + vec2(value_noise(xz * 0.9), value_noise(xz * 0.9 + 13.0)) * 0.8;
	vec3 big = pebbles(p * 2.3);
	vec3 small = pebbles(p * 6.5 + vec2(31.0, 7.0));
	vec3 c = mix(light, dark, big.z);
	c = mix(c, mix(light, dark, small.z), 0.5);
	c *= 0.85 + 0.3 * value_noise(xz * 0.35); // lighter and darker drifts
	float gap = min(smoothstep(0.0, 0.07, big.y), smoothstep(0.0, 0.05, small.y));
	return mix(mix(grit, c, 0.45), c, gap); // faint gaps of grit, not grout
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
	scree = UV.x;
}

void fragment() {
	float n = value_noise(world_pos.xz * 0.12) * 0.6 + value_noise(world_pos.xz * 0.8) * 0.4;

	vec3 c;
	if (overlay_mode == 1) {
		c = slope_color(slope_grade);
	} else if (overlay_mode == 2) {
		c = surface_color(surface_id);
	} else if (desert) {
		vec3 sand = vec3(0.66, 0.50, 0.33);
		vec3 hardpan = vec3(0.58, 0.36, 0.23);
		vec3 plant_soil = vec3(0.42, 0.32, 0.22);
		vec3 sandstone = vec3(0.62, 0.36, 0.24);
		vec3 pale_band = vec3(0.74, 0.54, 0.38);
		vec3 water = vec3(0.20, 0.17, 0.12);

		c = mix(sand, hardpan, smoothstep(0.12, 0.40, slope_grade + (n - 0.5) * 0.45));
		c = mix(c, plant_soil, min(layers.r * 1.2, 1.0));
		// Sedimentary layers: horizontal bands, a little wavy.
		float y = world_pos.y + (n - 0.5) * 3.0;
		float band = sin(y * 1.1) * 0.5 + sin(y * 0.37) * 0.5;
		c = mix(c, mix(sandstone, pale_band, smoothstep(0.1, 0.7, band) * 0.6), smoothstep(0.25, 0.6, layers.g + (n - 0.5) * 0.25));
		c = mix(c, scree_color(world_pos.xz, vec3(0.80, 0.64, 0.48), vec3(0.50, 0.34, 0.24), vec3(0.36, 0.25, 0.18)), smoothstep(0.35, 0.75, scree + (n - 0.5) * 0.5) * 0.8);
		c = mix(c, water, smoothstep(0.02, 0.2, layers.a));
		c *= 0.85 + 0.3 * n;
	} else {
		vec3 grass = vec3(0.42, 0.56, 0.24);
		vec3 meadow = vec3(0.58, 0.60, 0.40);
		vec3 forest_floor = vec3(0.24, 0.30, 0.14);
		vec3 roots = vec3(0.40, 0.29, 0.17);
		vec3 rock = vec3(0.56, 0.55, 0.52);
		vec3 water = vec3(0.20, 0.17, 0.12); // the bed and wet bank: SceneryView draws the water surface on top

		c = mix(grass, meadow, smoothstep(tree_line_m - 30.0, tree_line_m + 20.0, world_pos.y));
		c = mix(c, forest_floor, layers.r);
		c = mix(c, roots, layers.b * 0.8);
		c = mix(c, rock, smoothstep(0.25, 0.6, layers.g + (n - 0.5) * 0.25));
		// Scree: pale pebbles with dark grit between them, so a stone field never looks like boulder ground.
		c = mix(c, scree_color(world_pos.xz, vec3(0.70, 0.68, 0.63), vec3(0.40, 0.39, 0.37), vec3(0.30, 0.29, 0.26)), smoothstep(0.35, 0.75, scree + (n - 0.5) * 0.5) * 0.85);
		c = mix(c, water, smoothstep(0.02, 0.2, layers.a));
		c *= 0.85 + 0.3 * n;
	}

	if (land_dim > 0.0) {
		float owned = texture(own_mask, world_pos.xz / map_size_m).r;
		float grey = dot(c, vec3(0.299, 0.587, 0.114));
		c = mix(c, vec3(grey) * 0.55, (1.0 - owned) * land_dim * 0.85);
	}

	ALBEDO = srgb(c);
	ROUGHNESS = 0.95;
}
