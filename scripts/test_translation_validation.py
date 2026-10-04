import unittest
from pathlib import Path
import importlib.util

spec = importlib.util.spec_from_file_location('validation', Path(__file__).with_name('validate-translations.py'))
validation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(validation)


class TranslationValidationTests(unittest.TestCase):
    def test_partial_catalog_and_reordered_placeholders(self):
        validation.validate_catalog({'greeting': 'Hello', 'format': '{0} / {1:0.0}'}, {'format': '{1:0.0} / {0}'})

    def test_unknown_empty_and_changed_placeholders_rejected(self):
        for translation in ({'extra': 'text'}, {'format': ' '}, {'format': '{0}'}, {'format': '{0} / {1}'}):
            with self.subTest(translation=translation), self.assertRaises(ValueError):
                validation.validate_catalog({'format': '{0} / {1:0.0}'}, translation)

    def test_literal_braces_are_not_placeholders(self):
        self.assertEqual(['{0}'], validation.placeholders('{{literal}} {0}'))


if __name__ == '__main__':
    unittest.main()
