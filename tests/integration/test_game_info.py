"""Tests for game info (uevr_get_game_info / GET /api/game_info + pipe)."""

import pytest
import requests


class TestGetGameInfoHttp:
    """Test GET /api/game_info via HTTP."""

    def test_returns_game_path(self, require_game, api_url):
        """Game info includes full game executable path."""
        r = requests.get(f"{api_url}/api/game_info", timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert "gamePath" in data
        # Should be a file path ending in .exe
        assert data["gamePath"].lower().endswith(".exe")

    def test_returns_game_directory(self, require_game, api_url):
        """Game info includes game directory."""
        r = requests.get(f"{api_url}/api/game_info", timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert "gameDirectory" in data
        assert len(data["gameDirectory"]) > 0

    def test_returns_game_name(self, require_game, api_url):
        """Game info includes game executable name."""
        r = requests.get(f"{api_url}/api/game_info", timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert "gameName" in data
        assert data["gameName"].lower().endswith(".exe")

    def test_returns_vr_runtime(self, require_game, api_url):
        """Game info includes VR runtime type."""
        r = requests.get(f"{api_url}/api/game_info", timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert "vrRuntime" in data
        assert data["vrRuntime"] in ("OpenVR", "OpenXR", "Unknown")

    def test_returns_hmd_active(self, require_game, api_url):
        """Game info includes HMD active status."""
        r = requests.get(f"{api_url}/api/game_info", timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert "hmdActive" in data
        assert isinstance(data["hmdActive"], bool)

    def test_returns_uptime(self, require_game, api_url):
        """Game info includes uptime in seconds."""
        r = requests.get(f"{api_url}/api/game_info", timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert "uptimeSeconds" in data
        assert isinstance(data["uptimeSeconds"], (int, float))
        assert data["uptimeSeconds"] >= 0

    def test_returns_http_port(self, require_game, api_url):
        """Game info includes the HTTP port."""
        r = requests.get(f"{api_url}/api/game_info", timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert "httpPort" in data
        assert data["httpPort"] == 8899

    def test_game_path_matches_game_name(self, require_game, api_url):
        """gamePath should end with gameName."""
        r = requests.get(f"{api_url}/api/game_info", timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert data["gamePath"].endswith(data["gameName"])
