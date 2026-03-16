"""Tests that /api endpoint index lists all new endpoints."""

import pytest
import requests


EXPECTED_NEW_ENDPOINTS = [
    "/api/explorer/chain",
    "/api/explorer/singletons",
    "/api/explorer/singleton",
    "/api/explorer/array",
    "/api/explorer/memory",
    "/api/explorer/typed",
    "/api/camera",
    "/api/game_info",
    "/api/player/position",
    "/api/player/health",
]


class TestEndpointIndex:
    """Test GET /api — the plugin info and endpoint index."""

    def test_api_returns_200(self, require_game, api_url):
        """Root /api endpoint returns 200."""
        r = requests.get(f"{api_url}/api", timeout=5)
        assert r.status_code == 200

    def test_api_lists_endpoints(self, require_game, api_url):
        """Root /api endpoint includes an 'endpoints' array."""
        r = requests.get(f"{api_url}/api", timeout=5)
        data = r.json()
        assert "endpoints" in data
        assert isinstance(data["endpoints"], list)

    @pytest.mark.parametrize("endpoint", EXPECTED_NEW_ENDPOINTS)
    def test_new_endpoint_listed(self, require_game, api_url, endpoint):
        """Each new endpoint is listed in the /api index."""
        r = requests.get(f"{api_url}/api", timeout=5)
        data = r.json()
        endpoints = data["endpoints"]
        assert endpoint in endpoints, f"'{endpoint}' not found in endpoint index"

    def test_plugin_name_present(self, require_game, api_url):
        """Plugin name is present in /api response."""
        r = requests.get(f"{api_url}/api", timeout=5)
        data = r.json()
        assert "name" in data
        assert "UEVR" in data["name"]

    def test_version_present(self, require_game, api_url):
        """Version field is present in /api response."""
        r = requests.get(f"{api_url}/api", timeout=5)
        data = r.json()
        assert "version" in data
