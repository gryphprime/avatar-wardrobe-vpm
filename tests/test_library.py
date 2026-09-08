import io
import json
from pathlib import Path
import stat
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'Packages/dev.gryphprime.avatar-wardrobe/Desktop'))
from wardrobe_library import Library, unpack, digest


class LibraryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.library = Library(self.root / 'library')
        self.project = self.root / 'project'
        (self.project / 'ProjectSettings').mkdir(parents=True)
        (self.project / 'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: 2022.3.22f1')

    def tearDown(self):
        self.temp.cleanup()

    def archive(self, name='product.zip', value='prefab', guid='a'*32):
        path = self.root / name
        with zipfile.ZipFile(path, 'w') as z:
            z.writestr('Assets/Outfit.prefab', value)
            z.writestr('Assets/Outfit.prefab.meta', 'fileFormatVersion: 2\nguid: ' + guid + '\n')
        return path

    def test_duplicate_archive_and_immutable_original(self):
        source = self.archive()
        before = source.read_bytes()
        first = self.library.add(source)
        second = self.library.add(source)
        self.assertTrue(second['duplicate'])
        self.assertEqual(first['hash'], second['hash'])
        self.assertEqual(len(self.library.list()), 1)
        self.assertEqual(source.read_bytes(), before)
        self.assertEqual(digest(self.library.root/'versions'/first['hash']/'original'), first['hash'])

    def test_traversal_and_links_rejected(self):
        for index, name in enumerate(('../escape', '/absolute', 'C:/drive', 'Assets/../escape', 'Assets\\escape')):
            path = self.root / ('bad%d.zip' % index)
            with zipfile.ZipFile(path, 'w') as z:
                z.writestr(name, 'evil')
            with self.assertRaises(ValueError):
                self.library.add(path)
        path = self.root / 'link.zip'
        with zipfile.ZipFile(path, 'w') as z:
            info = zipfile.ZipInfo('Assets/link')
            info.external_attr = (stat.S_IFLNK | 0o777) << 16
            z.writestr(info, '../../outside')
        with self.assertRaises(ValueError):
            self.library.add(path)
        self.assertEqual(self.library.list(), [])

    def test_nested_unitypackage(self):
        package = io.BytesIO()
        with tarfile.open(fileobj=package, mode='w:gz') as tar:
            for name, value in [('guid/pathname', b'Assets/Nails.mat'), ('guid/asset', b'material'), ('guid/asset.meta', b'guid: ' + b'a'*32)]:
                info = tarfile.TarInfo(name); info.size = len(value)
                tar.addfile(info, io.BytesIO(value))
        source = self.root / 'bundle.zip'
        with zipfile.ZipFile(source, 'w') as z:
            z.writestr('Nails.unitypackage', package.getvalue())
        record = self.library.add(source)
        plan = self.library.import_plan(record['hash'], self.project)
        self.assertEqual({x['destination'] for x in plan['files']}, {'Assets/Nails.mat','Assets/Nails.mat.meta'})

    def test_project_copy_is_independent_and_tracked(self):
        record = self.library.add(self.archive())
        plan = self.library.import_plan(record['hash'], self.project)
        result = self.library.apply_import(record['hash'], self.project, plan)
        self.assertEqual(result['copied'], 2)
        target = self.project / 'Assets/Outfit.prefab'
        target.write_text('project-only edit')
        original = self.library.root / 'versions' / record['hash'] / 'files/Assets/Outfit.prefab'
        self.assertEqual(original.read_text(), 'prefab')
        self.assertEqual(len(self.library.list()[0]['usage']), 1)

    def test_changed_review_and_conflicts_rejected(self):
        record = self.library.add(self.archive())
        plan = self.library.import_plan(record['hash'], self.project)
        (self.project/'Assets').mkdir()
        (self.project/'Assets/Outfit.prefab').write_text('different')
        with self.assertRaises(ValueError):
            self.library.apply_import(record['hash'], self.project, plan)
        current = self.library.import_plan(record['hash'], self.project)
        self.assertEqual(current['conflicts'], 1)
        with self.assertRaises(ValueError):
            self.library.apply_import(record['hash'], self.project, current)
        self.assertEqual((self.project/'Assets/Outfit.prefab').read_text(), 'different')

    def test_guid_collision_rejected(self):
        record = self.library.add(self.archive())
        (self.project/'Assets').mkdir()
        (self.project/'Assets/Elsewhere.prefab.meta').write_text('guid: ' + 'a'*32)
        plan = self.library.import_plan(record['hash'], self.project)
        self.assertEqual(plan['guidConflicts'], [{'guid': 'a'*32, 'destination': 'Assets/Outfit.prefab', 'existingPaths': ['Assets/Elsewhere.prefab']}])
        with self.assertRaisesRegex(ValueError, 'GUID already exists'):
            self.library.apply_import(record['hash'], self.project, plan)
        self.assertFalse((self.project/'Assets/Outfit.prefab').exists())

    def test_symlink_destination_rejected(self):
        record = self.library.add(self.archive())
        outside = self.root/'outside'; outside.mkdir()
        (self.project/'Assets').symlink_to(outside, target_is_directory=True)
        with self.assertRaises(ValueError):
            self.library.import_plan(record['hash'], self.project)
        self.assertEqual(list(outside.iterdir()), [])

    def test_tampered_library_rolls_back_only_new_files(self):
        record = self.library.add(self.archive())
        plan = self.library.import_plan(record['hash'], self.project)
        target = self.library.root/'versions'/record['hash']/'files/Assets/Outfit.prefab.meta'
        target.chmod(0o644); target.write_text('tampered')
        with self.assertRaises(ValueError):
            self.library.apply_import(record['hash'], self.project, plan)
        self.assertFalse((self.project/'Assets/Outfit.prefab').exists())

    def test_duplicate_guid_inside_download_rejected(self):
        source = self.root / 'duplicate-identity.zip'
        with zipfile.ZipFile(source, 'w') as archive:
            for name in ('First', 'Second'):
                archive.writestr('Assets/' + name + '.prefab', 'prefab')
                archive.writestr('Assets/' + name + '.prefab.meta', 'guid: ' + 'a'*32)
        record = self.library.add(source)
        with self.assertRaisesRegex(ValueError, 'Duplicate Unity asset identity'):
            self.library.import_plan(record['hash'], self.project)

    def test_package_guid_collision_rejected(self):
        record = self.library.add(self.archive())
        package = self.project / 'Packages/example'
        package.mkdir(parents=True)
        (package/'Existing.prefab.meta').write_text('guid: ' + 'a'*32)
        plan = self.library.import_plan(record['hash'], self.project)
        with self.assertRaisesRegex(ValueError, 'GUID already exists'):
            self.library.apply_import(record['hash'], self.project, plan)
        self.assertFalse((self.project/'Assets/Outfit.prefab').exists())

    def test_duplicate_paths_and_bomb_budget_rejected(self):
        from unittest.mock import patch
        source = self.root / 'duplicate.zip'
        with zipfile.ZipFile(source, 'w') as archive:
            archive.writestr('Assets/A.prefab', '1')
            archive.writestr('Assets/a.prefab', '2')
        with self.assertRaisesRegex(ValueError, 'Duplicate archive path'):
            self.library.add(source)
        with patch('wardrobe_library.MAX_BYTES', 20):
            with self.assertRaises(ValueError):
                unpack(self.archive(), self.root / 'too-large')

    def test_update_impact_preserves_old_recipe(self):
        old = self.library.add(self.archive())
        plan = self.library.import_plan(old['hash'], self.project)
        self.library.apply_import(old['hash'], self.project, plan)
        new = self.library.add(self.archive('product-v2.zip', 'new version', 'b'*32))
        impact = self.library.update_impact(old['hash'], new['hash'])
        self.assertIn('Assets/Outfit.prefab', impact['changed'])
        self.assertIn('Assets/Outfit.prefab', impact['guidChanges'])
        self.assertEqual(len(impact['usage']), 1)
        self.assertFalse(impact['automaticUpdate'])
        self.assertEqual((self.project/'Assets/Outfit.prefab').read_text(), 'prefab')

    def test_missing_references_use_imported_and_project_guids(self):
        serialized = '%YAML 1.1\n--- !u!1 &1\n'
        for guid in ('a'*32, 'b'*32, 'c'*32, '0000000000000000f000000000000000'):
            serialized += '  reference: {fileID: 11400000, guid: ' + guid + ', type: 3}\n'
        record = self.library.add(self.archive(value=serialized, guid='A'*32))
        package = self.project/'Library/PackageCache/installed'
        package.mkdir(parents=True)
        (package/'Dependency.cs.meta').write_text('guid: ' + 'C'*32)
        plan = self.library.import_plan(record['hash'], self.project)
        self.assertEqual(plan['missingDependencies'], [{'guid': 'b'*32, 'referencedBy': ['Assets/Outfit.prefab']}])
        self.assertEqual(plan['dependencyScanIncomplete'], [])
        (package/'Other.cs.meta').write_text('guid: ' + 'b'*32)
        with self.assertRaisesRegex(ValueError, 'changed since review'):
            self.library.apply_import(record['hash'], self.project, plan)
        self.assertEqual(self.library.import_plan(record['hash'], self.project)['missingDependencies'], [])

    def test_dependency_scan_reports_limits_instead_of_claiming_complete(self):
        record = self.library.add(self.archive(value='%YAML 1.1\n' + ' '*100 + 'guid: ' + 'b'*32))
        with patch('wardrobe_library.MAX_TEXT_BYTES', 64):
            plan = self.library.import_plan(record['hash'], self.project)
        self.assertIn('Assets/Outfit.prefab', plan['dependencyScanIncomplete'])

    def test_identical_nested_copies_are_imported_once(self):
        source = self.root/'duplicates.zip'
        content = self.archive().read_bytes()
        with zipfile.ZipFile(source, 'w') as archive:
            archive.writestr('One.zip', content)
            archive.writestr('Two.zip', content)
        record = self.library.add(source)
        plan = self.library.import_plan(record['hash'], self.project)
        self.assertEqual(len(plan['files']), 2)
        self.assertEqual(self.library.apply_import(record['hash'], self.project, plan)['copied'], 2)

    def test_conflicting_nested_versions_require_selecting_one(self):
        source = self.root/'versions.zip'
        one = self.archive('one.zip').read_bytes()
        two = self.archive('two.zip', value='changed').read_bytes()
        with zipfile.ZipFile(source, 'w') as archive:
            archive.writestr('One.zip', one)
            archive.writestr('Two.zip', two)
        record = self.library.add(source)
        with self.assertRaisesRegex(ValueError, 'desired nested package separately'):
            self.library.import_plan(record['hash'], self.project)

    def test_empty_unitypackage_folder_keeps_identity(self):
        source = self.root/'empty.unitypackage'
        with tarfile.open(source, 'w:gz') as archive:
            for name, value in [('folder/pathname', b'Assets/Empty'), ('folder/asset.meta', b'guid: ' + b'a'*32 + b'\nfolderAsset: yes\n')]:
                entry = tarfile.TarInfo(name); entry.size = len(value)
                archive.addfile(entry, io.BytesIO(value))
        record = self.library.add(source)
        plan = self.library.import_plan(record['hash'], self.project)
        self.assertTrue(plan['files'][0]['folderAsset'])
        self.library.apply_import(record['hash'], self.project, plan)
        self.assertTrue((self.project/'Assets/Empty').is_dir())
        self.assertIn('a'*32, (self.project/'Assets/Empty.meta').read_text())

    def test_native_plugins_require_code_review(self):
        source = self.root/'native.zip'
        with zipfile.ZipFile(source, 'w') as archive:
            archive.writestr('Assets/Plugins/Mac.framework/Mac', 'binary')
            archive.writestr('Assets/Plugins/Android.jar', 'binary')
            archive.writestr('Assets/Plugins/static.a', 'binary')
        record = self.library.add(source)
        self.assertEqual(self.library.import_plan(record['hash'], self.project)['codeFiles'], 3)

    def test_directory_budget_and_special_files_rejected(self):
        source = self.root/'directories.zip'
        with zipfile.ZipFile(source, 'w') as archive:
            for index in range(4):
                archive.writestr('Folder' + str(index) + '/', '')
        with patch('wardrobe_library.MAX_FILES', 3):
            with self.assertRaisesRegex(ValueError, 'extraction budget'):
                self.library.add(source)
        with zipfile.ZipFile(source, 'w') as archive:
            entry = zipfile.ZipInfo('Assets/device')
            entry.external_attr = (stat.S_IFIFO | 0o644) << 16
            archive.writestr(entry, '')
        with self.assertRaisesRegex(ValueError, 'special files'):
            self.library.add(source)

    def test_internal_symlink_destination_rejected(self):
        record = self.library.add(self.archive())
        (self.project/'Elsewhere').mkdir()
        (self.project/'Assets').symlink_to(self.project/'Elsewhere', target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'uses a link'):
            self.library.import_plan(record['hash'], self.project)

    def test_failed_copy_rolls_back_partial_files_and_new_folders(self):
        record = self.library.add(self.archive())
        plan = self.library.import_plan(record['hash'], self.project)
        def fail_after_write(source, output, length):
            output.write(b'partial')
            raise OSError('simulated disk failure')
        with patch('wardrobe_library.shutil.copyfileobj', side_effect=fail_after_write):
            with self.assertRaisesRegex(OSError, 'disk failure'):
                self.library.apply_import(record['hash'], self.project, plan)
        self.assertFalse((self.project/'Assets').exists())
        self.assertEqual(self.library.list()[0]['usage'], [])

    def test_macos_sidecars_retained_but_never_unpacked_or_imported(self):
        source = self.root/'macos.zip'
        with zipfile.ZipFile(source, 'w') as archive:
            archive.writestr('Assets/Outfit.prefab', 'prefab')
            archive.writestr('Assets/._Outfit.prefab', 'AppleDouble')
            archive.writestr('Assets/.DS_Store', 'Finder')
            archive.writestr('__MACOSX/Assets/Outfit.prefab', 'AppleDouble')
            archive.writestr('__MACOSX/._Product.unitypackage', 'AppleDouble')
        record = self.library.add(source)
        self.assertEqual(len(self.library.record(record['hash'])['files']), 5)
        plan = self.library.import_plan(record['hash'], self.project)
        self.assertEqual([entry['destination'] for entry in plan['files']], ['Assets/Outfit.prefab'])

    def test_project_assets_root_cannot_be_replaced_by_file(self):
        source = self.root/'root-file.zip'
        with zipfile.ZipFile(source, 'w') as archive:
            archive.writestr('Assets', 'not a folder')
        record = self.library.add(source)
        with self.assertRaisesRegex(ValueError, 'replace the project Assets folder'):
            self.library.import_plan(record['hash'], self.project)

    def test_lost_lease_during_copy_rolls_back_created_files(self):
        record = self.library.add(self.archive(value='x' * (3 * 1024 * 1024)))
        plan = self.library.import_plan(record['hash'], self.project)
        def check():
            target = self.project/'Assets/Outfit.prefab'
            if target.exists() and target.stat().st_size >= 1024 * 1024:
                raise ValueError('lease lost')
        with self.assertRaisesRegex(ValueError, 'lease lost'):
            self.library.apply_import(record['hash'], self.project, plan, cancel_check=check)
        self.assertFalse((self.project/'Assets').exists())
        self.assertEqual(self.library.list()[0]['usage'], [])


if __name__ == '__main__':
    unittest.main()
