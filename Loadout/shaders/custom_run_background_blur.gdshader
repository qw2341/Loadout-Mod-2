shader_type canvas_item;

uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear;
uniform float radius : hint_range(0.0, 8.0) = 3.0;

void fragment() {
	vec2 step_size = radius / vec2(textureSize(screen_texture, 0));
	vec4 color = texture(screen_texture, SCREEN_UV) * 0.25;
	color += texture(screen_texture, SCREEN_UV + vec2(step_size.x, 0.0)) * 0.125;
	color += texture(screen_texture, SCREEN_UV - vec2(step_size.x, 0.0)) * 0.125;
	color += texture(screen_texture, SCREEN_UV + vec2(0.0, step_size.y)) * 0.125;
	color += texture(screen_texture, SCREEN_UV - vec2(0.0, step_size.y)) * 0.125;
	color += texture(screen_texture, SCREEN_UV + step_size) * 0.0625;
	color += texture(screen_texture, SCREEN_UV - step_size) * 0.0625;
	color += texture(screen_texture, SCREEN_UV + vec2(step_size.x, -step_size.y)) * 0.0625;
	color += texture(screen_texture, SCREEN_UV + vec2(-step_size.x, step_size.y)) * 0.0625;
	COLOR = color;
}
