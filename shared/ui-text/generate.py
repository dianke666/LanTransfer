#!/usr/bin/env python3
"""
Generate Android (strings.xml + UiText.kt), Windows (Strings.resx + UiText.g.cs),
and iOS (zh-Hans.lproj/Localizable.strings + UiText.g.swift) resources from
shared/ui-text/zh-CN.tsv.
"""
from __future__ import annotations

import pathlib
import re
import xml.sax.saxutils as saxutils

ROOT = pathlib.Path(__file__).resolve().parents[2]
TSV_PATH = ROOT / "shared" / "ui-text" / "zh-CN.tsv"
PLACEHOLDER_RE = re.compile(r"\{([a-zA-Z_][a-zA-Z0-9_]*)\}")


def load_entries() -> list[dict[str, str]]:
    rows: list[dict[str, str]] = []
    seen: set[str] = set()
    for line_no, raw in enumerate(TSV_PATH.read_text(encoding="utf-8").splitlines(), start=1):
        if line_no == 1 or not raw.strip():
            continue
        parts = raw.split("\t")
        if len(parts) < 5:
            raise ValueError(f"Invalid TSV line {line_no}: {raw!r}")
        key, text, source, status, platform = parts[:5]
        if key in seen:
            raise ValueError(f"Duplicate key: {key}")
        seen.add(key)
        rows.append({
            "key": key,
            "text": text.replace("\\n", "\n"),
            "source": source,
            "status": status,
            "platform": platform,
        })
    return rows


def to_pascal(key: str) -> str:
    return "".join(part.capitalize() for part in key.split("_"))


def to_camel(key: str) -> str:
    parts = key.split("_")
    return parts[0] + "".join(p.capitalize() for p in parts[1:])


def android_escape(text: str) -> str:
    # Escape % first for formatted strings, then convert {name} to %1$s
    names: list[str] = []
    def repl(m: re.Match[str]) -> str:
        name = m.group(1)
        if name not in names:
            names.append(name)
        idx = names.index(name) + 1
        return f"%{idx}$s"

    has_ph = bool(PLACEHOLDER_RE.search(text))
    escaped = text.replace("%", "%%") if has_ph else text
    escaped = PLACEHOLDER_RE.sub(repl, escaped)
    escaped = saxutils.escape(escaped)
    escaped = escaped.replace("\\", "\\\\").replace("'", "\\'").replace('"', '\\"').replace("\n", "\\n")
    if escaped.startswith("@") or escaped.startswith("?"):
        escaped = "\\" + escaped
    return escaped


def dotnet_format(text: str) -> tuple[str, list[str]]:
    names: list[str] = []
    # Escape literal braces first if any (none in current table)
    def repl(m: re.Match[str]) -> str:
        name = m.group(1)
        if name not in names:
            names.append(name)
        idx = names.index(name)
        return "{" + str(idx) + "}"

    formatted = PLACEHOLDER_RE.sub(repl, text)
    return formatted, names


def generate_android(entries: list[dict[str, str]]) -> None:
    android_rows = [e for e in entries if e["platform"] in ("both", "mobile", "android")]
    xml_path = ROOT / "android" / "app" / "src" / "main" / "res" / "values" / "strings.xml"
    xml_path.parent.mkdir(parents=True, exist_ok=True)
    xml_lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        "<!-- 自动生成自 shared/ui-text/zh-CN.tsv，请勿手工修改 -->",
        "<resources>",
    ]
    for row in android_rows:
        has_ph = bool(PLACEHOLDER_RE.search(row["text"]))
        fmt_attr = "" if has_ph else ' formatted="false"'
        xml_lines.append(f'    <string name="{row["key"]}"{fmt_attr}>{android_escape(row["text"])}</string>')
    xml_lines.append("</resources>")
    xml_path.write_text("\n".join(xml_lines) + "\n", encoding="utf-8", newline="\n")

    kt_path = ROOT / "android" / "app" / "src" / "main" / "kotlin" / "com" / "lantransfer" / "app" / "ui" / "text" / "UiText.kt"
    kt_path.parent.mkdir(parents=True, exist_ok=True)
    kt_lines = [
        "// 自动生成自 shared/ui-text/zh-CN.tsv，请勿手工修改。",
        "package com.lantransfer.app.ui.text",
        "",
        "import android.content.Context",
        "import com.lantransfer.app.R",
        "",
        "object UiText {",
    ]
    for row in android_rows:
        key = row["key"]
        fn_name = to_camel(key)
        names: list[str] = []
        for m in PLACEHOLDER_RE.finditer(row["text"]):
            if m.group(1) not in names:
                names.append(m.group(1))
        if not names:
            kt_lines.append(f"    fun {fn_name}(ctx: Context): String = ctx.getString(R.string.{key})")
        else:
            params = ", ".join(f"{n}: Any" for n in names)
            args = ", ".join(f"{n}.toString()" for n in names)
            kt_lines.append(f"    fun {fn_name}(ctx: Context, {params}): String = ctx.getString(R.string.{key}, {args})")
    kt_lines.append("}")
    kt_path.write_text("\n".join(kt_lines) + "\n", encoding="utf-8", newline="\n")


def generate_windows(entries: list[dict[str, str]]) -> None:
    win_rows = [e for e in entries if e["platform"] in ("both", "windows")]
    resx_path = ROOT / "windows" / "src" / "LanTransfer.App" / "Strings.resx"
    resx_path.parent.mkdir(parents=True, exist_ok=True)
    resx_lines = [
        '<?xml version="1.0" encoding="utf-8"?>',
        "<!-- 自动生成自 shared/ui-text/zh-CN.tsv，请勿手工修改 -->",
        "<root>",
        '  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>',
        '  <resheader name="version"><value>2.0</value></resheader>',
        '  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>',
        '  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>',
    ]
    for row in win_rows:
        fmt_text, _ = dotnet_format(row["text"])
        resx_lines.append(f'  <data name="{row["key"]}" xml:space="preserve"><value>{saxutils.escape(fmt_text)}</value></data>')
    resx_lines.append("</root>")
    resx_path.write_text("\n".join(resx_lines) + "\n", encoding="utf-8", newline="\n")

    cs_path = ROOT / "windows" / "src" / "LanTransfer.App" / "UiText.g.cs"
    cs_lines = [
        "// 自动生成自 shared/ui-text/zh-CN.tsv，请勿手工修改。",
        "using System.Globalization;",
        "using System.Resources;",
        "",
        "namespace LanTransfer.App;",
        "",
        "public static class UiText",
        "{",
        '    private static readonly ResourceManager Rm = new("LanTransfer.App.Strings", typeof(UiText).Assembly);',
        '    private static readonly CultureInfo ZhCn = CultureInfo.GetCultureInfo("zh-CN");',
        "",
        '    private static string Get(string key) => Rm.GetString(key, ZhCn) ?? key;',
        "",
    ]
    for row in win_rows:
        key = row["key"]
        prop_name = to_pascal(key)
        _, names = dotnet_format(row["text"])
        if not names:
            cs_lines.append(f'    public static string {prop_name} => Get("{key}");')
        else:
            params = ", ".join(f"object {n}" for n in names)
            args = ", ".join(names)
            cs_lines.append(f'    public static string {prop_name}({params}) => string.Format(ZhCn, Get("{key}"), {args});')
    cs_lines.append("}")
    cs_path.write_text("\n".join(cs_lines) + "\n", encoding="utf-8", newline="\n")


def ios_format(text: str) -> tuple[str, list[str]]:
    names: list[str] = []

    def repl(m: re.Match[str]) -> str:
        name = m.group(1)
        if name not in names:
            names.append(name)
        idx = names.index(name) + 1
        return f"%{idx}$@"

    has_ph = bool(PLACEHOLDER_RE.search(text))
    escaped = text.replace("%", "%%") if has_ph else text
    escaped = PLACEHOLDER_RE.sub(repl, escaped)
    escaped = escaped.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n")
    return escaped, names


def generate_ios(entries: list[dict[str, str]]) -> None:
    ios_rows = [e for e in entries if e["platform"] in ("both", "mobile", "ios")]
    strings_path = ROOT / "ios" / "LanTransferApp" / "Resources" / "zh-Hans.lproj" / "Localizable.strings"
    strings_path.parent.mkdir(parents=True, exist_ok=True)
    strings_lines = [
        "/* 自动生成自 shared/ui-text/zh-CN.tsv，请勿手工修改 */",
        "",
    ]
    for row in ios_rows:
        escaped, _ = ios_format(row["text"])
        strings_lines.append(f'"{row["key"]}" = "{escaped}";')
    strings_path.write_text("\n".join(strings_lines) + "\n", encoding="utf-8", newline="\n")

    swift_path = ROOT / "ios" / "LanTransferApp" / "UI" / "Text" / "UiText.g.swift"
    swift_path.parent.mkdir(parents=True, exist_ok=True)
    swift_lines = [
        "// 自动生成自 shared/ui-text/zh-CN.tsv，请勿手工修改。",
        "import Foundation",
        "",
        "public enum UiText: Sendable {",
        "    private static let zhHansBundle: Bundle = {",
        '        if let path = Bundle.main.path(forResource: "zh-Hans", ofType: "lproj"),',
        "           let bundle = Bundle(path: path) {",
        "            return bundle",
        "        }",
        "        return Bundle.main",
        "    }()",
        "",
        '    private static let zhHansLocale = Locale(identifier: "zh-Hans-CN")',
        "",
        "    private static func lookup(_ key: String) -> String {",
        '        zhHansBundle.localizedString(forKey: key, value: key, table: "Localizable")',
        "    }",
        "",
        "    private static func format(_ key: String, _ args: [CVarArg]) -> String {",
        "        String(format: lookup(key), locale: zhHansLocale, arguments: args)",
        "    }",
        "",
    ]
    for row in ios_rows:
        key = row["key"]
        prop_name = to_camel(key)
        _, names = ios_format(row["text"])
        if not names:
            swift_lines.append(f'    public static var {prop_name}: String {{ lookup("{key}") }}')
        else:
            params = ", ".join(f"{n}: Any" for n in names)
            args = ", ".join(f"String(describing: {n}) as NSString" for n in names)
            swift_lines.append(f'    public static func {prop_name}({params}) -> String {{ format("{key}", [{args}]) }}')
    swift_lines.append("}")
    swift_path.write_text("\n".join(swift_lines) + "\n", encoding="utf-8", newline="\n")


def main() -> None:
    entries = load_entries()
    generate_android(entries)
    generate_windows(entries)
    generate_ios(entries)
    print(f"Generated Android, Windows, and iOS resources from {len(entries)} entries.")


if __name__ == "__main__":
    main()
