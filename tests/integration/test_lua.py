"""Tests for Lua execution endpoints (POST /api/lua/exec, /api/lua/reset, GET /api/lua/state)."""

import pytest
import requests
import time


class TestLuaState:
    """Test GET /api/lua/state — Lua engine diagnostics."""

    def test_state_returns_200(self, require_game, api_url):
        """Lua state endpoint returns 200."""
        r = requests.get(f"{api_url}/api/lua/state", timeout=5)
        assert r.status_code == 200

    def test_state_shows_initialized(self, require_game, api_url):
        """Lua engine reports initialized=true after plugin load."""
        r = requests.get(f"{api_url}/api/lua/state", timeout=5)
        data = r.json()
        assert "initialized" in data
        assert data["initialized"] is True

    def test_state_has_exec_count(self, require_game, api_url):
        """State includes execCount field."""
        r = requests.get(f"{api_url}/api/lua/state", timeout=5)
        data = r.json()
        assert "execCount" in data
        assert isinstance(data["execCount"], int)

    def test_state_has_frame_callback_count(self, require_game, api_url):
        """State includes frameCallbackCount field."""
        r = requests.get(f"{api_url}/api/lua/state", timeout=5)
        data = r.json()
        assert "frameCallbackCount" in data
        assert isinstance(data["frameCallbackCount"], int)

    def test_state_has_memory_kb(self, require_game, api_url):
        """State includes memoryKB field."""
        r = requests.get(f"{api_url}/api/lua/state", timeout=5)
        data = r.json()
        assert "memoryKB" in data
        assert isinstance(data["memoryKB"], int)
        assert data["memoryKB"] > 0


class TestLuaExec:
    """Test POST /api/lua/exec — Execute Lua code."""

    def test_exec_missing_code_returns_400(self, require_game, api_url):
        """Missing 'code' parameter returns 400."""
        r = requests.post(f"{api_url}/api/lua/exec", json={}, timeout=5)
        assert r.status_code == 400

    def test_exec_invalid_json_returns_400(self, require_game, api_url):
        """Invalid JSON body returns 400."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          data="not json", headers={"Content-Type": "application/json"}, timeout=5)
        assert r.status_code == 400

    def test_exec_simple_return(self, require_game, api_url):
        """Execute 'return 42' returns {success:true, result:42}."""
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": "return 42"}, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True
        assert data["result"] == 42

    def test_exec_return_string(self, require_game, api_url):
        """Execute returning a string."""
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": 'return "hello"'}, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True
        assert data["result"] == "hello"

    def test_exec_return_boolean(self, require_game, api_url):
        """Execute returning a boolean."""
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": "return true"}, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True

    def test_exec_return_nil(self, require_game, api_url):
        """Execute returning nil."""
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": "return nil"}, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True
        assert data["result"] is None

    def test_exec_return_table_as_array(self, require_game, api_url):
        """Execute returning a Lua table with sequential integer keys → JSON array."""
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": "return {10, 20, 30}"}, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True
        assert data["result"] == [10, 20, 30]

    def test_exec_return_table_as_object(self, require_game, api_url):
        """Execute returning a Lua table with string keys → JSON object."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": 'return {name="test", value=123}'}, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True
        assert data["result"]["name"] == "test"
        assert data["result"]["value"] == 123

    def test_exec_return_float(self, require_game, api_url):
        """Execute returning a floating point number."""
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": "return 3.14"}, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True
        assert abs(data["result"] - 3.14) < 0.001

    def test_exec_arithmetic(self, require_game, api_url):
        """Execute arithmetic expression."""
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": "return 2 + 3 * 4"}, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True
        assert data["result"] == 14

    def test_exec_string_operations(self, require_game, api_url):
        """Execute string operations."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": 'return string.upper("hello")'}, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True
        assert data["result"] == "HELLO"

    def test_exec_has_exec_time(self, require_game, api_url):
        """Response includes execTime_ms."""
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": "return 1"}, timeout=10)
        data = r.json()
        assert "execTime_ms" in data
        assert isinstance(data["execTime_ms"], (int, float))
        assert data["execTime_ms"] >= 0

    def test_exec_has_output_array(self, require_game, api_url):
        """Response includes output array even when empty."""
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": "return 1"}, timeout=10)
        data = r.json()
        assert "output" in data
        assert isinstance(data["output"], list)


class TestLuaExecPrint:
    """Test print() capture in Lua execution."""

    def test_print_captures_output(self, require_game, api_url):
        """print() calls are captured in the output array."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": 'print("hello world")'}, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True
        assert "hello world" in data["output"]

    def test_print_multiple_calls(self, require_game, api_url):
        """Multiple print() calls appear as separate entries."""
        code = 'print("first"); print("second"); print("third")'
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": code}, timeout=10)
        data = r.json()
        assert len(data["output"]) == 3
        assert data["output"][0] == "first"
        assert data["output"][1] == "second"
        assert data["output"][2] == "third"

    def test_print_with_multiple_args(self, require_game, api_url):
        """print() with multiple arguments joins them with tabs."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": 'print("a", "b", "c")'}, timeout=10)
        data = r.json()
        assert len(data["output"]) == 1
        assert "a" in data["output"][0]
        assert "b" in data["output"][0]
        assert "c" in data["output"][0]

    def test_print_and_return(self, require_game, api_url):
        """print() output and return value coexist."""
        code = 'print("log line"); return 99'
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": code}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] == 99
        assert "log line" in data["output"]


class TestLuaExecErrors:
    """Test Lua execution error handling."""

    def test_syntax_error(self, require_game, api_url):
        """Syntax error returns success=false with error message."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "this is not valid lua +++"}, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is False
        assert "error" in data
        assert len(data["error"]) > 0

    def test_runtime_error(self, require_game, api_url):
        """Runtime error returns success=false."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "error('test error message')"}, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is False
        assert "test error message" in data["error"]

    def test_nil_index_error(self, require_game, api_url):
        """Indexing nil returns error."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "local x = nil; return x.foo"}, timeout=10)
        data = r.json()
        assert data["success"] is False

    def test_infinite_loop_protection(self, require_game, api_url):
        """Infinite loop is caught by instruction count limit."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "while true do end", "timeout": 15000}, timeout=20)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is False
        assert "instruction limit" in data["error"].lower() or "limit" in data["error"].lower()

    def test_error_still_has_output(self, require_game, api_url):
        """Output captured before an error is still returned."""
        code = 'print("before error"); error("boom")'
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": code}, timeout=10)
        data = r.json()
        assert data["success"] is False
        assert "before error" in data["output"]


class TestLuaExecPersistence:
    """Test that Lua state persists between calls."""

    def test_globals_persist(self, require_game, api_url):
        """Global variables persist between exec calls."""
        # Set a global
        r1 = requests.post(f"{api_url}/api/lua/exec",
                           json={"code": "test_persist_var = 42"}, timeout=10)
        assert r1.json()["success"] is True

        # Read it back
        r2 = requests.post(f"{api_url}/api/lua/exec",
                           json={"code": "return test_persist_var"}, timeout=10)
        data = r2.json()
        assert data["success"] is True
        assert data["result"] == 42

    def test_functions_persist(self, require_game, api_url):
        """Functions defined in one call are available in the next."""
        # Define a function
        r1 = requests.post(f"{api_url}/api/lua/exec",
                           json={"code": "function test_add(a, b) return a + b end"}, timeout=10)
        assert r1.json()["success"] is True

        # Call it
        r2 = requests.post(f"{api_url}/api/lua/exec",
                           json={"code": "return test_add(10, 20)"}, timeout=10)
        data = r2.json()
        assert data["success"] is True
        assert data["result"] == 30

    def test_exec_count_increments(self, require_game, api_url):
        """execCount in state info increments with each call."""
        r1 = requests.get(f"{api_url}/api/lua/state", timeout=5)
        count_before = r1.json()["execCount"]

        requests.post(f"{api_url}/api/lua/exec", json={"code": "return 1"}, timeout=10)

        r2 = requests.get(f"{api_url}/api/lua/state", timeout=5)
        count_after = r2.json()["execCount"]
        assert count_after > count_before


class TestLuaExecSandbox:
    """Test Lua sandbox restrictions."""

    def test_os_execute_disabled(self, require_game, api_url):
        """os.execute is nil (disabled)."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return os.execute == nil"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True

    def test_os_exit_disabled(self, require_game, api_url):
        """os.exit is nil (disabled)."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return os.exit == nil"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True

    def test_io_disabled(self, require_game, api_url):
        """io library is nil (disabled)."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return io == nil"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True

    def test_dofile_disabled(self, require_game, api_url):
        """dofile is nil (disabled)."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return dofile == nil"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True

    def test_safe_os_functions_work(self, require_game, api_url):
        """Safe os functions like os.clock still work."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return type(os.clock()) == 'number'"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True

    def test_standard_libs_available(self, require_game, api_url):
        """Standard safe libraries are available."""
        code = """
        return {
            has_string = type(string) == "table",
            has_math = type(math) == "table",
            has_table = type(table) == "table",
            has_coroutine = type(coroutine) == "table"
        }
        """
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": code}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"]["has_string"] is True
        assert data["result"]["has_math"] is True
        assert data["result"]["has_table"] is True
        assert data["result"]["has_coroutine"] is True


class TestLuaExecUevrApi:
    """Test UEVR API bindings in Lua."""

    def test_uevr_api_table_exists(self, require_game, api_url):
        """uevr.api table is available."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return type(uevr.api) == 'table'"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True

    def test_uevr_vr_table_exists(self, require_game, api_url):
        """uevr.vr table is available."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return type(uevr.vr) == 'table'"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True

    def test_uevr_uobject_hook_exists(self, require_game, api_url):
        """uevr.uobject_hook table is available."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return type(uevr.uobject_hook) == 'table'"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True

    def test_uevr_console_exists(self, require_game, api_url):
        """uevr.console table is available."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return type(uevr.console) == 'table'"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True

    def test_mcp_table_exists(self, require_game, api_url):
        """mcp table (callback management) is available."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return type(mcp) == 'table'"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True

    def test_get_player_controller(self, require_game, api_url):
        """uevr.api.get_player_controller() returns a UObject."""
        code = """
        local pc = uevr.api.get_player_controller()
        if pc == nil then return "nil" end
        return pc:get_full_name()
        """
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": code}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is not None
        if data["result"] != "nil":
            assert "Controller" in data["result"] or len(data["result"]) > 0

    def test_get_local_pawn(self, require_game, api_url):
        """uevr.api.get_local_pawn() returns a UObject."""
        code = """
        local pawn = uevr.api.get_local_pawn()
        if pawn == nil then return "nil" end
        return pawn:get_full_name()
        """
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": code}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is not None

    def test_get_engine(self, require_game, api_url):
        """uevr.api.get_engine() returns a UObject."""
        code = """
        local eng = uevr.api.get_engine()
        if eng == nil then return "nil" end
        return eng:get_full_name()
        """
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": code}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] != "nil"

    def test_find_uobject(self, require_game, api_url):
        """uevr.api.find_uobject works for known classes."""
        code = """
        local cls = uevr.api.find_uobject("Class /Script/CoreUObject.Object")
        if cls == nil then return "nil" end
        return cls:get_fname()
        """
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": code}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] == "Object"

    def test_uobject_get_class(self, require_game, api_url):
        """UObject:get_class() returns a UClass."""
        code = """
        local pawn = uevr.api.get_local_pawn()
        if pawn == nil then return "no pawn" end
        local cls = pawn:get_class()
        if cls == nil then return "no class" end
        return cls:get_fname()
        """
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": code}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert isinstance(data["result"], str)
        assert len(data["result"]) > 0

    def test_uobject_get_address(self, require_game, api_url):
        """UObject:get_address() returns a non-zero integer."""
        code = """
        local eng = uevr.api.get_engine()
        if eng == nil then return 0 end
        return eng:get_address()
        """
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": code}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert isinstance(data["result"], int)
        assert data["result"] > 0

    def test_vr_is_runtime_ready(self, require_game, api_url):
        """uevr.vr.is_runtime_ready() returns a boolean."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return uevr.vr.is_runtime_ready()"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert isinstance(data["result"], bool)

    def test_vr_get_aim_method(self, require_game, api_url):
        """uevr.vr.get_aim_method() returns an integer."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return uevr.vr.get_aim_method()"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert isinstance(data["result"], int)


class TestLuaFrameCallbacks:
    """Test mcp.on_frame() callback registration."""

    def test_register_frame_callback(self, require_game, api_url):
        """mcp.on_frame() returns a callback ID."""
        code = "return mcp.on_frame(function(dt) end)"
        r = requests.post(f"{api_url}/api/lua/exec", json={"code": code}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert isinstance(data["result"], int)
        assert data["result"] > 0

        # Clean up
        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "mcp.clear_callbacks()"}, timeout=10)

    def test_callback_count_updates(self, require_game, api_url):
        """frameCallbackCount increments when callbacks are added."""
        # Clear first
        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "mcp.clear_callbacks()"}, timeout=10)

        r1 = requests.get(f"{api_url}/api/lua/state", timeout=5)
        count_before = r1.json()["frameCallbackCount"]

        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "mcp.on_frame(function(dt) end)"}, timeout=10)

        r2 = requests.get(f"{api_url}/api/lua/state", timeout=5)
        count_after = r2.json()["frameCallbackCount"]
        assert count_after == count_before + 1

        # Clean up
        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "mcp.clear_callbacks()"}, timeout=10)

    def test_clear_callbacks(self, require_game, api_url):
        """mcp.clear_callbacks() removes all frame callbacks."""
        # Add a callback
        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "mcp.on_frame(function(dt) end)"}, timeout=10)

        # Clear
        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "mcp.clear_callbacks()"}, timeout=10)

        r = requests.get(f"{api_url}/api/lua/state", timeout=5)
        assert r.json()["frameCallbackCount"] == 0

    def test_remove_single_callback(self, require_game, api_url):
        """mcp.remove_callback(id) removes a specific callback."""
        # Clear first
        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "mcp.clear_callbacks()"}, timeout=10)

        # Add and get ID
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return mcp.on_frame(function(dt) end)"}, timeout=10)
        cb_id = r.json()["result"]

        # Remove it
        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": f"mcp.remove_callback({cb_id})"}, timeout=10)

        r2 = requests.get(f"{api_url}/api/lua/state", timeout=5)
        assert r2.json()["frameCallbackCount"] == 0

    def test_frame_callback_executes(self, require_game, api_url):
        """A frame callback actually runs (sets a global we can read back)."""
        # Clear first
        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "mcp.clear_callbacks(); _frame_counter = 0"}, timeout=10)

        # Register callback that increments a counter
        requests.post(f"{api_url}/api/lua/exec", json={
            "code": "mcp.on_frame(function(dt) _frame_counter = _frame_counter + 1 end)"
        }, timeout=10)

        # Wait a moment for some frames to tick
        time.sleep(0.5)

        # Read the counter
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return _frame_counter"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert isinstance(data["result"], int)
        assert data["result"] > 0, "Frame callback should have executed at least once"

        # Clean up
        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "mcp.clear_callbacks()"}, timeout=10)

    def test_frame_callback_receives_delta(self, require_game, api_url):
        """Frame callbacks receive a delta time argument > 0."""
        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "mcp.clear_callbacks(); _last_dt = nil"}, timeout=10)

        requests.post(f"{api_url}/api/lua/exec", json={
            "code": "mcp.on_frame(function(dt) _last_dt = dt end)"
        }, timeout=10)

        time.sleep(0.2)

        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return _last_dt"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert isinstance(data["result"], (int, float))
        assert data["result"] > 0

        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "mcp.clear_callbacks()"}, timeout=10)


class TestLuaReset:
    """Test POST /api/lua/reset — Reset Lua state."""

    def test_reset_returns_success(self, require_game, api_url):
        """Reset endpoint returns success."""
        r = requests.post(f"{api_url}/api/lua/reset", timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True

    def test_reset_clears_globals(self, require_game, api_url):
        """Reset clears previously set globals."""
        # Set a global
        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "_reset_test_var = 999"}, timeout=10)

        # Reset
        requests.post(f"{api_url}/api/lua/reset", timeout=10)

        # Global should be gone
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return _reset_test_var"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is None

    def test_reset_clears_callbacks(self, require_game, api_url):
        """Reset clears frame callbacks."""
        requests.post(f"{api_url}/api/lua/exec",
                      json={"code": "mcp.on_frame(function(dt) end)"}, timeout=10)

        requests.post(f"{api_url}/api/lua/reset", timeout=10)

        r = requests.get(f"{api_url}/api/lua/state", timeout=5)
        assert r.json()["frameCallbackCount"] == 0

    def test_reset_resets_exec_count(self, require_game, api_url):
        """Reset brings the execution counter to a low value (resets accumulated count)."""
        # Run a few execs to build up the counter
        for _ in range(3):
            requests.post(f"{api_url}/api/lua/exec", json={"code": "return 1"}, timeout=10)

        r_before = requests.get(f"{api_url}/api/lua/state", timeout=5)
        count_before = r_before.json()["execCount"]
        assert count_before >= 3

        # Reset
        reset_r = requests.post(f"{api_url}/api/lua/reset", timeout=10)
        assert reset_r.json().get("success") is True

        # After reset, count should be much lower than before
        r_after = requests.get(f"{api_url}/api/lua/state", timeout=5)
        count_after = r_after.json()["execCount"]
        assert count_after < count_before, f"execCount should decrease after reset: {count_after} vs {count_before}"

    def test_state_is_usable_after_reset(self, require_game, api_url):
        """Lua state works normally after reset."""
        requests.post(f"{api_url}/api/lua/reset", timeout=10)

        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return 1 + 1"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] == 2

    def test_uevr_api_available_after_reset(self, require_game, api_url):
        """UEVR API bindings are re-registered after reset."""
        requests.post(f"{api_url}/api/lua/reset", timeout=10)

        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return type(uevr.api) == 'table'"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True


class TestLuaMcpLog:
    """Test mcp.log() function."""

    def test_mcp_log_exists(self, require_game, api_url):
        """mcp.log is a function."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": "return type(mcp.log) == 'function'"}, timeout=10)
        data = r.json()
        assert data["success"] is True
        assert data["result"] is True

    def test_mcp_log_doesnt_crash(self, require_game, api_url):
        """Calling mcp.log doesn't error."""
        r = requests.post(f"{api_url}/api/lua/exec",
                          json={"code": 'mcp.log("test log message"); return true'}, timeout=10)
        data = r.json()
        assert data["success"] is True
