import json
from pathlib import Path
import sys
import unittest

INDEXER = Path(__file__).resolve().parents[1] / 'Packages/dev.gryphprime.avatar-wardrobe/Editor/WardrobeIndexer'
sys.path.insert(0, str(INDEXER))
from wardrobe_parse import parse_prefab_text
from wardrobe_index import Indexer

class ParserRegressions(unittest.TestCase):
    def test_signed_local_identifiers(self):
        for go, bone in [('101','201'),('-101','-201'),('-9223372036854775807','9223372036854775807')]:
            with self.subTest(go=go):
                text=f'''%YAML 1.1
--- !u!1 &{go}
GameObject:
  m_Name: Jacket
--- !u!1 &301
GameObject:
  m_Name: Hips
--- !u!4 &{bone}
Transform:
  m_GameObject: {{fileID: 301}}
--- !u!137 &401
SkinnedMeshRenderer:
  m_GameObject: {{fileID: {go}}}
  m_RootBone: {{fileID: {bone}}}
'''
                parsed=parse_prefab_text(text)
                self.assertEqual(['Jacket'],parsed['renderer_names'])
                self.assertEqual(['hips'],parsed['bones'])
        self.assertEqual([], parse_prefab_text('%YAML 1.1\n--- !u!137 &1\nSkinnedMeshRenderer:\n  m_RootBone: {fileID: 0}\n')['bones'])

    def test_nested_multiplicity_preserves_unique_dependencies(self):
        config=json.loads((INDEXER/'wardrobe_config.json').read_text())
        indexer=Indexer('.',config)
        child='a'*32
        nested=lambda n: ''.join(f'--- !u!1001 &{i+10}\nPrefabInstance:\n  m_SourcePrefab: {{fileID: -100100000, guid: {child}}}\n' for i in range(n))
        indexer.guid_to_path[child]='child.prefab'
        indexer.local['child.prefab']=parse_prefab_text('%YAML 1.1\n--- !u!23 &1\nMeshRenderer:\n')
        indexer.local['parent.prefab']=parse_prefab_text('%YAML 1.1\n'+nested(2))
        value=indexer.resolve('parent.prefab')
        self.assertEqual(2,value['renderers'])
        self.assertEqual({child},value['sources'])
        self.assertEqual(1,indexer.memo['child.prefab']['renderers'])

    def test_cycle_terminates(self):
        config=json.loads((INDEXER/'wardrobe_config.json').read_text())
        indexer=Indexer('.',config)
        for name,guid,other in [('a','a'*32,'b'*32),('b','b'*32,'a'*32)]:
            indexer.guid_to_path[guid]=name+'.prefab'
            indexer.local[name+'.prefab']=parse_prefab_text(f'%YAML 1.1\n--- !u!1001 &1\nPrefabInstance:\n  m_SourcePrefab: {{fileID: 1, guid: {other}}}\n')
        self.assertIsNotNone(indexer.resolve('a.prefab'))
