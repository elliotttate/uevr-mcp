"""Tests for the chain query feature (uevr_chain / POST /api/explorer/chain)."""

import pytest
import requests


class TestChainQuery:
    """Test multi-step object graph traversal."""

    def test_chain_single_field_step(self, require_game, api_url, controller_address):
        """Chain with a single field step follows an ObjectProperty reference."""
        r = requests.post(f"{api_url}/api/explorer/chain", json={
            "address": controller_address,
            "steps": [
                {"type": "field", "name": "PlayerCameraManager"}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        # Should return addresses (no collect step)
        assert "addresses" in data or "results" in data
        if "addresses" in data:
            assert data["count"] >= 0

    def test_chain_method_step(self, require_game, api_url, pawn_address):
        """Chain with a method step calls a getter and follows the result."""
        r = requests.post(f"{api_url}/api/explorer/chain", json={
            "address": pawn_address,
            "steps": [
                {"type": "method", "name": "GetController"}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        # May or may not find a result depending on the game
        assert "addresses" in data or "results" in data or "count" in data

    def test_chain_collect_step(self, require_game, api_url, pawn_address):
        """Chain with a collect step reads fields/methods from the working set."""
        r = requests.post(f"{api_url}/api/explorer/chain", json={
            "address": pawn_address,
            "steps": [
                {"type": "collect", "fields": [], "methods": ["GetActorLocation"]}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert "results" in data
        assert data["count"] >= 1
        # First result should have the pawn's address
        assert data["results"][0]["address"] == pawn_address

    def test_chain_field_then_collect(self, require_game, api_url, controller_address):
        """Chain: navigate to PlayerCameraManager, then collect FOV."""
        r = requests.post(f"{api_url}/api/explorer/chain", json={
            "address": controller_address,
            "steps": [
                {"type": "field", "name": "PlayerCameraManager"},
                {"type": "collect", "fields": ["DefaultFOV"], "methods": []}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert "results" in data
        # If camera manager exists, we should have results
        if data["count"] > 0:
            assert "address" in data["results"][0]

    def test_chain_empty_result_midway(self, require_game, api_url, pawn_address):
        """Chain that produces empty set mid-way returns gracefully."""
        r = requests.post(f"{api_url}/api/explorer/chain", json={
            "address": pawn_address,
            "steps": [
                {"type": "field", "name": "NonExistentField12345"}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        # Should indicate empty results
        assert data.get("count", 0) == 0

    def test_chain_filter_step(self, require_game, api_url, pawn_address):
        """Chain with filter step keeps only matching objects."""
        r = requests.post(f"{api_url}/api/explorer/chain", json={
            "address": pawn_address,
            "steps": [
                {"type": "filter", "method": "GetActorLocation", "value": {"x": 0, "y": 0, "z": 0}}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        # Result depends on actual pawn location; just verify structure
        assert "addresses" in data or "results" in data or "count" in data

    def test_chain_missing_address_returns_error(self, require_game, api_url):
        """Chain without address returns an error."""
        r = requests.post(f"{api_url}/api/explorer/chain", json={
            "steps": [{"type": "field", "name": "Foo"}]
        }, timeout=5)
        assert r.status_code == 400
        data = r.json()
        assert "error" in data

    def test_chain_missing_steps_returns_error(self, require_game, api_url, pawn_address):
        """Chain without steps returns an error."""
        r = requests.post(f"{api_url}/api/explorer/chain", json={
            "address": pawn_address
        }, timeout=5)
        assert r.status_code == 400
        data = r.json()
        assert "error" in data

    def test_chain_invalid_step_type(self, require_game, api_url, pawn_address):
        """Chain with unknown step type returns error."""
        r = requests.post(f"{api_url}/api/explorer/chain", json={
            "address": pawn_address,
            "steps": [{"type": "invalid_step_type"}]
        }, timeout=10)
        assert r.status_code in (200, 500)
        data = r.json()
        assert "error" in data

    def test_chain_field_step_requires_name(self, require_game, api_url, pawn_address):
        """Field step without name returns error."""
        r = requests.post(f"{api_url}/api/explorer/chain", json={
            "address": pawn_address,
            "steps": [{"type": "field"}]
        }, timeout=10)
        assert r.status_code in (200, 500)
        data = r.json()
        assert "error" in data
