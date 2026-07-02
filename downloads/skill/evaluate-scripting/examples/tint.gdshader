// Shaders live in their own .gdshader files (never inline in Lua) so they can
// hot-reload in place — edit `tint` below while the game runs and the crate
// recolors live.
shader_type spatial;

uniform vec3 tint = vec3(1.0, 0.4, 0.1);

void fragment() {
	ALBEDO = tint;
}
