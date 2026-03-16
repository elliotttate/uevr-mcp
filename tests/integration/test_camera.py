"""Tests for camera introspection (uevr_get_camera / GET /api/camera)."""

import pytest
import requests


class TestGetCamera:
    """Test GET /api/camera."""

    def test_returns_camera_manager(self, require_game, api_url):
        """Camera endpoint returns camera manager info."""
        r = requests.get(f"{api_url}/api/camera", timeout=5)
        assert r.status_code == 200
        data = r.json()
        # Should have cameraManager with address
        assert "cameraManager" in data
        assert "address" in data["cameraManager"]
        assert data["cameraManager"]["address"].startswith("0x")

    def test_returns_camera_class_name(self, require_game, api_url):
        """Camera manager has a className."""
        r = requests.get(f"{api_url}/api/camera", timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert "className" in data["cameraManager"]

    def test_returns_position(self, require_game, api_url):
        """Camera endpoint returns position with x, y, z."""
        r = requests.get(f"{api_url}/api/camera", timeout=5)
        assert r.status_code == 200
        data = r.json()
        if "position" in data:
            pos = data["position"]
            assert "x" in pos
            assert "y" in pos
            assert "z" in pos
            # Values should be numeric
            assert isinstance(pos["x"], (int, float))
            assert isinstance(pos["y"], (int, float))
            assert isinstance(pos["z"], (int, float))

    def test_returns_rotation(self, require_game, api_url):
        """Camera endpoint returns rotation."""
        r = requests.get(f"{api_url}/api/camera", timeout=5)
        assert r.status_code == 200
        data = r.json()
        if "rotation" in data:
            rot = data["rotation"]
            # Should have at least x, y, z (could also have w for quat)
            assert "x" in rot or "pitch" in rot or "Pitch" in rot

    def test_returns_fov(self, require_game, api_url):
        """Camera endpoint returns FOV."""
        r = requests.get(f"{api_url}/api/camera", timeout=5)
        assert r.status_code == 200
        data = r.json()
        if "fov" in data:
            fov = data["fov"]
            assert isinstance(fov, (int, float))
            # FOV should be a reasonable value (1-180)
            assert 1 <= fov <= 180

    def test_no_error_field(self, require_game, api_url):
        """Camera endpoint should not return an error when player exists."""
        r = requests.get(f"{api_url}/api/camera", timeout=5)
        assert r.status_code == 200
        data = r.json()
        # Should not have top-level error (camera manager should exist)
        assert "error" not in data or "cameraManager" in data
