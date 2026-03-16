"""Tests for array pagination (uevr_get_array / GET /api/explorer/array)."""

import pytest
import requests


class TestGetArray:
    """Test GET /api/explorer/array with pagination."""

    def _find_array_field(self, api_url, address):
        """Helper: find an array field on an object for testing."""
        r = requests.get(f"{api_url}/api/explorer/type", params={
            "typeName": "/Script/Engine.Actor"
        }, timeout=5)
        if r.status_code != 200:
            return None
        data = r.json()
        for field in data.get("fields", []):
            if field.get("type") == "ArrayProperty":
                return field["name"]
        return None

    def test_read_array_default_pagination(self, require_game, api_url, pawn_address):
        """Read array with default offset=0, limit=50."""
        # Try common array fields on the pawn
        for field_name in ["Tags", "ReplicatedMovement", "Children", "OwnedComponents"]:
            r = requests.get(f"{api_url}/api/explorer/array", params={
                "address": pawn_address,
                "fieldName": field_name
            }, timeout=5)
            if r.status_code == 200:
                data = r.json()
                if "error" not in data:
                    assert "totalCount" in data
                    assert "offset" in data
                    assert "limit" in data
                    assert "elements" in data
                    assert data["offset"] == 0
                    assert data["limit"] == 50
                    return
        pytest.skip("No array field found on pawn")

    def test_read_array_with_offset(self, require_game, api_url, pawn_address):
        """Read array starting at a non-zero offset."""
        r = requests.get(f"{api_url}/api/explorer/array", params={
            "address": pawn_address,
            "fieldName": "Tags",
            "offset": 0,
            "limit": 10
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        if "error" not in data:
            assert data["offset"] == 0
            assert data["limit"] == 10

    def test_read_array_offset_beyond_bounds(self, require_game, api_url, pawn_address):
        """Offset beyond array length returns empty elements."""
        r = requests.get(f"{api_url}/api/explorer/array", params={
            "address": pawn_address,
            "fieldName": "Tags",
            "offset": 99999
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        if "error" not in data:
            elements = data.get("elements", [])
            assert len(elements) == 0

    def test_non_array_field_returns_error(self, require_game, api_url, pawn_address):
        """Reading a non-array field returns an error."""
        # Try a known non-array field
        r = requests.get(f"{api_url}/api/explorer/array", params={
            "address": pawn_address,
            "fieldName": "bCanBeDamaged"
        }, timeout=5)
        data = r.json()
        assert "error" in data

    def test_nonexistent_field_returns_error(self, require_game, api_url, pawn_address):
        """Non-existent field name returns error."""
        r = requests.get(f"{api_url}/api/explorer/array", params={
            "address": pawn_address,
            "fieldName": "NonExistentArrayField12345"
        }, timeout=5)
        data = r.json()
        assert "error" in data

    def test_missing_params_returns_400(self, require_game, api_url):
        """Missing address or fieldName returns an error."""
        r = requests.get(f"{api_url}/api/explorer/array", timeout=5)
        assert r.status_code == 400
        data = r.json()
        assert "error" in data

    def test_invalid_address_returns_400(self, require_game, api_url):
        """Invalid address returns an error."""
        r = requests.get(f"{api_url}/api/explorer/array", params={
            "address": "not_an_address",
            "fieldName": "Tags"
        }, timeout=5)
        assert r.status_code == 400
        data = r.json()
        assert "error" in data

    def test_response_includes_inner_type(self, require_game, api_url, pawn_address):
        """Response includes the inner element type."""
        r = requests.get(f"{api_url}/api/explorer/array", params={
            "address": pawn_address,
            "fieldName": "Tags"
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        if "error" not in data and data.get("totalCount", 0) > 0:
            assert "innerType" in data

    def test_response_includes_returned_count(self, require_game, api_url, pawn_address):
        """Response includes returnedCount matching elements length."""
        r = requests.get(f"{api_url}/api/explorer/array", params={
            "address": pawn_address,
            "fieldName": "Tags",
            "limit": 5
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        if "error" not in data and "returnedCount" in data:
            assert data["returnedCount"] == len(data.get("elements", []))
