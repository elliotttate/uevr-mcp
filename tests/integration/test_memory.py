"""Tests for raw memory tools (uevr_read_memory, uevr_read_typed)."""

import pytest
import requests


class TestReadMemory:
    """Test GET /api/explorer/memory — hex dump."""

    def test_read_memory_default_size(self, require_game, api_url, pawn_address):
        """Read 256 bytes (default) from a known valid object address."""
        r = requests.get(f"{api_url}/api/explorer/memory", params={
            "address": pawn_address
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert "dump" in data
        assert "address" in data
        assert "size" in data
        assert data["size"] == 256
        assert data["address"] == pawn_address

    def test_read_memory_custom_size(self, require_game, api_url, pawn_address):
        """Read a custom number of bytes."""
        r = requests.get(f"{api_url}/api/explorer/memory", params={
            "address": pawn_address,
            "size": 64
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert data["size"] == 64

    def test_read_memory_large_size(self, require_game, api_url, pawn_address):
        """Read a large block (up to 8192). May AV if region is shorter than requested."""
        r = requests.get(f"{api_url}/api/explorer/memory", params={
            "address": pawn_address,
            "size": 2048
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert data["size"] == 2048

    def test_read_memory_too_large_returns_error(self, require_game, api_url, pawn_address):
        """Size > 8192 returns error."""
        r = requests.get(f"{api_url}/api/explorer/memory", params={
            "address": pawn_address,
            "size": 9000
        }, timeout=5)
        data = r.json()
        assert "error" in data

    def test_read_memory_zero_size_returns_error(self, require_game, api_url, pawn_address):
        """Size 0 returns error."""
        r = requests.get(f"{api_url}/api/explorer/memory", params={
            "address": pawn_address,
            "size": 0
        }, timeout=5)
        data = r.json()
        assert "error" in data

    def test_read_memory_hex_dump_format(self, require_game, api_url, pawn_address):
        """Hex dump contains expected format: address + hex bytes + ASCII sidebar."""
        r = requests.get(f"{api_url}/api/explorer/memory", params={
            "address": pawn_address,
            "size": 32
        }, timeout=5)
        assert r.status_code == 200
        dump = r.json()["dump"]
        lines = dump.strip().split("\n")
        assert len(lines) >= 1
        # Each line should have hex address, hex bytes, and |ascii|
        for line in lines:
            assert "|" in line  # ASCII sidebar delimiter

    def test_read_memory_missing_address_returns_400(self, require_game, api_url):
        """Missing address returns an error."""
        r = requests.get(f"{api_url}/api/explorer/memory", timeout=5)
        assert r.status_code == 400
        data = r.json()
        assert "error" in data

    def test_read_memory_invalid_address_returns_400(self, require_game, api_url):
        """Invalid address returns an error."""
        r = requests.get(f"{api_url}/api/explorer/memory", params={
            "address": "not_hex"
        }, timeout=5)
        assert r.status_code == 400
        data = r.json()
        assert "error" in data


class TestReadTyped:
    """Test GET /api/explorer/typed — typed value reads."""

    @pytest.mark.parametrize("type_name", ["u8", "i8", "u16", "i16", "u32", "i32", "u64", "i64", "f32", "f64", "ptr"])
    def test_read_single_value_each_type(self, require_game, api_url, pawn_address, type_name):
        """Read a single value of each supported type."""
        r = requests.get(f"{api_url}/api/explorer/typed", params={
            "address": pawn_address,
            "type": type_name
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert "values" in data
        assert len(data["values"]) == 1
        assert data["type"] == type_name
        assert data["count"] == 1

    def test_read_multiple_values(self, require_game, api_url, pawn_address):
        """Read multiple sequential values."""
        r = requests.get(f"{api_url}/api/explorer/typed", params={
            "address": pawn_address,
            "type": "u32",
            "count": 10
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert len(data["values"]) == 10
        assert data["count"] == 10

    def test_read_with_custom_stride(self, require_game, api_url, pawn_address):
        """Read values with a custom stride."""
        r = requests.get(f"{api_url}/api/explorer/typed", params={
            "address": pawn_address,
            "type": "f32",
            "count": 3,
            "stride": 16  # Read every 16th byte
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert data["stride"] == 16
        assert len(data["values"]) == 3

    def test_auto_stride_from_type(self, require_game, api_url, pawn_address):
        """Stride is auto-calculated from type when not specified."""
        r = requests.get(f"{api_url}/api/explorer/typed", params={
            "address": pawn_address,
            "type": "f64",
            "count": 2
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert data["stride"] == 8  # sizeof(double)

    def test_count_capped_at_50(self, require_game, api_url, pawn_address):
        """Count is capped at 50."""
        r = requests.get(f"{api_url}/api/explorer/typed", params={
            "address": pawn_address,
            "type": "u8",
            "count": 100
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert len(data["values"]) <= 50

    def test_invalid_type_returns_error(self, require_game, api_url, pawn_address):
        """Invalid type name returns error."""
        r = requests.get(f"{api_url}/api/explorer/typed", params={
            "address": pawn_address,
            "type": "invalid_type"
        }, timeout=5)
        data = r.json()
        assert "error" in data

    def test_missing_params_returns_400(self, require_game, api_url):
        """Missing address or type returns an error."""
        r = requests.get(f"{api_url}/api/explorer/typed", timeout=5)
        assert r.status_code == 400
        data = r.json()
        assert "error" in data

    def test_ptr_type_returns_hex_strings(self, require_game, api_url, pawn_address):
        """ptr type returns values as hex address strings."""
        r = requests.get(f"{api_url}/api/explorer/typed", params={
            "address": pawn_address,
            "type": "ptr"
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        # ptr values should be strings starting with 0x
        val = data["values"][0]
        if isinstance(val, str):
            assert val.startswith("0x") or val.startswith("0X")
