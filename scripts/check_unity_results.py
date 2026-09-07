"""Reject missing, empty, skipped or failed Unity regression runs."""
import sys
import xml.etree.ElementTree as ET
root=ET.parse(sys.argv[1]).getroot()
cases=[c for c in root.iter('test-case') if 'ReviewRegressionTests.' in c.get('fullname','')]
if len(cases)<6 or any(c.get('result')!='Passed' for c in cases):
    raise SystemExit('Unity review regressions did not all pass')
if root.get('result')!='Passed' or int(root.get('failed','0')):
    raise SystemExit('Unity test run failed')
print(f'Unity compilation and {len(cases)} review regressions passed')
