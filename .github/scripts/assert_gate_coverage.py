#!/usr/bin/env python3
"""Fails when a job in the given workflow is not accounted for by the CI Gate.

The gate is a required status check, so it decides what can merge -- but it only
aggregates the jobs listed in its `needs:`. A quality job added later and never wired in
is silently not merge-blocking, and nothing about adding it prompts anyone to notice.
This asserts the wiring instead of trusting it.

A job is accounted for when it is in the gate's `needs:`, or named in GATE_EXEMPT.
Exemption is an explicit job-id list rather than an inferred rule, because a decision
that grants itself automatically is not reviewable.

Pure Python, invoked directly rather than through a shell wrapper. The wrapper used to be
bash, which cannot survive CRLF line endings: a repo whose .gitattributes checks the file
out with CRLF got `set: pipefail: invalid option name` and a permanently red required
check. Python does not care about CRLF, so the failure mode is designed out rather than
patched per repo.
"""
import os
import re
import sys


ID = r"[A-Za-z_][A-Za-z0-9_-]*"
# A job key may be quoted -- `"security-scan":` is valid Actions syntax. The previous
# unquoted-only pattern silently dropped such a job from the `jobs` dict, so it could
# never appear in `set(jobs) - set(needs)` and the gate reported full coverage over a
# quality job it was not gating. That is a fail-OPEN result, the one outcome this
# script exists to prevent, so anything unclassifiable at job indentation now exits.
JOB = re.compile(rf"""^\ \ (?:'({ID})'|"({ID})"|({ID}))\s*:\s*(?:\#.*)?$""", re.VERBOSE)
NEEDS = re.compile(r"^    needs\s*:\s*(.*?)\s*$")
# Block sequence items may sit at the key's own indentation (4) or be indented under it
# (6), and may be quoted. Both forms are valid YAML; accepting only unquoted 6-space
# items dropped entries, which ENLARGES `missing` and reddens a valid workflow.
BLOCK_NEED = re.compile(rf"""^\s{{4,}}-\s*(?:'({ID})'|"({ID})"|({ID}))\s*(?:\#.*)?$""", re.VERBOSE)
COMMENT_OR_BLANK = re.compile(r"^\s*(?:\#.*)?$")


def _first_group(match):
    return next(g for g in match.groups() if g is not None)


def strip_comment(line):
    """Drop a trailing comment. Naive by design: a '#' inside a quoted scalar is not a
    shape this file's job/needs grammar admits, and guessing at YAML quoting rules here
    would be less predictable than the explicit exit below."""
    return line.split("#", 1)[0]


def parse_need_ids(value):
    value = strip_comment(value).strip()
    if value.startswith("[") and value.endswith("]"):
        values = value[1:-1].split(",")
    else:
        values = [value]
    ids = [item.strip().strip("'\"") for item in values if item.strip()]
    if any(not re.fullmatch(ID, item) for item in ids):
        sys.exit(f"unsupported needs value: {value}")
    return ids


def read_gate_contract(lines, gate_job):
    try:
        # `jobs: # comment` is valid and used to fail the equality outright, returning no
        # jobs at all.
        jobs_start = next(
            i for i, line in enumerate(lines) if strip_comment(line).rstrip() == "jobs:"
        )
    except StopIteration:
        return {}, []

    jobs = {}
    for i in range(jobs_start + 1, len(lines)):
        line = lines[i].rstrip("\r\n")
        if line.strip() and not line.startswith((" ", "#")):
            break
        if COMMENT_OR_BLANK.match(line):
            continue
        indent = len(line) - len(line.lstrip(" "))
        if indent != 2:
            continue
        match = JOB.match(line)
        if not match:
            # Fail closed. An unrecognised construct at job indentation (an anchor, a
            # merge key, a multi-line key) means this parser does not understand the
            # workflow, and "does not understand" must never render as "all accounted
            # for".
            sys.exit(
                f"unparsable line at job indentation (line {i + 1}): {line.strip()}\n"
                "This gate refuses to report coverage it cannot verify. Simplify the job "
                "key, or extend assert_gate_coverage.py to understand this form."
            )
        jobs[_first_group(match)] = i

    if gate_job not in jobs:
        return jobs, []

    gate_start = jobs[gate_job] + 1
    gate_end = min((i for i in jobs.values() if i >= gate_start), default=len(lines))
    for i in range(gate_start, gate_end):
        match = NEEDS.match(lines[i].rstrip("\r\n"))
        if not match:
            continue
        if strip_comment(match.group(1)).strip():
            return jobs, parse_need_ids(match.group(1))

        needs = []
        for line in lines[i + 1 : gate_end]:
            line = line.rstrip("\r\n")
            item = BLOCK_NEED.match(line)
            if item:
                needs.append(_first_group(item))
                continue
            # Skip comments and blanks BEFORE testing indentation: a comment sitting at
            # the key's own indentation used to end the sequence early and silently
            # truncate `needs`.
            if COMMENT_OR_BLANK.match(line):
                continue
            if len(line) - len(line.lstrip(" ")) <= 4:
                break
        return jobs, needs

    return jobs, []


def main(argv):
    if len(argv) < 2:
        sys.exit("usage: assert_gate_coverage.py <workflow-file> [gate-job-id]")

    workflow_path = argv[1]
    gate_job = argv[2] if len(argv) > 2 else os.environ.get("GATE_JOB", "ci-gate")

    with open(workflow_path, encoding="utf-8") as handle:
        jobs, needs = read_gate_contract(handle.readlines(), gate_job)

    if not jobs:
        sys.exit(f"{workflow_path}: no jobs found -- refusing to report coverage over nothing.")
    if gate_job not in jobs:
        sys.exit(f"{workflow_path}: no '{gate_job}' job found.")

    exempt = set(os.environ.get("GATE_EXEMPT", "").replace(",", " ").split())

    # A stale exemption is worse than a missing one: it reads as a deliberate decision
    # while covering nothing, and it survives the rename that made it meaningless.
    unknown = sorted(exempt - set(jobs))
    if unknown:
        sys.exit(f"{workflow_path}: GATE_EXEMPT names jobs that do not exist: {', '.join(unknown)}")

    missing = sorted(set(jobs) - set(needs) - exempt - {gate_job})
    if missing:
        sys.exit(
            f"{workflow_path}: not gated by '{gate_job}': {', '.join(missing)}.\n"
            f"Add each to the '{gate_job}' needs: list, or to GATE_EXEMPT if it is "
            "deliberately not merge-blocking."
        )

    print(f"{workflow_path}: all {len(jobs)} job(s) accounted for by '{gate_job}'.")


if __name__ == "__main__":
    main(sys.argv)
