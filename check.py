import xml.etree.ElementTree as ET

tree = ET.parse('src/RtdDolarNative/MainWindow.xaml')
root = tree.getroot()

ns = 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'
x_ns = 'http://schemas.microsoft.com/winfx/2006/xaml'

for tc in root.iter(f'{{{ns}}}TabControl'):
    if tc.attrib.get(f'{{{x_ns}}}Name') == 'MainTabs':
        i = 0
        for child in tc:
            if child.tag == f'{{{ns}}}TabItem':
                print(f"Index {i} | Tag {child.attrib.get('Tag', 'none')}: {child.attrib.get('Header')}")
                i += 1
