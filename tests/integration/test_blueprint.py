"""Tests for Blueprint editing endpoints (spawn, component, CDO, destroy, transform)."""

import pytest
import requests


class TestBlueprintSpawn:
    """Test POST /api/blueprint/spawn."""

    def test_spawn_missing_class_returns_400(self, require_game, api_url):
        """Missing className returns 400."""
        r = requests.post(f"{api_url}/api/blueprint/spawn", json={}, timeout=10)
        assert r.status_code == 400

    def test_spawn_invalid_json_returns_400(self, require_game, api_url):
        """Invalid JSON body returns 400."""
        r = requests.post(f"{api_url}/api/blueprint/spawn",
                          data="not json", headers={"Content-Type": "application/json"}, timeout=10)
        assert r.status_code == 400

    def test_spawn_nonexistent_class_returns_error(self, require_game, api_url):
        """Spawning a non-existent class returns error."""
        r = requests.post(f"{api_url}/api/blueprint/spawn", json={
            "className": "/Script/NonExistent.FakeClass12345XYZ"
        }, timeout=10)
        data = r.json()
        assert "error" in data

    def test_spawn_uobject(self, require_game, api_url):
        """Spawn a basic UObject (e.g. Object class)."""
        # Use a simple class that should exist in any UE game
        r = requests.post(f"{api_url}/api/blueprint/spawn", json={
            "className": "Class /Script/CoreUObject.Object"
        }, timeout=10)
        data = r.json()
        # Might succeed (200) or fail (404/500) depending on whether transient package exists.
        # Just verify we get a structured JSON response either way.
        assert "success" in data or "error" in data

    def test_spawn_returns_address(self, require_game, api_url):
        """Successful spawn returns an address."""
        r = requests.post(f"{api_url}/api/blueprint/spawn", json={
            "className": "Class /Script/CoreUObject.Object"
        }, timeout=10)
        data = r.json()
        if data.get("success"):
            assert "address" in data
            assert data["address"].startswith("0x")

    def test_spawn_with_invalid_outer(self, require_game, api_url):
        """Spawning with an invalid outer address returns error."""
        r = requests.post(f"{api_url}/api/blueprint/spawn", json={
            "className": "Class /Script/CoreUObject.Object",
            "outerAddress": "0xDEADBEEF"
        }, timeout=10)
        data = r.json()
        assert "error" in data


class TestBlueprintSpawned:
    """Test GET /api/blueprint/spawned."""

    def test_spawned_returns_200(self, require_game, api_url):
        """Spawned list endpoint returns 200."""
        r = requests.get(f"{api_url}/api/blueprint/spawned", timeout=5)
        assert r.status_code == 200

    def test_spawned_has_array(self, require_game, api_url):
        """Response contains spawned array and count."""
        r = requests.get(f"{api_url}/api/blueprint/spawned", timeout=5)
        data = r.json()
        assert "spawned" in data
        assert isinstance(data["spawned"], list)
        assert "count" in data

    def test_spawned_object_appears_in_list(self, require_game, api_url):
        """A spawned object appears in the spawned list."""
        # Spawn something
        spawn_r = requests.post(f"{api_url}/api/blueprint/spawn", json={
            "className": "Class /Script/CoreUObject.Object"
        }, timeout=10)
        spawn_data = spawn_r.json()
        if not spawn_data.get("success"):
            pytest.skip("Could not spawn test object")

        addr = spawn_data["address"]

        # Check list
        r = requests.get(f"{api_url}/api/blueprint/spawned", timeout=5)
        data = r.json()
        addresses = [s["address"] for s in data["spawned"]]
        assert addr in addresses

    def test_spawned_entries_have_metadata(self, require_game, api_url):
        """Spawned entries have address, alive, and class fields."""
        r = requests.get(f"{api_url}/api/blueprint/spawned", timeout=5)
        data = r.json()
        for entry in data["spawned"]:
            assert "address" in entry
            assert "alive" in entry
            assert isinstance(entry["alive"], bool)


class TestBlueprintCdo:
    """Test GET/POST /api/blueprint/cdo — Class Default Object."""

    def test_cdo_missing_class_returns_400(self, require_game, api_url):
        """Missing className returns 400."""
        r = requests.get(f"{api_url}/api/blueprint/cdo", timeout=5)
        assert r.status_code == 400

    def test_cdo_nonexistent_class_returns_error(self, require_game, api_url):
        """Non-existent class returns error."""
        r = requests.get(f"{api_url}/api/blueprint/cdo", params={
            "className": "/Script/NonExistent.FakeClass12345"
        }, timeout=10)
        data = r.json()
        assert "error" in data

    def test_cdo_actor_class(self, require_game, api_url):
        """Get CDO for Actor class."""
        r = requests.get(f"{api_url}/api/blueprint/cdo", params={
            "className": "Class /Script/Engine.Actor"
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        if "error" not in data:
            assert "cdoAddress" in data
            assert data["cdoAddress"].startswith("0x")
            assert "fields" in data
            assert isinstance(data["fields"], list)

    def test_cdo_fields_have_metadata(self, require_game, api_url):
        """CDO fields have name, type, and offset."""
        r = requests.get(f"{api_url}/api/blueprint/cdo", params={
            "className": "Class /Script/Engine.Actor"
        }, timeout=10)
        data = r.json()
        if "error" in data:
            pytest.skip("Could not get Actor CDO")
        for field in data["fields"][:5]:  # Check first 5
            assert "name" in field
            assert "type" in field
            assert "offset" in field

    def test_write_cdo_missing_params_returns_400(self, require_game, api_url):
        """POST CDO with missing params returns 400."""
        r = requests.post(f"{api_url}/api/blueprint/cdo", json={
            "className": "Class /Script/Engine.Actor"
            # Missing fieldName and value
        }, timeout=10)
        assert r.status_code == 400

    def test_write_cdo_nonexistent_field_returns_error(self, require_game, api_url):
        """Writing a non-existent field returns error."""
        r = requests.post(f"{api_url}/api/blueprint/cdo", json={
            "className": "Class /Script/Engine.Actor",
            "fieldName": "NonExistentField12345",
            "value": 42
        }, timeout=10)
        data = r.json()
        assert "error" in data


class TestBlueprintAddComponent:
    """Test POST /api/blueprint/add_component."""

    def test_add_component_missing_params_returns_400(self, require_game, api_url):
        """Missing actorAddress or componentClass returns 400."""
        r = requests.post(f"{api_url}/api/blueprint/add_component", json={
            "actorAddress": "0x12345"
        }, timeout=10)
        assert r.status_code == 400

    def test_add_component_invalid_actor_returns_error(self, require_game, api_url):
        """Adding component to invalid actor returns error."""
        r = requests.post(f"{api_url}/api/blueprint/add_component", json={
            "actorAddress": "0xDEADBEEF",
            "componentClass": "/Script/Engine.StaticMeshComponent"
        }, timeout=10)
        data = r.json()
        assert "error" in data

    def test_add_component_nonexistent_class_returns_error(self, require_game, api_url, pawn_address):
        """Adding a non-existent component class returns error."""
        r = requests.post(f"{api_url}/api/blueprint/add_component", json={
            "actorAddress": pawn_address,
            "componentClass": "/Script/NonExistent.FakeComponent12345"
        }, timeout=10)
        data = r.json()
        assert "error" in data


class TestBlueprintDestroy:
    """Test POST /api/blueprint/destroy."""

    def test_destroy_missing_address_returns_400(self, require_game, api_url):
        """Missing address returns 400."""
        r = requests.post(f"{api_url}/api/blueprint/destroy", json={}, timeout=10)
        assert r.status_code == 400

    def test_destroy_invalid_address_returns_error(self, require_game, api_url):
        """Invalid object address returns error."""
        r = requests.post(f"{api_url}/api/blueprint/destroy", json={
            "address": "0xDEADBEEF"
        }, timeout=10)
        data = r.json()
        assert "error" in data


class TestBlueprintSetTransform:
    """Test POST /api/blueprint/set_transform."""

    def test_set_transform_missing_address_returns_400(self, require_game, api_url):
        """Missing address returns 400."""
        r = requests.post(f"{api_url}/api/blueprint/set_transform", json={}, timeout=10)
        assert r.status_code == 400

    def test_set_transform_invalid_address_returns_error(self, require_game, api_url):
        """Invalid address returns error."""
        r = requests.post(f"{api_url}/api/blueprint/set_transform", json={
            "address": "0xDEADBEEF",
            "location": {"x": 0, "y": 0, "z": 0}
        }, timeout=10)
        data = r.json()
        assert "error" in data

    def test_set_transform_on_pawn(self, require_game, api_url, pawn_address):
        """Set transform on the local pawn (location only)."""
        r = requests.post(f"{api_url}/api/blueprint/set_transform", json={
            "address": pawn_address,
            "location": {"x": 0, "y": 0, "z": 500}
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        # Should have success or at least a location result
        assert "success" in data or "location" in data


class TestBlueprintEndpointsListed:
    """Test that blueprint endpoints appear in /api index."""

    EXPECTED_BLUEPRINT_ENDPOINTS = [
        "/api/blueprint/spawn",
        "/api/blueprint/add_component",
        "/api/blueprint/cdo",
        "/api/blueprint/destroy",
        "/api/blueprint/set_transform",
        "/api/blueprint/spawned",
    ]

    @pytest.mark.parametrize("endpoint", EXPECTED_BLUEPRINT_ENDPOINTS)
    def test_endpoint_listed(self, require_game, api_url, endpoint):
        """Blueprint endpoint appears in the /api index."""
        r = requests.get(f"{api_url}/api", timeout=5)
        data = r.json()
        assert endpoint in data["endpoints"], f"'{endpoint}' not in endpoint index"
