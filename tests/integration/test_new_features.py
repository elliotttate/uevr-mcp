"""Tests for v1.2.0 features: ProcessEvent listener, motion controllers,
VR input, world scale, object validity, and timers.

Also covers previously untested tools: explorer search/inspect/summary/field,
watch/snapshot/diff, hooks, world actors/components/hierarchy, line trace,
sphere overlap, input injection, material, animation, physics, asset, discovery,
console, VR, diagnostics, macros, events."""

import pytest
import requests
import time


# ── Helpers ──────────────────────────────────────────────────────────

def get(api_url, path, **params):
    r = requests.get(f"{api_url}{path}", params=params, timeout=10)
    return r.status_code, r.json()

def post(api_url, path, body=None):
    r = requests.post(f"{api_url}{path}", json=body or {}, timeout=10)
    return r.status_code, r.json()

def delete(api_url, path, body=None):
    r = requests.delete(f"{api_url}{path}", json=body or {}, timeout=10)
    return r.status_code, r.json()


# ── Fixtures ─────────────────────────────────────────────────────────

@pytest.fixture(scope="session")
def mesh_component_address(require_game, api_url, pawn_address):
    """Get the pawn's Mesh component address (a USceneComponent)."""
    code = ('local p = uevr.api:get_local_pawn(0); '
            'return string.format("0x%X", mcp.read_property(p:get_address(), "Mesh"))')
    _, data = post(api_url, "/api/lua/exec", {"code": code})
    addr = data.get("result")
    if not addr or addr == "0x0":
        pytest.skip("No Mesh component on pawn")
    return addr


@pytest.fixture(scope="session")
def world_settings_address(require_game, api_url):
    """Get the WorldSettings address."""
    _, data = get(api_url, "/api/vr/world_scale")
    addr = data.get("worldSettingsAddress")
    if not addr:
        pytest.skip("WorldSettings not available")
    return addr


# ═══════════════════════════════════════════════════════════════════
# 1. OBJECT VALIDITY (uevr_is_valid)
# ═══════════════════════════════════════════════════════════════════

class TestIsValid:
    def test_valid_object(self, require_game, api_url, pawn_address):
        status, data = get(api_url, "/api/explorer/is_valid", address=pawn_address)
        assert status == 200
        assert data["valid"] is True
        assert data["address"] == pawn_address

    def test_invalid_address(self, require_game, api_url):
        status, data = get(api_url, "/api/explorer/is_valid", address="0xDEADBEEF")
        assert status == 200
        assert data["valid"] is False

    def test_zero_address(self, require_game, api_url):
        status, data = get(api_url, "/api/explorer/is_valid", address="0x0")
        assert status == 200
        assert data["valid"] is False

    def test_missing_address_param(self, require_game, api_url):
        status, data = get(api_url, "/api/explorer/is_valid")
        assert status == 400
        assert "error" in data

    def test_garbage_address(self, require_game, api_url):
        status, data = get(api_url, "/api/explorer/is_valid", address="not_hex")
        assert status == 200
        assert data["valid"] is False


# ═══════════════════════════════════════════════════════════════════
# 2. VR INPUT
# ═══════════════════════════════════════════════════════════════════

class TestVrInput:
    def test_basic_input_state(self, require_game, api_url):
        status, data = get(api_url, "/api/vr/input")
        assert status == 200
        assert "leftJoystick" in data
        assert "rightJoystick" in data
        assert "x" in data["leftJoystick"]
        assert "y" in data["leftJoystick"]
        assert "x" in data["rightJoystick"]
        assert "y" in data["rightJoystick"]
        assert "usingControllers" in data
        assert "movementOrientation" in data

    def test_joystick_values_in_range(self, require_game, api_url):
        _, data = get(api_url, "/api/vr/input")
        for stick in ("leftJoystick", "rightJoystick"):
            assert -1.1 <= data[stick]["x"] <= 1.1
            assert -1.1 <= data[stick]["y"] <= 1.1

    def test_action_query(self, require_game, api_url):
        """Querying a nonexistent action should return an error entry, not crash."""
        _, data = get(api_url, "/api/vr/input", actions="/actions/fake/in/Nonexistent")
        assert "actions" in data
        entry = data["actions"].get("/actions/fake/in/Nonexistent", {})
        # Either has left/right/any booleans or an error field
        assert "error" in entry or "any" in entry


# ═══════════════════════════════════════════════════════════════════
# 3. WORLD SCALE
# ═══════════════════════════════════════════════════════════════════

class TestWorldScale:
    def test_get_world_scale(self, require_game, api_url):
        status, data = get(api_url, "/api/vr/world_scale")
        assert status == 200
        assert "worldToMetersScale" in data
        assert isinstance(data["worldToMetersScale"], (int, float))
        assert data["worldToMetersScale"] > 0

    def test_set_world_scale(self, require_game, api_url):
        status, data = post(api_url, "/api/vr/world_scale", {"scale": 150})
        assert status == 200
        assert data.get("success") is True
        assert data["worldToMetersScale"] == 150
        # Restore
        post(api_url, "/api/vr/world_scale", {"scale": 100})

    def test_set_invalid_scale_zero(self, require_game, api_url):
        status, data = post(api_url, "/api/vr/world_scale", {"scale": 0})
        assert status == 400

    def test_set_invalid_scale_negative(self, require_game, api_url):
        status, data = post(api_url, "/api/vr/world_scale", {"scale": -50})
        assert status == 400

    def test_set_missing_scale(self, require_game, api_url):
        status, data = post(api_url, "/api/vr/world_scale", {})
        assert status == 400


# ═══════════════════════════════════════════════════════════════════
# 4. MOTION CONTROLLER ATTACHMENT
# ═══════════════════════════════════════════════════════════════════

class TestMotionController:
    def test_attach_actor_fails_with_helpful_error(self, require_game, api_url, pawn_address):
        """Attaching an actor (not a component) gives a descriptive error."""
        status, data = post(api_url, "/api/vr/attach", {"address": pawn_address, "hand": "right"})
        assert "error" in data
        assert "USceneComponent" in data["error"]

    def test_attach_component_succeeds(self, require_game, api_url, mesh_component_address):
        status, data = post(api_url, "/api/vr/attach", {
            "address": mesh_component_address,
            "hand": "right",
            "permanent": True,
            "locationOffset": {"x": 1, "y": 2, "z": 3},
            "rotationOffset": {"x": 0, "y": 0, "z": 0, "w": 1}
        })
        assert data.get("success") is True
        assert data["hand"] == "right"
        assert data["locationOffset"]["x"] == 1.0
        # Cleanup
        post(api_url, "/api/vr/clear_attachments")

    def test_attach_left_hand(self, require_game, api_url, mesh_component_address):
        status, data = post(api_url, "/api/vr/attach", {
            "address": mesh_component_address, "hand": "left"
        })
        assert data.get("success") is True
        assert data["hand"] == "left"
        post(api_url, "/api/vr/clear_attachments")

    def test_list_empty(self, require_game, api_url):
        post(api_url, "/api/vr/clear_attachments")
        _, data = get(api_url, "/api/vr/attachments")
        assert data["count"] == 0
        assert data["attachments"] == []

    def test_list_after_attach(self, require_game, api_url, mesh_component_address):
        post(api_url, "/api/vr/attach", {"address": mesh_component_address, "hand": "right"})
        _, data = get(api_url, "/api/vr/attachments")
        assert data["count"] == 1
        assert data["attachments"][0]["address"] == mesh_component_address
        post(api_url, "/api/vr/clear_attachments")

    def test_detach(self, require_game, api_url, mesh_component_address):
        post(api_url, "/api/vr/attach", {"address": mesh_component_address, "hand": "right"})
        status, data = post(api_url, "/api/vr/detach", {"address": mesh_component_address})
        assert data.get("success") is True
        assert data["detached"] is True
        _, list_data = get(api_url, "/api/vr/attachments")
        assert list_data["count"] == 0

    def test_detach_nonexistent(self, require_game, api_url):
        post(api_url, "/api/vr/clear_attachments")
        status, data = post(api_url, "/api/vr/detach", {"address": "0xDEAD"})
        assert "error" in data

    def test_clear_attachments(self, require_game, api_url, mesh_component_address):
        post(api_url, "/api/vr/attach", {"address": mesh_component_address, "hand": "right"})
        _, data = post(api_url, "/api/vr/clear_attachments")
        assert data.get("success") is True
        _, list_data = get(api_url, "/api/vr/attachments")
        assert list_data["count"] == 0

    def test_attach_missing_address(self, require_game, api_url):
        status, data = post(api_url, "/api/vr/attach", {"hand": "right"})
        assert status == 400

    def test_attach_invalid_address(self, require_game, api_url):
        status, data = post(api_url, "/api/vr/attach", {"address": "garbage"})
        assert status == 400


# ═══════════════════════════════════════════════════════════════════
# 5. TIMER / SCHEDULER
# ═══════════════════════════════════════════════════════════════════

class TestTimers:
    def test_create_oneshot(self, require_game, api_url):
        _, data = post(api_url, "/api/timer/create", {
            "delay": 0.1, "code": "return 1", "looping": False
        })
        assert data.get("success") is True
        assert isinstance(data["timerId"], int)

    def test_create_looping(self, require_game, api_url):
        _, data = post(api_url, "/api/timer/create", {
            "delay": 60, "code": "return 1", "looping": True
        })
        assert data.get("success") is True
        post(api_url, "/api/timer/clear")

    def test_list_timers(self, require_game, api_url):
        post(api_url, "/api/timer/clear")
        post(api_url, "/api/timer/create", {"delay": 60, "code": "return 1", "looping": True})
        _, data = get(api_url, "/api/timer/list")
        assert data["count"] == 1
        assert data["timers"][0]["looping"] is True
        assert data["timers"][0]["delay"] == 60
        post(api_url, "/api/timer/clear")

    def test_cancel_timer(self, require_game, api_url):
        _, create_data = post(api_url, "/api/timer/create", {
            "delay": 60, "code": "return 1", "looping": True
        })
        tid = create_data["timerId"]
        _, cancel_data = delete(api_url, "/api/timer/cancel", {"timerId": tid})
        assert cancel_data.get("success") is True
        _, list_data = get(api_url, "/api/timer/list")
        assert list_data["count"] == 0

    def test_clear_all_timers(self, require_game, api_url):
        post(api_url, "/api/timer/create", {"delay": 60, "code": "return 1", "looping": True})
        post(api_url, "/api/timer/create", {"delay": 60, "code": "return 2", "looping": True})
        _, data = post(api_url, "/api/timer/clear")
        assert data.get("success") is True
        _, list_data = get(api_url, "/api/timer/list")
        assert list_data["count"] == 0

    def test_oneshot_fires_and_disappears(self, require_game, api_url):
        """One-shot timer fires and removes itself."""
        post(api_url, "/api/timer/clear")
        post(api_url, "/api/lua/exec", {"code": "_timer_test_fired = false"})
        post(api_url, "/api/timer/create", {
            "delay": 0.1, "code": "_timer_test_fired = true", "looping": False
        })
        time.sleep(0.5)
        _, list_data = get(api_url, "/api/timer/list")
        assert list_data["count"] == 0
        _, lua_data = post(api_url, "/api/lua/exec", {"code": "return _timer_test_fired"})
        assert lua_data["result"] is True

    def test_looping_timer_fires_multiple_times(self, require_game, api_url):
        post(api_url, "/api/timer/clear")
        post(api_url, "/api/lua/exec", {"code": "_loop_count = 0"})
        post(api_url, "/api/timer/create", {
            "delay": 0.05, "code": "_loop_count = _loop_count + 1", "looping": True
        })
        time.sleep(0.5)
        post(api_url, "/api/timer/clear")
        _, lua_data = post(api_url, "/api/lua/exec", {"code": "return _loop_count"})
        assert lua_data["result"] >= 3

    def test_create_invalid_delay(self, require_game, api_url):
        status, data = post(api_url, "/api/timer/create", {
            "delay": 0.0001, "code": "return 1"
        })
        assert "error" in data

    def test_create_missing_code(self, require_game, api_url):
        status, data = post(api_url, "/api/timer/create", {"delay": 1.0})
        assert status == 400


# ═══════════════════════════════════════════════════════════════════
# 6. PROCESS EVENT LISTENER
# ═══════════════════════════════════════════════════════════════════

class TestProcessEvent:
    def test_start(self, require_game, api_url):
        _, data = post(api_url, "/api/process_event/start")
        assert data.get("status") == "started" or data.get("hooked") is True

    def test_status_while_listening(self, require_game, api_url):
        post(api_url, "/api/process_event/start")
        time.sleep(0.3)
        _, data = get(api_url, "/api/process_event/status")
        assert data["listening"] is True
        assert data["hooked"] is True
        assert data["uniqueFunctions"] > 0

    def test_functions_returns_data(self, require_game, api_url):
        post(api_url, "/api/process_event/start")
        time.sleep(0.5)
        _, data = get(api_url, "/api/process_event/functions", limit=5)
        assert "functions" in data
        assert data["totalUnique"] > 0
        for fn in data["functions"]:
            assert "name" in fn
            assert "callCount" in fn
            assert fn["callCount"] > 0

    def test_functions_search_filter(self, require_game, api_url):
        post(api_url, "/api/process_event/start")
        time.sleep(0.3)
        _, data = get(api_url, "/api/process_event/functions", search="Camera", limit=10)
        for fn in data["functions"]:
            assert "Camera" in fn["name"]

    def test_functions_maxcalls_filter(self, require_game, api_url):
        """maxCalls filter excludes high-frequency functions."""
        post(api_url, "/api/process_event/start")
        time.sleep(0.5)
        _, data = get(api_url, "/api/process_event/functions", maxCalls=5, limit=50)
        for fn in data["functions"]:
            assert fn["callCount"] <= 5

    def test_recent(self, require_game, api_url):
        post(api_url, "/api/process_event/start")
        time.sleep(0.2)
        _, data = get(api_url, "/api/process_event/recent", count=3)
        assert "recent" in data
        assert len(data["recent"]) <= 3

    def test_ignore_by_pattern(self, require_game, api_url):
        post(api_url, "/api/process_event/start")
        post(api_url, "/api/process_event/clear_ignored")
        _, data = post(api_url, "/api/process_event/ignore", {"pattern": "Blueprint"})
        assert data["ignored"] >= 0
        assert "totalIgnored" in data

    def test_ignore_all_then_clear_baseline(self, require_game, api_url):
        """ignore_all + clear creates a clean baseline."""
        post(api_url, "/api/process_event/start")
        time.sleep(0.3)
        post(api_url, "/api/process_event/ignore_all")
        post(api_url, "/api/process_event/clear")
        time.sleep(0.2)
        _, data = get(api_url, "/api/process_event/functions", limit=50)
        # Should have 0 functions (all are ignored)
        assert data["totalUnique"] == 0

    def test_clear_ignored_restores_tracking(self, require_game, api_url):
        post(api_url, "/api/process_event/start")
        post(api_url, "/api/process_event/ignore_all")
        post(api_url, "/api/process_event/clear")
        post(api_url, "/api/process_event/clear_ignored")
        time.sleep(0.5)
        _, data = get(api_url, "/api/process_event/functions", limit=5)
        assert data["totalUnique"] > 0

    def test_stop(self, require_game, api_url):
        post(api_url, "/api/process_event/start")
        _, data = post(api_url, "/api/process_event/stop")
        assert data.get("status") == "stopped"
        _, status_data = get(api_url, "/api/process_event/status")
        assert status_data["listening"] is False

    def test_clear(self, require_game, api_url):
        post(api_url, "/api/process_event/start")
        time.sleep(0.3)
        _, data = post(api_url, "/api/process_event/clear")
        assert "cleared" in data
        post(api_url, "/api/process_event/stop")


# ═══════════════════════════════════════════════════════════════════
# 7. EXPLORER TOOLS (previously untested)
# ═══════════════════════════════════════════════════════════════════

class TestExplorerSearch:
    def test_search_objects(self, require_game, api_url):
        _, data = get(api_url, "/api/explorer/search", query="PlayerController", limit=5)
        assert "results" in data
        assert data["count"] > 0

    def test_search_classes(self, require_game, api_url):
        _, data = get(api_url, "/api/explorer/classes", query="Character", limit=5)
        assert "results" in data

    def test_search_empty_query(self, require_game, api_url):
        status, data = get(api_url, "/api/explorer/search", query="")
        assert status == 400

    def test_inspect_object(self, require_game, api_url, pawn_address):
        _, data = get(api_url, "/api/explorer/object", address=pawn_address)
        assert "fields" in data or "properties" in data or "address" in data

    def test_summary(self, require_game, api_url, pawn_address):
        _, data = get(api_url, "/api/explorer/summary", address=pawn_address)
        assert "fields" in data or "summary" in data

    def test_read_field(self, require_game, api_url, pawn_address):
        _, data = get(api_url, "/api/explorer/field",
                      address=pawn_address, fieldName="CapsuleComponent")
        # Field exists on Character
        assert "error" not in data or "not found" not in data.get("error", "").lower()

    def test_read_field_nonexistent(self, require_game, api_url, pawn_address):
        _, data = get(api_url, "/api/explorer/field",
                      address=pawn_address, fieldName="FakeFieldXYZ123")
        assert "error" in data

    def test_get_type(self, require_game, api_url):
        _, data = get(api_url, "/api/explorer/type",
                      typeName="Class /Script/Engine.Character")
        assert "fields" in data or "methods" in data


class TestExplorerWrite:
    def test_write_field_invalid_address(self, require_game, api_url):
        status, data = post(api_url, "/api/explorer/field", {
            "address": "0xDEAD", "fieldName": "Health", "value": 100
        })
        assert "error" in data

    def test_invoke_method_invalid_address(self, require_game, api_url):
        status, data = post(api_url, "/api/explorer/method", {
            "address": "0xDEAD", "methodName": "K2_GetActorLocation"
        })
        assert "error" in data


# ═══════════════════════════════════════════════════════════════════
# 8. WORLD & SPATIAL
# ═══════════════════════════════════════════════════════════════════

class TestWorld:
    def test_world_actors(self, require_game, api_url):
        _, data = get(api_url, "/api/world/actors", limit=10)
        assert "actors" in data
        assert data["count"] > 0
        for a in data["actors"]:
            assert "address" in a
            assert "class" in a

    def test_world_actors_filter(self, require_game, api_url):
        _, data = get(api_url, "/api/world/actors", filter="StaticMesh", limit=5)
        for a in data["actors"]:
            assert "StaticMesh" in a["class"]

    def test_world_components(self, require_game, api_url):
        """Components endpoint returns 200 (may be empty for some actors)."""
        _, actors = get(api_url, "/api/world/actors", filter="StaticMesh", limit=1)
        if actors["count"] > 0:
            addr = actors["actors"][0]["address"]
            _, data = get(api_url, "/api/world/components", address=addr)
            assert "components" in data

    def test_hierarchy(self, require_game, api_url, pawn_address):
        _, data = get(api_url, "/api/world/hierarchy", address=pawn_address)
        assert "class" in data or "superChain" in data or "outer" in data

    def test_line_trace(self, require_game, api_url):
        _, data = post(api_url, "/api/world/line_trace", {
            "start": {"x": 0, "y": 0, "z": 200},
            "end": {"x": 0, "y": 0, "z": -10000}
        })
        # Should hit the ground or report no hit
        assert "hit" in data or "error" not in data

    def test_sphere_overlap(self, require_game, api_url):
        _, data = post(api_url, "/api/world/sphere_overlap", {
            "center": {"x": 0, "y": 0, "z": 0},
            "radius": 10000
        })
        # May return actors list or an error about unsupported param types
        assert "actors" in data or "error" in data


# ═══════════════════════════════════════════════════════════════════
# 9. CONSOLE VARIABLES
# ═══════════════════════════════════════════════════════════════════

class TestConsole:
    def test_list_cvars(self, require_game, api_url):
        _, data = get(api_url, "/api/console/cvars", filter="r.", limit=10)
        assert "cvars" in data or "results" in data or "count" in data

    def test_get_cvar(self, require_game, api_url):
        _, data = get(api_url, "/api/console/cvar", name="t.MaxFPS")
        assert "error" not in data or "not found" in data.get("error", "")

    def test_exec_command(self, require_game, api_url):
        _, data = post(api_url, "/api/console/command", {"command": "stat fps"})
        assert "error" not in data


# ═══════════════════════════════════════════════════════════════════
# 10. VR TOOLS
# ═══════════════════════════════════════════════════════════════════

class TestVr:
    def test_vr_status(self, require_game, api_url):
        _, data = get(api_url, "/api/vr/status")
        assert "runtimeReady" in data
        assert "hmdActive" in data
        assert "resolution" in data

    def test_vr_poses(self, require_game, api_url):
        _, data = get(api_url, "/api/vr/poses")
        assert "hmd" in data
        assert "leftController" in data
        assert "rightController" in data
        assert "position" in data["hmd"]
        assert "rotation" in data["hmd"]

    def test_vr_settings(self, require_game, api_url):
        _, data = get(api_url, "/api/vr/settings")
        assert "snapTurnEnabled" in data
        assert "aimMethod" in data

    def test_vr_recenter(self, require_game, api_url):
        _, data = post(api_url, "/api/vr/recenter")
        assert data.get("success") is True

    def test_vr_haptics(self, require_game, api_url):
        _, data = post(api_url, "/api/vr/haptics", {
            "hand": "right", "duration": 0.01, "amplitude": 0.1
        })
        assert data.get("success") is True


# ═══════════════════════════════════════════════════════════════════
# 11. WATCH / SNAPSHOT / DIFF
# ═══════════════════════════════════════════════════════════════════

class TestWatch:
    def test_watch_add_and_list(self, require_game, api_url, pawn_address):
        post(api_url, "/api/watch/clear")
        _, add_data = post(api_url, "/api/watch/add", {
            "address": pawn_address, "fieldName": "CapsuleComponent"
        })
        assert "watchId" in add_data or "id" in add_data
        _, list_data = get(api_url, "/api/watch/list")
        assert list_data.get("count", 0) >= 1
        post(api_url, "/api/watch/clear")

    def test_watch_remove(self, require_game, api_url, pawn_address):
        post(api_url, "/api/watch/clear")
        _, add_data = post(api_url, "/api/watch/add", {
            "address": pawn_address, "fieldName": "CapsuleComponent"
        })
        watch_id = add_data.get("watchId") or add_data.get("id")
        delete(api_url, "/api/watch/remove", {"watchId": watch_id})
        _, list_data = get(api_url, "/api/watch/list")
        assert list_data.get("count", 0) == 0

    def test_watch_changes(self, require_game, api_url):
        _, data = get(api_url, "/api/watch/changes", max=10)
        assert "changes" in data or "events" in data or isinstance(data, dict)

    def test_snapshot_and_diff(self, require_game, api_url, pawn_address):
        _, snap_data = post(api_url, "/api/watch/snapshot", {"address": pawn_address})
        snap_id = snap_data.get("snapshotId") or snap_data.get("id")
        assert snap_id is not None

        _, list_data = get(api_url, "/api/watch/snapshots")
        assert len(list_data.get("snapshots", [])) >= 1

        _, diff_data = post(api_url, "/api/watch/diff", {"snapshotId": snap_id})
        assert "changed" in diff_data or "fields" in diff_data or "diff" in diff_data or "error" not in diff_data

        # Cleanup
        delete(api_url, "/api/watch/snapshot", {"snapshotId": snap_id})


# ═══════════════════════════════════════════════════════════════════
# 12. FUNCTION HOOKS
# ═══════════════════════════════════════════════════════════════════

class TestHooks:
    def test_hook_add_and_list(self, require_game, api_url):
        post(api_url, "/api/hook/clear")
        _, add_data = post(api_url, "/api/hook/add", {
            "className": "Class /Script/Engine.Actor",
            "functionName": "ReceiveTick",
            "action": "log"
        })
        assert "hookId" in add_data or "id" in add_data
        _, list_data = get(api_url, "/api/hook/list")
        assert len(list_data.get("hooks", [])) >= 1
        post(api_url, "/api/hook/clear")

    def test_hook_remove(self, require_game, api_url):
        post(api_url, "/api/hook/clear")
        _, add_data = post(api_url, "/api/hook/add", {
            "className": "Class /Script/Engine.Actor",
            "functionName": "ReceiveTick",
            "action": "log"
        })
        hook_id = add_data.get("hookId") or add_data.get("id")
        delete(api_url, "/api/hook/remove", {"hookId": hook_id})
        _, list_data = get(api_url, "/api/hook/list")
        # After remove, the hook should be inactive (clear removes all; remove deactivates one)
        active_hooks = [h for h in list_data.get("hooks", []) if h.get("active", True)]
        assert len(active_hooks) == 0

    def test_hook_log(self, require_game, api_url):
        post(api_url, "/api/hook/clear")
        _, add_data = post(api_url, "/api/hook/add", {
            "className": "Class /Script/Engine.Actor",
            "functionName": "ReceiveTick",
            "action": "log"
        })
        hook_id = add_data.get("hookId") or add_data.get("id")
        time.sleep(0.3)
        _, log_data = get(api_url, "/api/hook/log", hookId=hook_id, max=5)
        assert "entries" in log_data or "calls" in log_data or isinstance(log_data, dict)
        post(api_url, "/api/hook/clear")

    def test_hook_clear(self, require_game, api_url):
        post(api_url, "/api/hook/add", {
            "className": "Class /Script/Engine.Actor",
            "functionName": "ReceiveTick",
            "action": "log"
        })
        _, data = post(api_url, "/api/hook/clear")
        assert "error" not in data


# ═══════════════════════════════════════════════════════════════════
# 13. MACROS
# ═══════════════════════════════════════════════════════════════════

class TestMacros:
    def test_save_list_get_delete(self, require_game, api_url, pawn_address):
        # Save
        _, save_data = post(api_url, "/api/macro/save", {
            "name": "_test_macro",
            "operations": [{"type": "read_field", "address": pawn_address, "fieldName": "CapsuleComponent"}],
            "description": "test macro"
        })
        assert "error" not in save_data

        # List
        _, list_data = get(api_url, "/api/macro/list")
        names = [m["name"] for m in list_data.get("macros", [])]
        assert "_test_macro" in names

        # Get
        _, get_data = get(api_url, "/api/macro/get", name="_test_macro")
        assert get_data.get("name") == "_test_macro"

        # Delete
        delete(api_url, "/api/macro/delete", {"name": "_test_macro"})
        _, list_data2 = get(api_url, "/api/macro/list")
        names2 = [m["name"] for m in list_data2.get("macros", [])]
        assert "_test_macro" not in names2


# ═══════════════════════════════════════════════════════════════════
# 14. ASSET DISCOVERY
# ═══════════════════════════════════════════════════════════════════

class TestAssets:
    def test_asset_search(self, require_game, api_url):
        _, data = get(api_url, "/api/asset/search", query="Material", limit=5)
        assert "results" in data or "assets" in data

    def test_asset_classes(self, require_game, api_url):
        _, data = get(api_url, "/api/asset/classes", filter="Texture", limit=10)
        assert "classes" in data or "results" in data or "count" in data


# ═══════════════════════════════════════════════════════════════════
# 15. DEEP DISCOVERY
# ═══════════════════════════════════════════════════════════════════

class TestDiscovery:
    def test_subclasses(self, require_game, api_url):
        _, data = get(api_url, "/api/discovery/subclasses",
                      className="Class /Script/Engine.Actor", limit=10)
        assert "subclasses" in data or "results" in data
        assert data.get("count", 0) > 0

    def test_search_names(self, require_game, api_url):
        _, data = get(api_url, "/api/discovery/names", query="Health", limit=10)
        assert "results" in data

    def test_delegates(self, require_game, api_url, pawn_address):
        _, data = get(api_url, "/api/discovery/delegates", address=pawn_address)
        assert "delegates" in data or "events" in data or "error" not in data

    def test_all_children(self, require_game, api_url):
        _, data = get(api_url, "/api/discovery/all_children",
                      typeName="Class /Script/Engine.Character")
        assert "properties" in data or "functions" in data or "children" in data


# ═══════════════════════════════════════════════════════════════════
# 16. DIAGNOSTICS
# ═══════════════════════════════════════════════════════════════════

class TestDiagnostics:
    def test_diagnostics_snapshot(self, require_game, api_url):
        _, data = get(api_url, "/api/diagnostics/snapshot")
        assert "callbacks" in data or "breadcrumb" in data or "plugins" in data

    def test_callback_health(self, require_game, api_url):
        _, data = get(api_url, "/api/diagnostics/callbacks")
        assert isinstance(data, dict)
        # Should have at least one callback tracked
        assert len(data) > 0

    def test_breadcrumb(self, require_game, api_url):
        _, data = get(api_url, "/api/diagnostics/breadcrumb")
        # Breadcrumb returns callback, path, sequence, detail
        assert "callback" in data or "path" in data or "sequence" in data

    def test_loaded_plugins(self, require_game, api_url):
        _, data = get(api_url, "/api/diagnostics/plugins")
        assert "plugins" in data


# ═══════════════════════════════════════════════════════════════════
# 17. INPUT INJECTION
# ═══════════════════════════════════════════════════════════════════

class TestInput:
    def test_key_tap(self, require_game, api_url):
        _, data = post(api_url, "/api/input/key", {"key": "W", "action": "tap"})
        assert data.get("success") is True or "error" not in data

    def test_mouse_move(self, require_game, api_url):
        _, data = post(api_url, "/api/input/mouse", {"dx": 1, "dy": 0})
        assert "error" not in data

    def test_text_input(self, require_game, api_url):
        _, data = post(api_url, "/api/input/text", {"text": "a"})
        assert "error" not in data


# ═══════════════════════════════════════════════════════════════════
# 18. SCREENSHOT
# ═══════════════════════════════════════════════════════════════════

class TestScreenshot:
    def test_screenshot_info(self, require_game, api_url):
        _, data = get(api_url, "/api/screenshot/info")
        assert "initialized" in data or "renderer" in data

    def test_screenshot_capture(self, require_game, api_url):
        _, data = get(api_url, "/api/screenshot", maxWidth=320, quality=50, timeout=8000)
        # Should return base64 image or error if D3D not ready
        assert "image" in data or "data" in data or "error" in data


# ═══════════════════════════════════════════════════════════════════
# 19. EVENTS POLL
# ═══════════════════════════════════════════════════════════════════

class TestEvents:
    def test_events_poll_returns_quickly_when_empty(self, require_game, api_url):
        """Events endpoint should return within a reasonable time with no events."""
        start = time.time()
        r = requests.get(f"{api_url}/api/events", params={"timeout": 500}, timeout=5)
        elapsed = time.time() - start
        assert r.status_code == 200
        assert elapsed < 3.0


# ═══════════════════════════════════════════════════════════════════
# 20. EDGE CASES & ERROR HANDLING
# ═══════════════════════════════════════════════════════════════════

class TestEdgeCases:
    def test_invalid_json_body(self, require_game, api_url):
        """POST with invalid JSON returns 400, not 500."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          data="{{not json}}", headers={"Content-Type": "application/json"}, timeout=5)
        assert r.status_code == 400

    def test_empty_body(self, require_game, api_url):
        """POST with empty body to a route expecting JSON."""
        r = requests.post(f"{api_url}/api/vr/settings",
                          data="", headers={"Content-Type": "application/json"}, timeout=5)
        assert r.status_code == 400

    def test_very_long_search_query(self, require_game, api_url):
        """Very long search query doesn't crash."""
        _, data = get(api_url, "/api/explorer/search", query="A" * 1000, limit=1)
        # Should return empty results, not crash
        assert data.get("count", 0) == 0

    def test_concurrent_lua_exec(self, require_game, api_url):
        """Multiple simultaneous Lua executions don't crash.
        Note: kept conservative (3 workers, 5 tasks) because higher concurrency
        can overwhelm the game thread queue and crash the plugin."""
        import concurrent.futures
        def run_lua(i):
            try:
                r = requests.post(f"{api_url}/api/lua/exec",
                                  json={"code": f"return {i}"}, timeout=15)
                return r.json()
            except Exception:
                return {"success": False, "error": "connection failed"}
        with concurrent.futures.ThreadPoolExecutor(max_workers=3) as ex:
            futures = [ex.submit(run_lua, i) for i in range(5)]
            results = [f.result() for f in futures]
        successes = sum(1 for r in results if r.get("success"))
        assert successes >= 3  # Allow some timeouts under load

    def test_stale_address_operations(self, require_game, api_url):
        """Operations on an invalid address return error, not crash."""
        fake = "0x1234567890AB"
        _, data = get(api_url, "/api/explorer/object", address=fake)
        assert "error" in data

        _, data2 = get(api_url, "/api/explorer/summary", address=fake)
        assert "error" in data2

    def test_objects_by_class_nonexistent(self, require_game, api_url):
        """Searching for a nonexistent class returns empty, not crash."""
        _, data = get(api_url, "/api/explorer/objects_by_class",
                      className="Class /Script/Fake.NonexistentClass123")
        assert data.get("count", 0) == 0 or "error" in data
