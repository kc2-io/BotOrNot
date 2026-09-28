#!/usr/bin/env python3
"""Apply BotOrNot's reviewed parser patch to its pinned upstream revision."""

from pathlib import Path
import subprocess
import sys


PINNED_UPSTREAM_COMMIT = "2fc699e99cf8f6654f13fcc5272ea1de57d89fd7"
PATCH_FILE = Path(__file__).with_name("fortnite-replay-reader-botornot.patch")


def run(*args: str) -> None:
    subprocess.run(args, check=True)


def main() -> None:
    if len(sys.argv) != 2:
        print(f"Usage: {sys.argv[0]} <repo-root>")
        raise SystemExit(1)

    repo = Path(sys.argv[1]).resolve()
    try:
        actual_commit = subprocess.check_output(
            ["git", "-C", str(repo), "rev-parse", "HEAD"], text=True
        ).strip()
    except (OSError, subprocess.CalledProcessError) as exc:
        print(f"ERROR: Could not verify upstream checkout: {exc}")
        raise SystemExit(1) from exc

    if actual_commit != PINNED_UPSTREAM_COMMIT:
        print("ERROR: Refusing to patch an unexpected upstream revision")
        print(f"  Expected: {PINNED_UPSTREAM_COMMIT}")
        print(f"  Actual:   {actual_commit}")
        raise SystemExit(1)

    if not PATCH_FILE.is_file():
        print(f"ERROR: Missing parser patch: {PATCH_FILE}")
        raise SystemExit(1)

    print(f"Applying {PATCH_FILE.name} to {actual_commit}...")
    try:
        run("git", "-C", str(repo), "apply", "--check", str(PATCH_FILE))
        run("git", "-C", str(repo), "apply", "--whitespace=nowarn", str(PATCH_FILE))
    except (OSError, subprocess.CalledProcessError) as exc:
        print(f"ERROR: Parser patch did not apply cleanly: {exc}")
        raise SystemExit(1) from exc

    print("All BotOrNot parser fixes applied successfully.")


if __name__ == "__main__":
    main()
