"""
UEVR-MCP Integration Test Configuration

These tests run against a live game with the UEVR-MCP plugin loaded.
Set UEVR_MCP_API_URL environment variable if not using default http://localhost:8899.

Usage:
    pip install pytest requests
    pytest tests/integration/ -v
    pytest tests/integration/ -v -k "chain"       # run only chain tests
    pytest tests/integration/ -v --live-only       # skip offline tests
"""

import os
import pytest
import requests

BASE_URL = os.environ.get("UEVR_MCP_API_URL", "http://localhost:8899")
PIPE_NAME = r"\\.\pipe\UEVR_MCP"


def is_game_running():
    """Check if the UEVR-MCP plugin HTTP server is reachable."""
    try:
        r = requests.get(f"{BASE_URL}/api/status", timeout=2)
        return r.status_code == 200
    except Exception:
        return False


@pytest.fixture(scope="session")
def api_url():
    return BASE_URL


@pytest.fixture(scope="session")
def game_running():
    """Returns True if the game is running, False otherwise."""
    return is_game_running()


@pytest.fixture(scope="session")
def require_game(game_running):
    """Skip the test if the game is not running."""
    if not game_running:
        pytest.skip("Game not running (UEVR-MCP plugin not reachable)")


@pytest.fixture(scope="session")
def player_info(require_game, api_url):
    """Get player controller and pawn addresses (cached for session)."""
    r = requests.get(f"{api_url}/api/player", timeout=5)
    assert r.status_code == 200
    data = r.json()
    return data


@pytest.fixture(scope="session")
def pawn_address(player_info):
    """Get the local pawn address."""
    pawn = player_info.get("pawn")
    if not pawn or not pawn.get("address"):
        pytest.skip("No local pawn available")
    return pawn["address"]


@pytest.fixture(scope="session")
def controller_address(player_info):
    """Get the player controller address."""
    ctrl = player_info.get("controller")
    if not ctrl or not ctrl.get("address"):
        pytest.skip("No player controller available")
    return ctrl["address"]


@pytest.fixture(scope="session")
def singletons(require_game, api_url):
    """Get singletons list (cached for session)."""
    r = requests.get(f"{api_url}/api/explorer/singletons", timeout=5)
    assert r.status_code == 200
    return r.json()


@pytest.fixture(scope="session")
def engine_address(singletons):
    """Get the GameEngine singleton address."""
    for s in singletons.get("singletons", []):
        if s.get("name") == "GameEngine":
            return s["address"]
    pytest.skip("GameEngine singleton not found")
