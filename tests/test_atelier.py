"""Include standalone Atelier tests without shadowing the atelier application."""
from pathlib import Path
import unittest


def load_tests(loader, tests, pattern):
    directory = str(Path(__file__).parent / 'atelier')
    return unittest.TestLoader().discover(directory, pattern='test_*.py', top_level_dir=directory)
