#!/usr/bin/env python3
"""Resolve the commit whose committed policy is the comparison base.

Pull requests and merge-queue runs must name a usable base. A missing base on
those events fails closed instead of opening the first-baseline path. A push
before a branch's first commit has no base, so an all-zero push SHA is the only
case that resolves to an empty base.
"""

import argparse
import sys


def resolve(event, pull_request_base, merge_group_base, push_before):
    event = (event or "").strip()
    if event in ("pull_request", "merge_group"):
        value = (pull_request_base if event == "pull_request" else merge_group_base) or ""
        value = value.strip()
        if not value or set(value) == {"0"}:
            raise ValueError(
                f"{event} event has no usable base SHA; refusing to open the "
                "first-baseline path")
        return value
    if event == "push":
        value = (push_before or "").strip()
        if not value or set(value) == {"0"}:
            return ""
        return value
    raise ValueError(f"unsupported event {event!r}; cannot resolve a policy base")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--event", required=True)
    parser.add_argument("--pull-request-base", default="")
    parser.add_argument("--merge-group-base", default="")
    parser.add_argument("--push-before", default="")
    args = parser.parse_args()
    try:
        print(resolve(args.event, args.pull_request_base, args.merge_group_base,
                      args.push_before))
    except ValueError as error:
        print(f"::error::{error}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
