"""Validate community RESX files against English; missing entries intentionally fall back."""
import argparse
import pathlib
import re
import xml.etree.ElementTree as ET


def placeholders(value):
    result = []
    index = 0
    while index < len(value):
        if value[index:index + 2] in ('{{', '}}'):
            index += 2
        elif value[index] == '{':
            match = re.match(r'\{\d+(?:,\s*-?\d+)?(?::[^{}]+)?\}', value[index:])
            if not match:
                raise ValueError('invalid composite-format placeholder')
            result.append(match.group())
            index += len(match.group())
        elif value[index] == '}':
            raise ValueError('unescaped closing brace')
        else:
            index += 1
    return sorted(result)


def read_resx(path):
    result = {}
    root = ET.parse(path).getroot()
    if root.tag != 'root':
        raise ValueError('expected RESX root')
    for entry in root.findall('data'):
        key = entry.get('name')
        value = entry.find('value')
        if not key or key in result or value is None or entry.get('type') or entry.get('mimetype'):
            raise ValueError('invalid, duplicate, or non-string resource entry')
        text = ''.join(value.itertext())
        if not text.strip():
            raise ValueError(f'{key}: empty value; omit untranslated entries instead')
        result[key] = text
    return result


def validate_catalog(english, translated):
    for key, value in translated.items():
        if key not in english:
            raise ValueError(f'{key}: unknown English key')
        if not value.strip():
            raise ValueError(f'{key}: empty translation')
        if placeholders(value) != placeholders(english[key]):
            raise ValueError(f'{key}: format placeholders must match English exactly')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--resources', type=pathlib.Path,
                        default=pathlib.Path(__file__).resolve().parents[1] / 'src/Colibri.App/Resources')
    args = parser.parse_args()
    english = read_resx(args.resources / 'Strings.resx')
    for value in english.values():
        placeholders(value)
    translations = sorted(args.resources.glob('Strings.*.resx'))
    for path in translations:
        culture = path.name[len('Strings.'):-len('.resx')]
        if not re.fullmatch(r'[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*', culture):
            raise ValueError(f'{path.name}: invalid culture filename')
        validate_catalog(english, read_resx(path))
    print(f'Validated English and {len(translations)} translation catalogs; missing keys use English fallback.')


if __name__ == '__main__':
    main()
