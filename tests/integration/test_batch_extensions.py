"""Tests for the new batch operation types added to POST /api/explorer/batch."""

import pytest
import requests


class TestBatchNewOperations:
    """Test that the new operation types work in batch requests."""

    def test_batch_summary_operation(self, require_game, api_url, pawn_address):
        """Batch with 'summary' operation type."""
        r = requests.post(f"{api_url}/api/explorer/batch", json={
            "operations": [
                {"type": "summary", "address": pawn_address}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert "results" in data
        assert len(data["results"]) == 1
        result = data["results"][0]
        assert "className" in result or "error" in result

    def test_batch_get_type_operation(self, require_game, api_url):
        """Batch with 'get_type' operation type."""
        r = requests.post(f"{api_url}/api/explorer/batch", json={
            "operations": [
                {"type": "get_type", "typeName": "/Script/Engine.Actor"}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert "results" in data
        assert len(data["results"]) == 1
        result = data["results"][0]
        assert "fields" in result or "error" in result

    def test_batch_singleton_operation(self, require_game, api_url):
        """Batch with 'singleton' operation type."""
        r = requests.post(f"{api_url}/api/explorer/batch", json={
            "operations": [
                {"type": "singleton", "typeName": "/Script/Engine.PlayerController"}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert "results" in data
        assert len(data["results"]) == 1

    def test_batch_read_array_operation(self, require_game, api_url, pawn_address):
        """Batch with 'read_array' operation type."""
        r = requests.post(f"{api_url}/api/explorer/batch", json={
            "operations": [
                {"type": "read_array", "address": pawn_address, "fieldName": "Tags", "offset": 0, "limit": 10}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert "results" in data
        assert len(data["results"]) == 1

    def test_batch_read_memory_operation(self, require_game, api_url, pawn_address):
        """Batch with 'read_memory' operation type."""
        r = requests.post(f"{api_url}/api/explorer/batch", json={
            "operations": [
                {"type": "read_memory", "address": pawn_address, "size": 64}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert "results" in data
        assert len(data["results"]) == 1
        result = data["results"][0]
        assert "dump" in result or "error" in result

    def test_batch_read_typed_operation(self, require_game, api_url, pawn_address):
        """Batch with 'read_typed' operation type."""
        r = requests.post(f"{api_url}/api/explorer/batch", json={
            "operations": [
                {"type": "read_typed", "address": pawn_address, "type": "u64", "count": 4}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert "results" in data
        assert len(data["results"]) == 1
        result = data["results"][0]
        assert "values" in result or "error" in result

    def test_batch_mixed_old_and_new_operations(self, require_game, api_url, pawn_address):
        """Batch mixing original and new operation types."""
        r = requests.post(f"{api_url}/api/explorer/batch", json={
            "operations": [
                {"type": "read_field", "address": pawn_address, "fieldName": "bCanBeDamaged"},
                {"type": "summary", "address": pawn_address},
                {"type": "read_memory", "address": pawn_address, "size": 32},
                {"type": "singleton", "typeName": "/Script/Engine.PlayerController"}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert "results" in data
        assert len(data["results"]) == 4

    def test_batch_error_in_one_doesnt_abort_others(self, require_game, api_url, pawn_address):
        """An error in one batch operation doesn't abort the others."""
        r = requests.post(f"{api_url}/api/explorer/batch", json={
            "operations": [
                {"type": "read_memory", "address": pawn_address, "size": 32},
                {"type": "singleton", "typeName": "NonExistentClass12345XYZABC"},
                {"type": "summary", "address": pawn_address}
            ]
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        results = data["results"]
        assert len(results) == 3
        # First and third should succeed, second should have error
        assert "dump" in results[0]  # read_memory succeeded
        assert "error" in results[1]  # singleton failed
        assert "className" in results[2]  # summary succeeded
