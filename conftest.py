"""Repo-wide pytest setup (2026-11-23): the opt-in slow tier.

Tests marked @pytest.mark.slow (real models, big downloads, minutes of runtime) are skipped unless the run asks for them:
    GALATEA_SLOW_TESTS=1 scripts/galatea-test.sh --python      or      pytest --run-slow
The default run (CI, galatea-test.sh) stays fast and needs no model downloads."""
import os

import pytest


def pytest_addoption(parser):
    parser.addoption("--run-slow", action="store_true", help="also run tests marked slow (or set GALATEA_SLOW_TESTS=1)")


def pytest_configure(config):
    config.addinivalue_line("markers", "slow: opt-in slow tier (GALATEA_SLOW_TESTS=1 or --run-slow)")


def slow_tier_on(config):
    return config.getoption("--run-slow") or os.environ.get("GALATEA_SLOW_TESTS") == "1"


def pytest_collection_modifyitems(config, items):
    if slow_tier_on(config):
        return
    skip = pytest.mark.skip(reason="slow tier: set GALATEA_SLOW_TESTS=1 or pass --run-slow")
    for item in items:
        if "slow" in item.keywords:
            item.add_marker(skip)
