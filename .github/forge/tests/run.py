#!/usr/bin/env python3
"""Runs every test of the engine. Usage: python3 .github/forge/tests/run.py   (no GitHub needed; needs jq, yq, PyYAML)"""
import os
import sys
import unittest

here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, here)
suite = unittest.defaultTestLoader.discover(here, pattern="test_*.py", top_level_dir=here)
sys.exit(0 if unittest.TextTestRunner(verbosity=1).run(suite).wasSuccessful() else 1)
