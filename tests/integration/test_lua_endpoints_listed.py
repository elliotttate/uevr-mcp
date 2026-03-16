"""Tests that all Lua endpoints are listed in the /api index."""

import pytest
import requests


EXPECTED_LUA_ENDPOINTS = [
    "/api/lua/exec",
    "/api/lua/reset",
    "/api/lua/state",
    "/api/lua/scripts/write",
    "/api/lua/scripts/list",
    "/api/lua/scripts/read",
    "/api/lua/scripts/delete",
]


class TestLuaEndpointsListed:
    """Test that Lua endpoints appear in /api index."""

    @pytest.mark.parametrize("endpoint", EXPECTED_LUA_ENDPOINTS)
    def test_lua_endpoint_listed(self, require_game, api_url, endpoint):
        """Each Lua endpoint is listed in the /api index."""
        r = requests.get(f"{api_url}/api", timeout=5)
        data = r.json()
        assert endpoint in data["endpoints"], f"'{endpoint}' not in endpoint index"
