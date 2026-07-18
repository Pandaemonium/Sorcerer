extends SceneTree

const MAIN_SCENE := "res://Scenes/Main.tscn"
const JOURNAL_SCENE := "res://Scenes/Journal.tscn"


func _initialize() -> void:
	OS.set_environment("SORCERER_QUICKSTART", "1")
	call_deferred("_run")


func _run() -> void:
	change_scene_to_file(MAIN_SCENE)
	await _settle_layout()
	var before := _find_command_panel()
	if not _assert_panel_visible(before, "before journal"):
		quit(1)
		return

	change_scene_to_file(JOURNAL_SCENE)
	await _settle_layout()
	change_scene_to_file(MAIN_SCENE)
	await _settle_layout()
	var after := _find_command_panel()
	if not _assert_panel_visible(after, "after journal"):
		quit(1)
		return

	print("Journal return layout smoke passed: command panel bottom=%.1f design viewport=%.1f"
		% [after.get_global_rect().end.y, _design_viewport_height()])
	quit(0)


func _settle_layout() -> void:
	# Main deliberately defers map sizing so the complete VBox can establish its available height.
	for _frame in range(6):
		await process_frame


func _find_command_panel() -> Control:
	var spell_line := _find_spell_line(root)
	if spell_line == null:
		return null

	# LineEdit -> spell row -> command stack -> command PanelContainer.
	return spell_line.get_parent().get_parent().get_parent() as Control


func _find_spell_line(node: Node) -> LineEdit:
	if node is LineEdit and node.placeholder_text == "speak your spell...":
		return node

	for child in node.get_children():
		var found := _find_spell_line(child)
		if found != null:
			return found
	return null


func _assert_panel_visible(panel: Control, stage: String) -> bool:
	if panel == null:
		push_error("Journal return layout smoke: command panel missing %s." % stage)
		return false

	var rect := panel.get_global_rect()
	var viewport_height := _design_viewport_height()
	if rect.size.y <= 0.0 or rect.position.y < 0.0 or rect.end.y > viewport_height + 0.5:
		push_error("Journal return layout smoke: command panel outside design viewport %s: rect=%s height=%s"
			% [stage, rect, viewport_height])
		return false
	return true


func _design_viewport_height() -> float:
	# The dummy headless display reports a 64px physical window even though Control layout uses
	# the project's design viewport. The real window grows from this same 1440x900 baseline.
	return float(ProjectSettings.get_setting("display/window/size/viewport_height"))
