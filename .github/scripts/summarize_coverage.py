#!/usr/bin/env python3
"""Render a Cobertura report as a short GitHub step summary.

Reported, never gated. Coverage left this repository in the OSS cut with no
replacement, so a contributor could not see what a change dropped; this restores
the signal without adding a threshold that could red a PR before anyone has
measured a baseline.

Reads the report path given as argv[1] and writes Markdown to stdout. A missing
or unparseable report is reported as such and still exits 0 -- the coverage
summary must not fail a build whose tests passed.
"""

import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path


def main():
    if len(sys.argv) < 2:
        print("### Coverage\n\nNo report path supplied.")
        return 0

    report = Path(sys.argv[1])
    if not report.is_file():
        print(f"### Coverage\n\nNo coverage report at `{report}`.")
        return 0

    try:
        root = ElementTree.parse(report).getroot()
    except ElementTree.ParseError as error:
        print(f"### Coverage\n\nCoverage report at `{report}` is not parseable: {error}")
        return 0

    def rate(attribute):
        try:
            return float(root.get(attribute, "0")) * 100
        except ValueError:
            return 0.0

    lines = ["### Coverage", "", "| Metric | Covered |", "| --- | --- |"]
    lines.append(f"| Line | {rate('line-rate'):.2f}% |")
    lines.append(f"| Branch | {rate('branch-rate'):.2f}% |")
    lines.append("")
    lines.append("Reported only - no threshold is enforced.")
    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    sys.exit(main())
