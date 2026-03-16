"""Tests for Lua script file management endpoints."""

import pytest
import requests
import uuid


class TestLuaScriptWrite:
    """Test POST /api/lua/scripts/write."""

    def test_write_script(self, require_game, api_url):
        """Write a simple script file."""
        filename = f"_test_{uuid.uuid4().hex[:8]}.lua"
        r = requests.post(f"{api_url}/api/lua/scripts/write", json={
            "filename": filename,
            "content": "-- test script\nreturn 42\n"
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True
        assert "path" in data

        # Clean up
        requests.delete(f"{api_url}/api/lua/scripts/delete",
                        json={"filename": filename}, timeout=5)

    def test_write_script_missing_filename(self, require_game, api_url):
        """Missing filename returns 400."""
        r = requests.post(f"{api_url}/api/lua/scripts/write", json={
            "content": "return 1"
        }, timeout=10)
        assert r.status_code == 400

    def test_write_script_path_traversal_blocked(self, require_game, api_url):
        """Path traversal in filename is rejected."""
        r = requests.post(f"{api_url}/api/lua/scripts/write", json={
            "filename": "../../../evil.lua",
            "content": "return 1"
        }, timeout=10)
        assert r.status_code == 400

    def test_write_script_backslash_blocked(self, require_game, api_url):
        """Backslash in filename is rejected."""
        r = requests.post(f"{api_url}/api/lua/scripts/write", json={
            "filename": "sub\\evil.lua",
            "content": "return 1"
        }, timeout=10)
        assert r.status_code == 400

    def test_write_autorun_script(self, require_game, api_url):
        """Write a script to the autorun folder."""
        filename = f"_test_{uuid.uuid4().hex[:8]}.lua"
        r = requests.post(f"{api_url}/api/lua/scripts/write", json={
            "filename": filename,
            "content": "-- autorun test\n",
            "autorun": True
        }, timeout=10)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True
        assert "autorun" in data["path"].lower()

        # Clean up
        requests.delete(f"{api_url}/api/lua/scripts/delete",
                        json={"filename": filename, "autorun": True}, timeout=5)


class TestLuaScriptList:
    """Test GET /api/lua/scripts/list."""

    def test_list_returns_200(self, require_game, api_url):
        """Scripts list endpoint returns 200."""
        r = requests.get(f"{api_url}/api/lua/scripts/list", timeout=5)
        assert r.status_code == 200

    def test_list_has_scripts_array(self, require_game, api_url):
        """Response contains a 'scripts' array."""
        r = requests.get(f"{api_url}/api/lua/scripts/list", timeout=5)
        data = r.json()
        assert "scripts" in data
        assert isinstance(data["scripts"], list)

    def test_written_script_appears_in_list(self, require_game, api_url):
        """A script we write shows up in the list."""
        filename = f"_test_list_{uuid.uuid4().hex[:8]}.lua"
        requests.post(f"{api_url}/api/lua/scripts/write", json={
            "filename": filename,
            "content": "-- list test\n"
        }, timeout=10)

        r = requests.get(f"{api_url}/api/lua/scripts/list", timeout=5)
        data = r.json()
        filenames = [s["filename"] for s in data["scripts"]]
        assert filename in filenames

        # Clean up
        requests.delete(f"{api_url}/api/lua/scripts/delete",
                        json={"filename": filename}, timeout=5)

    def test_list_entries_have_metadata(self, require_game, api_url):
        """Each entry has filename, autorun, and size fields."""
        filename = f"_test_meta_{uuid.uuid4().hex[:8]}.lua"
        requests.post(f"{api_url}/api/lua/scripts/write", json={
            "filename": filename,
            "content": "return 1\n"
        }, timeout=10)

        r = requests.get(f"{api_url}/api/lua/scripts/list", timeout=5)
        data = r.json()
        entry = next((s for s in data["scripts"] if s["filename"] == filename), None)
        assert entry is not None
        assert "autorun" in entry
        assert "size" in entry
        assert entry["autorun"] is False
        assert entry["size"] > 0

        # Clean up
        requests.delete(f"{api_url}/api/lua/scripts/delete",
                        json={"filename": filename}, timeout=5)


class TestLuaScriptRead:
    """Test GET /api/lua/scripts/read."""

    def test_read_written_script(self, require_game, api_url):
        """Read back content of a written script."""
        filename = f"_test_read_{uuid.uuid4().hex[:8]}.lua"
        content = "-- read test\nreturn 'hello'\n"
        requests.post(f"{api_url}/api/lua/scripts/write", json={
            "filename": filename,
            "content": content
        }, timeout=10)

        r = requests.get(f"{api_url}/api/lua/scripts/read", params={
            "filename": filename
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert data["filename"] == filename
        assert data["content"] == content

        # Clean up
        requests.delete(f"{api_url}/api/lua/scripts/delete",
                        json={"filename": filename}, timeout=5)

    def test_read_nonexistent_returns_404(self, require_game, api_url):
        """Reading a non-existent script returns 404."""
        r = requests.get(f"{api_url}/api/lua/scripts/read", params={
            "filename": "nonexistent_xyz_12345.lua"
        }, timeout=5)
        assert r.status_code == 404

    def test_read_missing_filename_returns_400(self, require_game, api_url):
        """Missing filename parameter returns 400."""
        r = requests.get(f"{api_url}/api/lua/scripts/read", timeout=5)
        assert r.status_code == 400


class TestLuaScriptDelete:
    """Test DELETE /api/lua/scripts/delete."""

    def test_delete_existing_script(self, require_game, api_url):
        """Delete a script that exists."""
        filename = f"_test_del_{uuid.uuid4().hex[:8]}.lua"
        requests.post(f"{api_url}/api/lua/scripts/write", json={
            "filename": filename,
            "content": "-- delete test\n"
        }, timeout=10)

        r = requests.delete(f"{api_url}/api/lua/scripts/delete", json={
            "filename": filename
        }, timeout=5)
        assert r.status_code == 200
        data = r.json()
        assert data["success"] is True

        # Verify it's gone
        r2 = requests.get(f"{api_url}/api/lua/scripts/read", params={
            "filename": filename
        }, timeout=5)
        assert r2.status_code == 404

    def test_delete_nonexistent_returns_404(self, require_game, api_url):
        """Deleting a non-existent script returns 404."""
        r = requests.delete(f"{api_url}/api/lua/scripts/delete", json={
            "filename": "nonexistent_xyz_12345.lua"
        }, timeout=5)
        assert r.status_code == 404

    def test_delete_missing_filename_returns_400(self, require_game, api_url):
        """Missing filename returns 400."""
        r = requests.delete(f"{api_url}/api/lua/scripts/delete", json={}, timeout=5)
        assert r.status_code == 400
