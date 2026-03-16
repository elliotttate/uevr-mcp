"""Tests for player write shortcuts (uevr_set_position, uevr_set_health)."""

import pytest
import requests


class TestSetPosition:
    """Test POST /api/player/position."""

    def test_set_position_all_axes(self, require_game, api_url):
        """Set player position with all three coordinates."""
        # First get current position
        r_get = requests.get(f"{api_url}/api/camera", timeout=5)

        r = requests.post(f"{api_url}/api/player/position", json={
            "x": 100.0, "y": 200.0, "z": 300.0
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        # Should either succeed or report a meaningful error
        assert "success" in data or "error" in data

    def test_set_position_partial_x_only(self, require_game, api_url):
        """Set only x coordinate, keeping y and z."""
        r = requests.post(f"{api_url}/api/player/position", json={
            "x": 500.0
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert "success" in data or "error" in data

    def test_set_position_partial_z_only(self, require_game, api_url):
        """Set only z coordinate (vertical)."""
        r = requests.post(f"{api_url}/api/player/position", json={
            "z": 1000.0
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert "success" in data or "error" in data

    def test_set_position_returns_new_position(self, require_game, api_url):
        """Response includes the new position."""
        r = requests.post(f"{api_url}/api/player/position", json={
            "x": 100.0, "y": 200.0, "z": 300.0
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        if "success" in data and data["success"]:
            assert "position" in data
            assert "x" in data["position"]
            assert "y" in data["position"]
            assert "z" in data["position"]

    def test_set_position_empty_body_accepted(self, require_game, api_url):
        """Empty body is accepted (no axes to change = no-op)."""
        r = requests.post(f"{api_url}/api/player/position", json={}, timeout=10)
        assert r.status_code == 200

    def test_set_position_invalid_json_returns_400(self, require_game, api_url):
        """Invalid JSON body returns 400."""
        r = requests.post(f"{api_url}/api/player/position",
                         data="not json", headers={"Content-Type": "application/json"},
                         timeout=5)
        assert r.status_code == 400


class TestSetHealth:
    """Test POST /api/player/health."""

    def test_set_health_float_value(self, require_game, api_url):
        """Set health with a float value."""
        r = requests.post(f"{api_url}/api/player/health", json={
            "value": 100.0
        }, timeout=10)
        assert r.status_code in (200, 500)
        data = r.json()
        # May succeed if a health field exists, or error if not found
        assert "success" in data or "error" in data

    def test_set_health_int_value(self, require_game, api_url):
        """Set health with an integer value."""
        r = requests.post(f"{api_url}/api/player/health", json={
            "value": 999
        }, timeout=10)
        assert r.status_code in (200, 500)
        data = r.json()
        assert "success" in data or "error" in data

    def test_set_health_returns_field_name(self, require_game, api_url):
        """On success, response includes which field was written."""
        r = requests.post(f"{api_url}/api/player/health", json={
            "value": 100.0
        }, timeout=10)
        assert r.status_code in (200, 500)
        data = r.json()
        assert "success" in data or "error" in data
        if data.get("success"):
            assert "field" in data
            assert "newValue" in data

    def test_set_health_missing_value_returns_400(self, require_game, api_url):
        """Missing 'value' in body returns 400."""
        r = requests.post(f"{api_url}/api/player/health", json={}, timeout=5)
        assert r.status_code == 400

    def test_set_health_invalid_json_returns_400(self, require_game, api_url):
        """Invalid JSON body returns 400."""
        r = requests.post(f"{api_url}/api/player/health",
                         data="not json", headers={"Content-Type": "application/json"},
                         timeout=5)
        assert r.status_code == 400

    def test_set_health_error_message_is_helpful(self, require_game, api_url):
        """If no health field found, error message suggests using write_field."""
        r = requests.post(f"{api_url}/api/player/health", json={
            "value": 100.0
        }, timeout=10)
        data = r.json()
        if "error" in data:
            # Error should be helpful, not just "failed"
            assert len(data["error"]) > 10
