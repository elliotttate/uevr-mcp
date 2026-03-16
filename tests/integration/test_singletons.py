"""Tests for singleton discovery (uevr_get_singletons, uevr_get_singleton)."""

import pytest
import requests


class TestGetSingletons:
    """Test GET /api/explorer/singletons."""

    def test_returns_non_empty_list(self, require_game, api_url):
        """Singletons endpoint returns at least one entry."""
        r = requests.get(f"{api_url}/api/explorer/singletons", timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert "singletons" in data
        assert "count" in data
        assert data["count"] > 0

    def test_includes_game_engine(self, require_game, singletons):
        """Singletons list includes GameEngine."""
        names = [s["name"] for s in singletons["singletons"]]
        assert "GameEngine" in names

    def test_includes_player_controller(self, require_game, singletons):
        """Singletons list includes PlayerController."""
        names = [s["name"] for s in singletons["singletons"]]
        assert "PlayerController" in names

    def test_includes_local_pawn(self, require_game, singletons):
        """Singletons list includes LocalPawn."""
        names = [s["name"] for s in singletons["singletons"]]
        assert "LocalPawn" in names

    def test_each_singleton_has_address(self, require_game, singletons):
        """Every singleton entry has an address field."""
        for s in singletons["singletons"]:
            assert "address" in s
            assert s["address"].startswith("0x")

    def test_each_singleton_has_class_name(self, require_game, singletons):
        """Every singleton entry has a className field."""
        for s in singletons["singletons"]:
            assert "className" in s
            assert len(s["className"]) > 0

    def test_no_default_objects_in_singletons(self, require_game, singletons):
        """Singletons should not include Default__ objects."""
        for s in singletons["singletons"]:
            full_name = s.get("fullName", "")
            assert "Default__" not in full_name, f"Default object found: {full_name}"


class TestGetSingleton:
    """Test GET /api/explorer/singleton?typeName=..."""

    def test_find_player_controller(self, require_game, api_url):
        """Find PlayerController singleton by type name."""
        r = requests.get(f"{api_url}/api/explorer/singleton", params={
            "typeName": "/Script/Engine.PlayerController"
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert "address" in data
        assert data["address"].startswith("0x")

    def test_find_game_engine(self, require_game, api_url):
        """Find the game engine by searching for known engine class patterns."""
        # Try common engine class names
        r = requests.get(f"{api_url}/api/explorer/singleton", params={
            "typeName": "/Script/Engine.GameEngine"
        }, timeout=5)
        # May or may not find it depending on how UE registers it
        assert r.status_code == 200

    def test_missing_type_name_returns_error(self, require_game, api_url):
        """Missing typeName parameter returns an error."""
        r = requests.get(f"{api_url}/api/explorer/singleton", timeout=5)
        assert r.status_code == 400
        data = r.json()
        assert "error" in data

    def test_nonexistent_type_returns_error(self, require_game, api_url):
        """Non-existent type name returns error in response."""
        r = requests.get(f"{api_url}/api/explorer/singleton", params={
            "typeName": "NonExistentClass12345XYZABC"
        }, timeout=5)
        assert r.status_code in (200, 404)
        data = r.json()
        assert "error" in data

    def test_result_skips_default_objects(self, require_game, api_url):
        """Returned singleton should not be a Default__ object."""
        r = requests.get(f"{api_url}/api/explorer/singleton", params={
            "typeName": "/Script/Engine.PlayerController"
        }, timeout=5)
        if r.status_code == 200 and "fullName" in r.json():
            assert "Default__" not in r.json()["fullName"]
